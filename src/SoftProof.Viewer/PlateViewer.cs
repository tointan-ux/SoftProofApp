using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoftProof.Core.Interfaces;
using SoftProof.Core.Models;

namespace SoftProof.Viewer;

/// <summary>A native-resolution request: image region (base-level pixels) and decimation factor (1, 2, 4).</summary>
public sealed record DetailRequest(Rect Region, int Factor);

/// <summary>Active mouse tool in the viewer.</summary>
public enum ViewerTool
{
    Select,
    Pan,
    Densitometer,
    Measure
}

/// <summary>How the plates are combined for display.</summary>
public enum PlateViewMode
{
    /// <summary>Plates multiplied onto white paper in their ink colours.</summary>
    Composite,
    /// <summary>Plates shown as black film on white.</summary>
    FilmBlackWhite,
    /// <summary>Only the first N visible plates (press-sequence preview).</summary>
    Progressive
}

public sealed class RectangleMeasuredEventArgs : EventArgs
{
    public RectangleMeasuredEventArgs(Rect bounds, bool completed)
    {
        Bounds = bounds;
        Completed = completed;
    }

    /// <summary>Measured rectangle in plate pixels, normalised to positive width/height.</summary>
    public Rect Bounds { get; }

    /// <summary>True when the drag has finished.</summary>
    public bool Completed { get; }
}

public sealed class CaliperEventArgs : EventArgs
{
    public CaliperEventArgs(Point start, Point end, bool completed)
    {
        Start = start;
        End = end;
        Completed = completed;
    }
    
    /// <summary>Start point in image pixels.</summary>
    public Point Start { get; }
    /// <summary>End point in image pixels.</summary>
    public Point End { get; }
    public bool Completed { get; }
}

/// <summary>
/// Raster viewer that composites 8-bit plate coverage into a single bitmap, with zoom / pan,
/// a cursor readout, a densitometer aperture marker and a caliper measuring tool.
/// Image coordinates are in plate pixels; the host converts them to millimetres.
/// </summary>
public class PlateViewer : FrameworkElement
{
    private const double MinZoom = 0.01;
    // Zoom above 100 % native is disabled. View scale 1.0 means 100 % of the *base* level,
    // so the cap in view units is the plate's native factor (8 for a 2400 dpi plate on a
    // 300 dpi base).
    private double MaxZoom => Math.Max(1.0, _nativeFactor);
    
    private IReadOnlyList<PlateLayer> _plates = Array.Empty<PlateLayer>();
    private int _width;
    private int _height;
    private WriteableBitmap? _bitmap;
    private byte[]? _buffer;

    // Level being rendered: level 0 is the plate's base coverage; coarser levels are drawn for
    // zoomed-out views so compositing works on far fewer pixels.
    private int _levelIndex;
    private int _renderWidth;
    private int _renderHeight;

    // ICC soft proof: an RGB render of the same plates, produced by the colour engine.
    private BitmapSource? _proofedBitmap;
    private Func<BitmapData, CancellationToken, Task<BitmapData>>? _proofProvider;
    private int _renderStep = 1;        // base pixels per level pixel
    private int _renderLevelWidth;      // level bitmap width (sampling stride)
    private int _renderLevelHeight;
    private int _renderX0, _renderY0;    // level-space origin of the rendered window
    private Rect _renderRect = Rect.Empty; // base-space rect covered by _bitmap
    private bool _switchingLevel;

    // Native (full-resolution) detail layer, supplied by the host when the user zooms past the
    // coarse level. The viewer only knows about a bitmap and the image rectangle it covers.
    private int _nativeFactor = 1;
    private int _detailFactor = 1;
    private WriteableBitmap? _detailBitmap;
    private Rect _requestedDetailRegion = Rect.Empty;
    private Rect _detailRect = Rect.Empty;
    private bool _refining;

    // Content-box overlay: plate centre cross, content box, margin dimensions and offset.
    private Rect _contentBounds = Rect.Empty;
    private bool _showContentOverlay;

    // Simulation and orientation.
    private readonly PlateOrientation _orientation = new();
    private bool _simulateMetallicPaper;
    private Color _paperColor = Colors.White;
    private double _backSideOpacity;

    /// <summary>Raised when the visible region needs native-resolution pixels.</summary>
    public event EventHandler<DetailRequest>? DetailRequested;

    /// <summary>Layers currently shown (used by the host to decode native tiles per pane).</summary>
    public IReadOnlyList<PlateLayer> Plates => _plates;
    
    /// <summary>
    /// The camera — the single source of truth for zoom and pan, in native plate pixels.
    /// The render matrix is derived from it, so <c>Zoom</c>, the readout, hit-testing and
    /// the detail requests can no longer disagree about what 100 % means.
    /// </summary>
    private ViewTransform _camera = new(1.0, 0.5, 0.5, 1.0, 1.0);

    private Matrix _view => BuildViewMatrix();

    /// <summary>Derive the base-pixel → DIP matrix from <see cref="_camera"/>.</summary>
    private Matrix BuildViewMatrix() => MatrixFromCamera(_camera, _nativeFactor);

    /// <summary>
    /// Adopt a mutated matrix as the camera. The camera is expressed in the current viewport
    /// size, otherwise its centre would be measured against a stale frame and only the scale
    /// would be meaningful.
    /// </summary>
    private void SetCamera(Matrix m)
    {
        var w = ActualWidth > 0 ? ActualWidth : _camera.ViewportWidth;
        var h = ActualHeight > 0 ? ActualHeight : _camera.ViewportHeight;

        var next = CameraFromMatrix(m, _nativeFactor, Math.Max(1.0, w), Math.Max(1.0, h));
        if (next.HasValue)
            _camera = next.Value;
    }

    /// <summary>
    /// Base-pixel → DIP matrix for a camera. The camera is native-relative
    /// (DIP per native pixel) while base bitmaps are decimated by <paramref name="nativeFactor"/>.
    /// </summary>
    internal static Matrix MatrixFromCamera(ViewTransform camera, int nativeFactor)
    {
        var s = camera.NativeScale;                       // DIP per native pixel
        var factor = Math.Max(1, nativeFactor);           // native pixels per base pixel

        return new Matrix(s * factor, 0, 0, s * factor,
            camera.ViewportWidth / 2.0 - camera.CentreX * s,
            camera.ViewportHeight / 2.0 - camera.CentreY * s);
    }

    /// <summary>Inverse of <see cref="MatrixFromCamera"/>; null when the matrix has no usable scale.</summary>
    internal static ViewTransform? CameraFromMatrix(Matrix m, int nativeFactor,
                                                    double viewportWidth, double viewportHeight)
    {
        var factor = Math.Max(1, nativeFactor);
        var s = m.M11 / factor;
        if (!(s > 0) || !(viewportWidth > 0) || !(viewportHeight > 0))
            return null;

        return new ViewTransform(s,
            (viewportWidth / 2.0 - m.OffsetX) / s,
            (viewportHeight / 2.0 - m.OffsetY) / s,
            viewportWidth, viewportHeight);
    }

    /// <summary>The base-pixel → DIP matrix (kept private; use the camera for state).</summary>
    private Matrix ViewMatrix => _view;

    // Device-pixel render target: the composited view is built at the monitor's physical
    // resolution and blitted 1:1, so WPF never resamples it.
    private WriteableBitmap? _deviceBitmap;
    private byte[]? _devicePixels;
    private int _deviceWidth;
    private int _deviceHeight;
    private bool _fitPending;
    
    private bool _panning;
    private Point _panLast;
    
    private bool _measuring;
    private bool _selecting;
    private Point? _selectStartScreen;
    private Point? _selectCurrentScreen;
    private bool _measureRectangleMode;
    private bool _measureRectangle;
    private Point? _caliperStart;
    private Point? _caliperEnd;
    
    private Point? _cursorScreen;
    private Point? _lastCanvasPoint;
    private ViewerTool _tool = ViewerTool.Select;
    private PlateViewMode _viewMode = PlateViewMode.Composite;
    private int _progressiveCount = int.MaxValue;
    
    public PlateViewer()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Arrow;

        _detailDebounce.Tick += (_, _) =>
        {
            _detailDebounce.Stop();
            MaybeRequestDetail();
        };

        _compositeDebounce.Tick += (_, _) =>
        {
            _compositeDebounce.Stop();
            if (!_switchingLevel)
                Recomposite();
        };
    }

    // Full-resolution requests are debounced so dragging/zooming stays on the cached preview.
    private readonly DispatcherTimer _detailDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _suppressSchedule;
    // Re-window the composite shortly after the view settles (never a full-image canvas).
    private readonly DispatcherTimer _compositeDebounce = new() { Interval = TimeSpan.FromMilliseconds(60) };
    
    /// <summary>Raised whenever zoom or pan changes.</summary>
    public event EventHandler? ViewChanged;
    /// <summary>Raised when the cursor moves over the image (image pixel coordinates) or leaves (null).</summary>
    public event EventHandler<Point?>? CursorMoved;
    /// <summary>Raised while a caliper measurement is dragged and when it completes.</summary>
    public event EventHandler<CaliperEventArgs>? CaliperChanged;

    /// <summary>Raised instead of <see cref="CaliperChanged"/> while rectangle measurement is on.</summary>
    public event EventHandler<RectangleMeasuredEventArgs>? RectangleChanged;
    
    public int ImageWidth => _width;
    public int ImageHeight => _height;
    public bool HasImage => _bitmap != null;
    
    /// <summary>Screen pixels per image pixel (1.0 = 100%).</summary>
    public double Zoom => _view.M11;
    public double OffsetX => _view.OffsetX;
    public double OffsetY => _view.OffsetY;
    
    /// <summary>Image pixels per millimetre; used for the caliper label.</summary>
    public double PixelsPerMm { get; set; } = 10.0;
    
    /// <summary>Densitometer aperture in image pixels (1, 3 or 5).</summary>
    public int ApertureSize { get; set; } = 1;

    /// <summary>When true, the Measure tool drags a rectangle and reports width × height / area.</summary>
    public bool RectangleMeasure
    {
        get => _measureRectangleMode;
        set
        {
            if (_measureRectangleMode == value) return;
            _measureRectangleMode = value;
            InvalidateVisual();
        }
    }
    
    public ViewerTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            _tool = value;
            Cursor = value switch
            {
                ViewerTool.Pan => Cursors.Hand,
                ViewerTool.Densitometer => Cursors.Cross,
                ViewerTool.Measure => Cursors.Cross,
                _ => Cursors.Arrow
            };
            InvalidateVisual();
        }
    }
    
    public PlateViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (_viewMode == value) return;
            _viewMode = value;
            Refresh();
        }
    }
    
    /// <summary>For <see cref="PlateViewMode.Progressive"/>: how many plates (in job order) to show.</summary>
    public int ProgressiveCount
    {
        get => _progressiveCount;
        set
        {
            if (_progressiveCount == value) return;
            _progressiveCount = value;

            if (_viewMode == PlateViewMode.Progressive)
                Refresh();
        }
    }
    
    // ------------------------------------------------------------------------------------------
    // Data
    // ------------------------------------------------------------------------------------------
    
    /// <summary>Replace the displayed plates. All plates must share the given size.</summary>
    public void SetPlates(IReadOnlyList<PlateLayer> plates, int width, int height)
    {
        _plates = plates;
        _width = width;
        _height = height;
        _levelIndex = 0;
        _caliperStart = _caliperEnd = null;
        _nativeFactor = Math.Max(1, plates.Select(p => p.Pyramid?.Base.Factor ?? 1).DefaultIfEmpty(1).Max());
        _detailBitmap = null;
        _detailRect = Rect.Empty;

        EnsureLevelBitmap();

        Recomposite();
        
        if (ActualWidth > 0 && ActualHeight > 0)
            FitToView();
        else
            _fitPending = true;
    }
    
    public void Clear()
    {
        _plates = Array.Empty<PlateLayer>();
        _width = _height = 0;
        _bitmap = null;
        _buffer = null;
        _levelIndex = 0;
        _renderWidth = _renderHeight = 0;
        _caliperStart = _caliperEnd = null;
        InvalidateVisual();
    }

    /// <summary>Level currently rendered (0 = the plate's full decoded coverage).</summary>
    public int LevelIndex => _levelIndex;

    /// <summary>Width in pixels of the level being drawn (base width at level 0).</summary>
    public int RenderWidth => _renderWidth;

    /// <summary>True while a proofed render is what the viewer draws.</summary>
    public bool ProofEnabled => _proofedBitmap is not null;

    /// <summary>Why the last proof attempt produced nothing, for the caller to report.</summary>
    public string? ProofFailure { get; private set; }

    /// <summary>
    /// Renders the visible plates through <paramref name="proof"/> (normally the ICC proof
    /// transform) and shows that instead of the screen composite. Returns false when there is
    /// nothing to proof or the provider could not produce an image.
    /// </summary>
    public async Task<bool> EnableProofAsync(
        Func<BitmapData, CancellationToken, Task<BitmapData>> proof,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proof);

        _proofProvider = proof;

        return await RenderProofAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Drops the proofed render and goes back to the screen composite.</summary>
    public void DisableProof()
    {
        _proofProvider = null;
        _proofedBitmap = null;
        InvalidateVisual();
    }

    private async Task<bool> RenderProofAsync(CancellationToken cancellationToken)
    {
        if (_proofProvider is null || _renderWidth <= 0 || _renderHeight <= 0)
            return false;

        var layers = GetActiveLayers();
        if (layers.Count == 0)
            return false;

        var count = layers.Count;
        var coverage = new byte[count][];
        var colours = new SeparationColor[count];
        var inverted = new bool[count];
        var opacity = new double[count];

        for (var i = 0; i < count; i++)
        {
            coverage[i] = LevelOf(layers[i], _levelIndex)?.Coverage ?? layers[i].Coverage;
            colours[i] = layers[i].Separation.Color;
            inverted[i] = layers[i].Inverted;
            opacity[i] = layers[i].Opacity;
        }

        // A spot-only job has no CMYK channels: the transform would produce bare paper, which
        // looks like a rendering bug. Say so instead of drawing a blank plate.
        if (ProofComposer.ProcessChannelCount(colours) == 0)
        {
            ProofFailure = "soft proof needs process CMYK plates; this job only has spot inks";
            System.Diagnostics.Debug.WriteLine(ProofFailure);
            return false;
        }

        var cmyk = ProofComposer.BuildCmyk(coverage, colours, inverted, opacity,
                                           _renderWidth, _renderHeight);

        var proofed = await _proofProvider(cmyk, cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();

        _proofedBitmap = ProofComposer.ToBgra(proofed);
        ProofFailure = null;
        InvalidateVisual();
        return true;
    }

    private int MaxLevelIndex
    {
        get
        {
            if (_plates.Count == 0)
                return 0;

            var levels = _plates.Min(p => p.Pyramid?.Levels.Count ?? 1) - 1;
            return Math.Max(0, levels);
        }
    }

    private static PyramidLevel? LevelOf(PlateLayer plate, int index) =>
        plate.Pyramid is { } pyramid && index >= 0 && index < pyramid.Levels.Count
            ? pyramid.Levels[index]
            : null;

    /// <summary>Chooses the level for the current zoom and rebuilds the bitmap if it changed.</summary>
    private void ApplyZoomLevel(bool force)
    {
        if (_switchingLevel || _width == 0)
            return;

        var pyramid = _plates.Select(p => p.Pyramid).FirstOrDefault(p => p is not null);
        var desired = Math.Clamp(pyramid?.IndexForZoom(Zoom) ?? 0, 0, MaxLevelIndex);

        if (!force && desired == _levelIndex)
            return;

        _switchingLevel = true;
        try
        {
            _levelIndex = desired;
            EnsureLevelBitmap();
            Recomposite();
        }
        finally
        {
            _switchingLevel = false;
        }
    }

    private void EnsureLevelBitmap()
    {
        var level = _plates.Select(p => LevelOf(p, _levelIndex)).FirstOrDefault(l => l is not null);
        _renderLevelWidth = level?.Width ?? _width;
        _renderLevelHeight = level?.Height ?? _height;
        _renderStep = 1 << Math.Max(0, _levelIndex);
        PrepareRenderRect();
    }

    /// <summary>
    /// Size the composite to the visible viewport (plus a margin), never to the full plate.
    /// The bitmap is a window into the level coverage, exactly like a tile canvas.
    /// </summary>
    private void PrepareRenderRect()
    {
        if (_width <= 0 || _height <= 0 || _renderLevelWidth <= 0 || _renderLevelHeight <= 0)
        {
            _bitmap = null;
            _buffer = null;
            _renderRect = Rect.Empty;
            return;
        }

        // Full-level composite (known-good renderer): the viewport-window experiment painted
        // white over inked areas at 1:1, so the canvas covers the whole level again.
        var w = _renderLevelWidth;
        var h = _renderLevelHeight;

        if (_bitmap is null || _renderWidth != w || _renderHeight != h)
        {
            _buffer = new byte[checked(w * h * 4)];
            _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        }

        _renderWidth = w;
        _renderHeight = h;
        _renderX0 = 0;
        _renderY0 = 0;
        _renderRect = new Rect(0, 0, _width, _height);
    }

    /// <summary>Re-composite after plate flags (visible / solo / inverted) changed.</summary>
    /// <summary>Recomposite from the current plate state and re-decode the native detail.</summary>
    public void Refresh()
    {
        // Manual re-composite bypasses the debounce: draw the 300 dpi base level and fetch
        // full resolution for the viewport now.
        _detailDebounce.Stop();
        _detailBitmap = null;
        _detailRect = Rect.Empty;
        _requestedDetailRegion = Rect.Empty;

        if (_width > 0 && _levelIndex != 0)
        {
            _levelIndex = 0;
            EnsureLevelBitmap();
        }

        _suppressSchedule = true;
        try
        {
            Recomposite();
        }
        finally
        {
            _suppressSchedule = false;
        }

        _detailDebounce.Stop();
        _requestedDetailRegion = Rect.Empty;

        // Plate state changed, so a visible proof is stale. Re-render it in the background rather
        // than dropping back to the screen composite without saying so.
        if (_proofedBitmap is not null && _proofProvider is not null)
            _ = RefreshProofAsync();

        MaybeRequestDetail();
    }

    private async Task RefreshProofAsync()
    {
        try
        {
            await RenderProofAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Soft proof refresh failed: {ex.Message}");
        }
    }
    
    private List<PlateLayer> GetActiveLayers()
    {
        var anySolo = _plates.Any(p => p.Solo);
        var list = new List<PlateLayer>();
        foreach (var plate in _plates)
        {
            if (anySolo ? plate.Solo : plate.Visible)
                list.Add(plate);
        }
        
        if (_viewMode == PlateViewMode.Progressive)
            list = list.Take(Math.Max(0, _progressiveCount)).ToList();
        
        return list;
    }
    
    private void Recomposite()
    {
        PrepareRenderRect();
        if (_bitmap == null || _buffer == null || _renderWidth <= 0 || _renderHeight <= 0)
        {
            InvalidateVisual();
            return;
        }
        
        var layers = GetActiveLayers();
        var n = layers.Count;
        var coverage = new byte[n][];
        var inverted = new bool[n];
        var kr = new float[n];
        var kg = new float[n];
        var kb = new float[n];
        for (var i = 0; i < n; i++)
        {
            coverage[i] = LevelOf(layers[i], _levelIndex)?.Coverage ?? layers[i].Coverage;
            inverted[i] = layers[i].Inverted;
            // How strongly this ink absorbs each channel at full coverage.
            kr[i] = 1f - layers[i].R / 255f;
            kg[i] = 1f - layers[i].G / 255f;
            kb[i] = 1f - layers[i].B / 255f;
        }
        
        var isWhite = new bool[n];
        var opacity = new float[n];
        for (var i = 0; i < n; i++)
        {
            isWhite[i] = layers[i].Separation.Color == SeparationColor.White ||
                         layers[i].Name.Contains("white", StringComparison.OrdinalIgnoreCase);
            opacity[i] = (float)Math.Clamp(layers[i].Opacity, 0.0, 1.0);
        }

        var film = _viewMode == PlateViewMode.FilmBlackWhite;
        var paperR = _paperColor.R / 255f;
        var paperG = _paperColor.G / 255f;
        var paperB = _paperColor.B / 255f;

        var buffer = _buffer;
        var w = _renderWidth;
        
        Parallel.For(0, _renderHeight, y =>
        {
            var rowStart = (y + _renderY0) * _renderLevelWidth + _renderX0;
            for (var x = 0; x < w; x++)
            {
                var idx = rowStart + x;
                float r = paperR, g = paperG, b = paperB;

                for (var i = 0; i < n; i++)
                {
                    var c = coverage[i][idx] * (1f / 255f);
                    if (inverted[i]) c = 1f - c;
                    c *= opacity[i];
                    if (c <= 0f) continue;

                    if (isWhite[i])
                    {
                        // White ink is opaque: it hides whatever is underneath (metallic stock included).
                        r += (1f - r) * c;
                        g += (1f - g) * c;
                        b += (1f - b) * c;
                        continue;
                    }

                    if (c <= 0f) continue;

                    if (film)
                    {
                        var t = 1f - c;
                        r *= t; g *= t; b *= t;
                    }
                    else
                    {
                        r *= 1f - c * kr[i];
                        g *= 1f - c * kg[i];
                        b *= 1f - c * kb[i];
                    }
                }
                
                var o = (y * w + x) * 4;
                buffer[o] = (byte)(b * 255f + 0.5f);
                buffer[o + 1] = (byte)(g * 255f + 0.5f);
                buffer[o + 2] = (byte)(r * 255f + 0.5f);
                buffer[o + 3] = 255;
            }
        });
        
        _bitmap.WritePixels(new Int32Rect(0, 0, _renderWidth, _renderHeight), buffer,
                            _renderWidth * 4, 0);
        InvalidateVisual();
    }
    
    // ------------------------------------------------------------------------------------------
    // View transform
    // ------------------------------------------------------------------------------------------
    
    public Point ScreenToImage(Point screen)
    {
        if (_width <= 0 || _height <= 0)
            return screen;

        var inv = _view;
        inv.Invert();
        return _orientation.InverseMatrix(_width, _height).Transform(inv.Transform(screen));
    }

    public Point ImageToScreen(Point image) =>
        _view.Transform(_orientation.GetMatrix(_width, _height).Transform(image));
    
    public void FitToView()
    {
        if (_width == 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
        
        var oriented = _orientation.OrientedSize(_width, _height);
        var scale = Math.Min(ActualWidth / oriented.Width, ActualHeight / oriented.Height) * 0.95;
        SetCamera(new Matrix(scale, 0, 0, scale,
            (ActualWidth - oriented.Width * scale) / 2,
            (ActualHeight - oriented.Height * scale) / 2));
        _fitPending = false;
        NotifyViewChanged();
    }
    
    /// <summary>Integer zoom stops above 100 % — every plate pixel becomes an exact N×N block.</summary>
    private static readonly double[] ZoomStops = { 1.0, 2.0, 3.0, 4.0, 6.0, 8.0, 12.0, 16.0, 24.0, 32.0 };

    /// <summary>Next stop in <paramref name="direction"/> (+1 in, -1 out) from the current zoom.</summary>
    private static double ZoomStop(double current, int direction)
    {
        if (current < 1.0)
            return direction > 0 ? 1.0 : Math.Max(MinZoomConstant, current / 1.15);

        if (direction > 0)
        {
            foreach (var stop in ZoomStops)
                if (stop > current + 1e-6)
                    return stop;
            return ZoomStops[^1];
        }

        for (var i = ZoomStops.Length - 1; i >= 0; i--)
            if (ZoomStops[i] < current - 1e-6)
                return ZoomStops[i];
        return 1.0;
    }

    private const double MinZoomConstant = 0.01;

    /// <summary>Round the pan offset to whole device pixels so tiles never land on half pixels.</summary>
    private void SnapToDevicePixels()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var sx = dpi.DpiScaleX <= 0 ? 1.0 : dpi.DpiScaleX;
        var sy = dpi.DpiScaleY <= 0 ? 1.0 : dpi.DpiScaleY;

        var m = _view;
        var x = Math.Round(m.OffsetX * sx) / sx;
        var y = Math.Round(m.OffsetY * sy) / sy;
        if (Math.Abs(x - m.OffsetX) < 1e-9 && Math.Abs(y - m.OffsetY) < 1e-9)
            return;

        m.OffsetX = x;
        m.OffsetY = y;
        SetCamera(m);
    }

    /// <summary>Set the zoom (1.0 = 100%), keeping <paramref name="anchor"/> (screen coords) fixed.</summary>
    public void SetZoom(double zoom, Point? anchor = null)
    {
        if (_width == 0) return;
        var a = anchor ?? new Point(ActualWidth / 2, ActualHeight / 2);
        var target = Math.Clamp(zoom, MinZoom, MaxZoom);
        var factor = target / _view.M11;
        var m = _view;
        m.ScaleAt(factor, factor, a.X, a.Y);
        SetCamera(m);
        SnapToDevicePixels();
        NotifyViewChanged();
    }

    /// <summary>Plate decimation of the base level (1 = the base is native resolution).</summary>
    public int NativeFactor => _nativeFactor;

    /// <summary>Decimation of the detail bitmap on screen (1 = native, 2 = half, 4 = quarter).</summary>
    public int ActiveDetailFactor =>
        _detailBitmap is not null && !_detailRect.IsEmpty ? Math.Max(1, _detailFactor) : 1;

    /// <summary>Zoom relative to native pixels: 1.0 = one plate pixel per screen pixel.</summary>
    public double NativeZoom => _camera.NativeScale;

    /// <summary>
    /// The current camera as a <see cref="ViewTransform"/> (single source of truth for the
    /// zoom readout). The centre is the plate pixel under the middle of the viewport.
    /// </summary>
    public ViewTransform View => _camera;

    /// <summary>True while native pixels are being decoded for the current view.</summary>
    public bool IsRefining
    {
        get => _refining;
        set
        {
            if (_refining == value) return;
            _refining = value;
            InvalidateVisual();
        }
    }

    /// <summary>Index into the stored pyramid levels (0 = finest stored preview level).</summary>
    public int CurrentLevelIndex => _levelIndex;

    /// <summary>Decimation of the level currently drawn, relative to native pixels.</summary>
    public int CurrentLevelFactor => Math.Max(1, _nativeFactor) << Math.Max(0, _levelIndex);

    /// <summary>True when native-resolution pixels are covering the viewport.</summary>
    public bool ShowsNativePixels =>
        _detailFactor == 1 && _detailBitmap is not null && !_detailRect.IsEmpty &&
        _detailRect.Contains(VisibleImageRect);

    /// <summary>
    /// Express <paramref name="source"/> in this viewer's frame: same magnification and centre,
    /// clamped to the zoom limits, measured against <paramref name="viewportWidth"/> ×
    /// <paramref name="viewportHeight"/>. Pure so the sync rule can be tested without a window.
    /// </summary>
    internal static ViewTransform SyncCamera(ViewTransform source, int nativeFactor,
                                             double viewportWidth, double viewportHeight)
    {
        var factor = Math.Max(1, nativeFactor);

        // Same bounds as the wheel: the 1 % floor, and 100 % because the viewer never magnifies.
        var scale = Math.Clamp(source.NativeScale, MinZoom / factor, ViewTransform.MaxNativeScale);

        return new ViewTransform(scale, source.CentreX, source.CentreY,
                                 Math.Max(1.0, viewportWidth), Math.Max(1.0, viewportHeight));
    }

    /// <summary>
    /// Adopt another view: same magnification and the same plate pixel in the middle of the
    /// viewport. This is what side-by-side sync copies — one struct, no coordinate juggling.
    /// </summary>
    public void SetView(ViewTransform view)
    {
        if (_width == 0 || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var camera = SyncCamera(view, _nativeFactor, ActualWidth, ActualHeight);

        SetCamera(MatrixFromCamera(camera, _nativeFactor));
        SnapToDevicePixels();
        NotifyViewChanged();
    }

    /// <summary>Set the zoom relative to native pixels (1.0 = 100% of the plate).</summary>
    public void SetNativeZoom(double nativeZoom, Point? anchor = null) =>
        SetZoom(nativeZoom * Math.Max(1, _nativeFactor), anchor);

    /// <summary>Zoom to a native scale at the pointed image location (or the view centre).</summary>
    public void ZoomNativeTo(double nativeZoom)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var target = nativeZoom * Math.Max(1, _nativeFactor);
        var p = Mouse.GetPosition(this);
        var inside = p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight;

        if (!inside)
            p = _lastCanvasPoint ?? new Point(ActualWidth / 2, ActualHeight / 2);

        var image = ScreenToImage(p);
        SetViewCenter(image.X, image.Y, target);
    }

    /// <summary>Rotate/flip state; zoom and pan stay a separate pure-scale matrix.</summary>
    public PlateOrientation Orientation => _orientation;

    /// <summary>
    /// True keeps process inks under black (overprint). False knocks the underlying inks out
    /// where black is solid — the difference a trap actually has to survive.
    /// </summary>
    /// <summary>Metallic stock: dull silver paper, with white ink printed opaquely on top.</summary>
    public bool SimulateMetallicPaper
    {
        get => _simulateMetallicPaper;
        set
        {
            if (_simulateMetallicPaper == value) return;
            _simulateMetallicPaper = value;
            Refresh();
        }
    }

    /// <summary>Substrate (paper) tint used by the composite and the native detail.</summary>
    public Color PaperColor
    {
        get => _paperColor;
        set
        {
            if (_paperColor == value) return;
            _paperColor = value;
            Refresh();
        }
    }

    /// <summary>0 = front only, 1 = mirrored back side fully replacing the front.</summary>
    public double BackSideOpacity
    {
        get => _backSideOpacity;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (Math.Abs(_backSideOpacity - clamped) < 0.001) return;
            _backSideOpacity = clamped;
            InvalidateVisual();
        }
    }

    /// <summary>Native detail cannot honour the simulation modes yet; hide it rather than lie.</summary>
    public bool SimulationActive => _simulateMetallicPaper;

    public string OrientationLabel => _orientation.Label();

    public void RotateClockwise()
    {
        _orientation.RotateClockwise();
        AfterOrientationChange();
    }

    public void SetRotation(int degrees)
    {
        _orientation.SetRotation(degrees);
        AfterOrientationChange();
    }

    public void ToggleFlipHorizontal()
    {
        _orientation.ToggleFlipHorizontal();
        AfterOrientationChange();
    }

    public void ToggleFlipVertical()
    {
        _orientation.ToggleFlipVertical();
        AfterOrientationChange();
    }

    public void ResetOrientation()
    {
        _orientation.Reset();
        AfterOrientationChange();
    }

    private void AfterOrientationChange()
    {
        if (_width > 0 && ActualWidth > 0 && ActualHeight > 0)
            FitToView();
        else
            InvalidateVisual();
    }

    /// <summary>Zoom and centre on an image rectangle (preflight click-to-focus).</summary>
    public void FocusOn(Rect imageRect, double fill = 0.8)
    {
        if (_width == 0 || imageRect.IsEmpty || imageRect.Width <= 0 || imageRect.Height <= 0)
            return;

        var scale = Math.Min(ActualWidth / imageRect.Width, ActualHeight / imageRect.Height) * fill;
        scale = Math.Clamp(scale, MinZoom, MaxZoom);

        SetViewCenter(imageRect.X + imageRect.Width / 2,
                      imageRect.Y + imageRect.Height / 2, scale);
    }

    /// <summary>Draw the content-box / margin / offset overlay.</summary>
    public bool ShowContentOverlay
    {
        get => _showContentOverlay;
        set
        {
            if (_showContentOverlay == value) return;
            _showContentOverlay = value;
            InvalidateVisual();
        }
    }

    /// <summary>Content box in image pixels (union of plate ink bounds).</summary>
    public void SetContentBounds(Rect boundsInImagePixels)
    {
        _contentBounds = boundsInImagePixels;
        InvalidateVisual();
    }

    /// <summary>Supply the native-resolution detail for an image rectangle (null clears it).</summary>
    public void SetDetail(BitmapSource? bitmap, Rect imageRect)
    {
        _detailBitmap = bitmap is null ? null : new WriteableBitmap(bitmap);
        _detailRect = bitmap is null ? Rect.Empty : imageRect;
        _requestedDetailRegion = Rect.Empty;
        _refining = false;
        InvalidateVisual();
        // Let the host refresh the "view 1200 dpi / Native pixels" readout.
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Paint a partially decoded bitmap over the low-resolution preview. Unpainted areas stay
    /// transparent so the cached base image remains visible underneath.
    /// </summary>
    public void SetDetailProgress(BitmapSource? bitmap, Rect imageRect)
    {
        _detailBitmap = bitmap is null ? null : new WriteableBitmap(bitmap);
        _detailRect = bitmap is null ? Rect.Empty : imageRect;
        InvalidateVisual();
    }

    /// <summary>Re-evaluate whether native detail is needed now (visibility toggles, etc.).</summary>
    public void RefreshDetail()
    {
        _requestedDetailRegion = Rect.Empty;
        MaybeRequestDetail();
    }

    /// <summary>
    /// While the user drags or zooms, keep the fast cached preview and only ask for native
    /// tiles once the view has been still for the debounce interval.
    /// </summary>
    private void ScheduleDetailRequest()
    {
        if (_suppressSchedule)
            return;

        _detailDebounce.Stop();

        if (_width == 0)
            return;

        var level = DetailLevelForZoom();
        var factor = 1 << level;
        var visible = VisibleImageRect;

        // A sync from the compare pane can raise ViewChanged without moving this view; keep the
        // native bitmap we already have instead of dropping and re-requesting it.
        if (_detailBitmap is not null && _detailFactor == factor &&
            !visible.IsEmpty && _detailRect.Contains(visible))
            return;

        if (level >= 3)
        {
            _refining = false;
            _detailBitmap = null;
            _detailRect = Rect.Empty;
            _requestedDetailRegion = Rect.Empty;
            InvalidateVisual();
            return;
        }

        // Drop stale native pixels so interaction shows the overview/pyramid preview.
        _detailBitmap = null;
        _detailRect = Rect.Empty;
        _requestedDetailRegion = Rect.Empty;
        _refining = true;
        InvalidateVisual();

        _detailDebounce.Start();
    }

    /// <summary>
    /// Pyramid level for the current zoom: 0 = native 2400 dpi, 1 = 1200 dpi, 2 = 600 dpi,
    /// 3 = the 300 dpi base bitmap (no tiles needed).
    /// </summary>
    private int DetailLevelForZoom()
    {
        // scale = screen px per native plate px; level = floor(log2(1 / scale)).
        // 2400 dpi base pyramid has levels 0..3 (2400 / 1200 / 600 / 300 dpi).
        var scale = NativeZoom;
        var level = scale >= 1.0 ? 0 : (int)Math.Floor(Math.Log2(1.0 / Math.Max(scale, 1e-6)));
        return Math.Clamp(level, 0, 3); // pyramid.MaxLevel = 3 (base is 1/8 = 300 dpi)
    }

    /// <summary>Ask for the best tile level for the current zoom.</summary>
    private void MaybeRequestDetail()
    {
        if (_width == 0 || _nativeFactor <= 1)
            return;

        var level = DetailLevelForZoom();
        if (level >= 3)
        {
            _refining = false;
            _requestedDetailRegion = Rect.Empty;
            if (_detailBitmap is not null)
            {
                _detailBitmap = null;
                _detailRect = Rect.Empty;
            }
            return;
        }

        var factor = 1 << level;
        var visible = VisibleImageRect;
        if (visible.IsEmpty ||
            (_detailBitmap is not null && _detailFactor == factor && _detailRect.Contains(visible)))
            return;

        // A request is already in flight for a region that still covers this view. Re-firing on
        // every mouse move caused thousands of cancelled decodes and multi-gigabyte churn.
        if (!_requestedDetailRegion.IsEmpty && _requestedDetailRegion.Contains(visible))
            return;

        // Decode a margin around the viewport so a small pan does not immediately re-decode.
        var marginX = visible.Width * 0.25;
        var marginY = visible.Height * 0.25;
        var region = new Rect(visible.X - marginX, visible.Y - marginY,
                              visible.Width + marginX * 2, visible.Height + marginY * 2);
        region.Intersect(new Rect(0, 0, _width, _height));

        if (region.Width < 1 || region.Height < 1)
            return;

        _requestedDetailRegion = region;
        _detailFactor = factor;
        IsRefining = true;
        DetailRequested?.Invoke(this, new DetailRequest(region, factor));
    }

    /// <summary>Visible part of the page in image pixels; empty when nothing is loaded.</summary>
    public Rect VisibleImageRect
    {
        get
        {
            if (_width == 0 || ActualWidth <= 0 || ActualHeight <= 0)
                return Rect.Empty;

            var rect = new Rect(ScreenToImage(new Point(0, 0)),
                                ScreenToImage(new Point(ActualWidth, ActualHeight)));
            rect.Intersect(new Rect(0, 0, _width, _height));
            return rect;
        }
    }

    /// <summary>Pan so an image point sits at the centre of the viewport (optionally setting zoom).</summary>
    public void SetViewCenter(double imageX, double imageY, double? zoom = null)
    {
        if (_width == 0)
            return;

        var scale = Math.Clamp(zoom ?? Zoom, MinZoom, MaxZoom);
        var oriented = _orientation.GetMatrix(_width, _height).Transform(new Point(imageX, imageY));
        SetCamera(new Matrix(scale, 0, 0, scale,
            ActualWidth / 2 - oriented.X * scale,
            ActualHeight / 2 - oriented.Y * scale));
        SnapToDevicePixels();
        NotifyViewChanged();
    }

    /// <summary>Plate width in image pixels — the reference for "100 %" in compare overlays.</summary>
    public int PlateWidthPixels => _width;

    /// <summary>
    /// Centre on the inked area when its bounds are known, falling back to the plate centre.
    /// A press sheet is mostly paper, so centring on the sheet lands on a blank margin.
    /// </summary>
    public void CentreOnContent()
    {
        var b = _contentBounds;

        if (b.IsEmpty || b.Width <= 0 || b.Height <= 0)
        {
            CentreOnPlate();
            return;
        }

        SetViewCenter(b.X + b.Width / 2, b.Y + b.Height / 2);
    }

    /// <summary>Centre the viewport on the plate centre (used when a compare pane opens).</summary>
    public void CentreOnPlate()
    {
        if (_width <= 0 || _height <= 0)
            return;
        SetViewCenter(_width / 2.0, _height / 2.0);
    }

    /// <summary>Image-space centre of the viewport and the current zoom (for session restore).</summary>
    public (double CenterX, double CenterY, double Zoom) GetViewState()
    {
        var centre = ScreenToImage(new Point(ActualWidth / 2, ActualHeight / 2));
        return (centre.X, centre.Y, Zoom);
    }
    
    /// <summary>Keep at least a strip of the page inside the viewport.</summary>
    private void ClampPan()
    {
        if (_width <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var tl = ImageToScreen(new Point(0, 0));
        var br = ImageToScreen(new Point(_width, _height));
        var screen = new Rect(tl, br);

        var dx = 0.0;
        var dy = 0.0;

        // When the page is larger than the viewport it must cover it (no dead background);
        // when it is smaller, keep it centred.
        if (screen.Width >= ActualWidth)
        {
            if (screen.Left > 0) dx = -screen.Left;
            else if (screen.Right < ActualWidth) dx = ActualWidth - screen.Right;
        }
        else
        {
            dx = (ActualWidth - screen.Width) / 2 - screen.Left;
        }

        if (screen.Height >= ActualHeight)
        {
            if (screen.Top > 0) dy = -screen.Top;
            else if (screen.Bottom < ActualHeight) dy = ActualHeight - screen.Bottom;
        }
        else
        {
            dy = (ActualHeight - screen.Height) / 2 - screen.Top;
        }

        if (dx != 0 || dy != 0)
        {
            var m = _view;
            m.Translate(dx, dy);
            SetCamera(m);
            SnapToDevicePixels();
        }
    }

    private void NotifyViewChanged()
    {
        // Nearest-neighbour when magnified so individual pixels stay crisp.
        // Crisp squares only when the pixels being drawn are the plate's own; a coarse level
        // enlarged with nearest-neighbour would just look like stair-stepped mush.
        // Nearest only for exact native 1:1-or-better dot inspection; every decimated level and
        // composite preview is upscaled with high-quality smoothing (bicubic-class).
        var crispNative = DetailLevelForZoom() == 0 && NativeZoom >= 1.0;
        RenderOptions.SetBitmapScalingMode(this,
            crispNative ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);

        // Zoomed out far enough that a coarser pyramid level covers more than a screen pixel?
        // Switch to it, so panning composites a fraction of the pixels.
        ClampPan();
        ApplyZoomLevel(force: false);
        _compositeDebounce.Stop();
        _compositeDebounce.Start();
        ScheduleDetailRequest();

        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }
    
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_fitPending)
        {
            FitToView();
        }
        else
        {
            // Keep the same pixels on screen while re-expressing the camera in the new frame.
            SetCamera(_view);
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    
    // ------------------------------------------------------------------------------------------
    // Mouse
    // ------------------------------------------------------------------------------------------
    
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_width == 0) return;
        
        var direction = e.Delta > 0 ? 1 : -1;
        var target = Zoom >= 1.0 ? ZoomStop(Zoom, direction) : Zoom * (direction > 0 ? 1.15 : 1 / 1.15);
        SetZoom(target, e.GetPosition(this));
        e.Handled = true;
    }
    
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_width == 0) return;
        
        var pos = e.GetPosition(this);
        var wantsPan = e.ChangedButton == MouseButton.Middle ||
                       (e.ChangedButton == MouseButton.Left &&
                        (_tool == ViewerTool.Pan || Keyboard.IsKeyDown(Key.Space)));
        
        if (wantsPan)
        {
            _panning = true;
            _panLast = pos;
            CaptureMouse();
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Left && _tool == ViewerTool.Select)
        {
            // Marquee zoom is a plate operation: dragging on the margin/canvas does nothing.
            if (!IsInsidePlate(ScreenToImage(pos)))
            {
                e.Handled = true;
                return;
            }

            _selecting = true;
            _selectStartScreen = _selectCurrentScreen = pos;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Left && _tool == ViewerTool.Measure)
        {
            _measuring = true;
            _measureRectangle = RectangleMeasure || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            _caliperStart = _caliperEnd = ScreenToImage(pos);
            CaptureMouse();
            RaiseMeasurement(false);
            InvalidateVisual();
            e.Handled = true;
        }
    }
    
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pos = e.GetPosition(this);
        _cursorScreen = pos;
        if (pos.X >= 0 && pos.Y >= 0 && pos.X <= ActualWidth && pos.Y <= ActualHeight)
            _lastCanvasPoint = pos;
        
        if (_panning)
        {
            var m = _view;
            m.Translate(pos.X - _panLast.X, pos.Y - _panLast.Y);
            SetCamera(m);
            SnapToDevicePixels();
            _panLast = pos;
            NotifyViewChanged();
        }
        else if (_selecting)
        {
            _selectCurrentScreen = pos;
            InvalidateVisual();
        }
        else if (_measuring)
        {
            _caliperEnd = ScreenToImage(pos);
            RaiseMeasurement(false);
            InvalidateVisual();
        }
        else if (_tool == ViewerTool.Densitometer)
        {
            InvalidateVisual(); // move the aperture marker
        }
        
        if (_width > 0)
            CursorMoved?.Invoke(this, ScreenToImage(pos));
    }
    
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        
        if (_selecting)
        {
            _selecting = false;
            _selectCurrentScreen = e.GetPosition(this);
            ZoomToSelection();
            _selectStartScreen = _selectCurrentScreen = null;
            InvalidateVisual();
        }

        if (_measuring)
        {
            _measuring = false;
            _caliperEnd = ScreenToImage(e.GetPosition(this));
            RaiseMeasurement(true);
            InvalidateVisual();
        }
        
        _panning = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }
    
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _cursorScreen = null;
        CursorMoved?.Invoke(this, null);
        InvalidateVisual();
    }
    
    /// <summary>Fit the marquee rectangle drawn with the Select tool into the viewport.</summary>
    private void ZoomToSelection()
    {
        if (_selectStartScreen is not { } a || _selectCurrentScreen is not { } b)
            return;

        var w = Math.Abs(b.X - a.X);
        var h = Math.Abs(b.Y - a.Y);
        if (w < 8 || h < 8)
            return; // treat as a click, not a marquee

        var p1 = ScreenToImage(a);
        var p2 = ScreenToImage(b);
        var rect = new Rect(
            new Point(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y)),
            new Point(Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y)));

        if (rect.Width < 1 || rect.Height < 1)
            return;

        var scale = Math.Min(ActualWidth / rect.Width, ActualHeight / rect.Height);
        scale = Math.Clamp(scale, MinZoom, MaxZoom);
        SetViewCenter(rect.X + rect.Width / 2, rect.Y + rect.Height / 2, scale);
    }

    private void RaiseMeasurement(bool completed)
    {
        if (_caliperStart is not { } s || _caliperEnd is not { } en)
            return;

        if (_measureRectangle)
        {
            var rect = new Rect(
                new Point(Math.Min(s.X, en.X), Math.Min(s.Y, en.Y)),
                new Point(Math.Max(s.X, en.X), Math.Max(s.Y, en.Y)));

            RectangleChanged?.Invoke(this, new RectangleMeasuredEventArgs(rect, completed));
            return;
        }

        CaliperChanged?.Invoke(this, new CaliperEventArgs(s, en, completed));
    }
    
    // ------------------------------------------------------------------------------------------
    // Rendering
    // ------------------------------------------------------------------------------------------
    
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        
        // Transparent fill so the whole area receives mouse input.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        // The proofed render replaces the screen composite pixel for pixel; it covers the same
        // level, so the same rect and matrix apply.
        var source = _proofedBitmap ?? _bitmap;

        if (source == null) return;
        
        var drawMatrix = _orientation.GetMatrix(_width, _height);
        drawMatrix.Append(_view);

        // Above 50 % the view is rasterised at device resolution and shown 1:1; below that the
        // magnified bitmap path is cheaper and equally soft either way.
        if (NativeZoom >= 0.5 && ActualWidth > 0 && ActualHeight > 0)
        {
            RenderDeviceResolution(dc, drawMatrix, source);
        }
        else
        {
            dc.PushTransform(new MatrixTransform(drawMatrix));
            dc.DrawImage(source, _renderRect);
            if (_detailBitmap is not null && NativeZoom >= 0.75 && !SimulationActive && !ProofEnabled)
                dc.DrawImage(_detailBitmap, _detailRect);
            dc.Pop();
            RenderBackSide(dc);
            DrawOverlaysAndChrome(dc);
            return;
        }

        RenderBackSide(dc);
        DrawOverlaysAndChrome(dc);
    }

    private void RenderDeviceResolution(DrawingContext dc, Matrix drawMatrix, BitmapSource source)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var pw = Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX));
        var ph = Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY));

        if (_deviceBitmap is null || _deviceWidth != pw || _deviceHeight != ph)
        {
            _deviceBitmap = new WriteableBitmap(pw, ph, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Bgra32, null);
            _devicePixels = new byte[pw * ph * 4];
            _deviceWidth = pw;
            _deviceHeight = ph;
        }

        Array.Clear(_devicePixels!, 0, _devicePixels!.Length);
        ViewportResampler.Draw(source, _renderRect, drawMatrix, dpi.DpiScaleX, dpi.DpiScaleY, pw, ph, _devicePixels!);

        if (_detailBitmap is not null && NativeZoom >= 0.75 && !SimulationActive && !ProofEnabled)
            ViewportResampler.Draw(_detailBitmap, _detailRect, drawMatrix, dpi.DpiScaleX, dpi.DpiScaleY, pw, ph, _devicePixels!);

        _deviceBitmap.WritePixels(new Int32Rect(0, 0, pw, ph), _devicePixels!, pw * 4, 0);

        RenderOptions.SetBitmapScalingMode(_deviceBitmap, BitmapScalingMode.NearestNeighbor);
        dc.DrawImage(_deviceBitmap, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    private void RenderBackSide(DrawingContext dc)
    {
        if (_backSideOpacity <= 0.001 || _bitmap is null)
            return;

        // The back side mirrors along the axis the user is flipping: left/right, top/bottom,
        // or both. This keeps the bleed-through check aligned with the flip switch.
        var backMatrix = (_orientation.FlipHorizontal, _orientation.FlipVertical) switch
        {
            (true, true) => new Matrix(-1, 0, 0, -1, _width, _height),
            (false, true) => new Matrix(1, 0, 0, -1, 0, _height),
            _ => new Matrix(-1, 0, 0, 1, _width, 0)
        };

        var drawMatrix = _orientation.GetMatrix(_width, _height);
        drawMatrix.Append(_view);

        dc.PushTransform(new MatrixTransform(drawMatrix));
        dc.PushOpacity(_backSideOpacity);
        dc.PushTransform(new MatrixTransform(backMatrix));
        dc.DrawImage(_bitmap, _renderRect);
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private void DrawOverlaysAndChrome(DrawingContext dc)
    {
        DrawContentOverlay(dc);
        DrawResolutionBadge(dc);
        DrawOrientationBadge(dc);

        // Page border
        var topLeft = ImageToScreen(new Point(0, 0));
        var bottomRight = ImageToScreen(new Point(_width, _height));
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(60, 72, 96)), 1),
            new Rect(topLeft, bottomRight));

        if (_selecting && _selectStartScreen is { } ms && _selectCurrentScreen is { } mc)
        {
            var marquee = new Rect(ms, mc);
            var fill = new SolidColorBrush(Color.FromArgb(40, 90, 170, 255));
            var stroke = new Pen(new SolidColorBrush(Color.FromRgb(130, 205, 255)), 1)
            {
                DashStyle = DashStyles.Dash
            };
            dc.DrawRectangle(fill, stroke, marquee);
        }

        DrawDensitometerMarker(dc);
        DrawCaliper(dc);
    }

    private void DrawOrientationBadge(DrawingContext dc)
    {
        if (_bitmap is null || _orientation.IsIdentity)
            return;

        var formatted = new FormattedText("View: " + _orientation.Label(),
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var origin = new Point(Math.Max(10, ActualWidth - formatted.Width - 16), 10);
        var background = new SolidColorBrush(Color.FromArgb(210, 15, 21, 32));
        background.Freeze();
        dc.DrawRoundedRectangle(background, new Pen(new SolidColorBrush(Color.FromRgb(70, 82, 106)), 1),
            new Rect(origin.X - 6, origin.Y - 3, formatted.Width + 12, formatted.Height + 6), 4, 4);
        dc.DrawText(formatted, origin);
    }

    private void DrawContentOverlay(DrawingContext dc)
    {
        if (!_showContentOverlay || _bitmap is null || _contentBounds.IsEmpty || PixelsPerMm <= 0)
            return;

        if (ContentBoxOverlay.Compute(_contentBounds, _width, _height, PixelsPerMm) is not { } layout)
            return;

        var accent = new SolidColorBrush(Color.FromRgb(255, 190, 60));
        accent.Freeze();
        var dimColour = new SolidColorBrush(Color.FromRgb(150, 190, 255));
        dimColour.Freeze();

        var boxPen = new Pen(accent, 1.2) { DashStyle = DashStyles.Dash };
        boxPen.Freeze();
        var dimPen = new Pen(dimColour, 1.0);
        dimPen.Freeze();

        var topLeft = ToScreen(new Point(_contentBounds.Left, _contentBounds.Top));
        var bottomRight = ToScreen(new Point(_contentBounds.Right, _contentBounds.Bottom));
        var box = new Rect(topLeft, bottomRight);
        dc.DrawRectangle(null, boxPen, box);

        // Plate centre cross, and a smaller cross for the content centre.
        var plateCentre = ToScreen(new Point(_width / 2.0, _height / 2.0));
        DrawCross(dc, plateCentre, 10, accent, 1.6);

        var contentCentreImage = new Point(_contentBounds.Left + _contentBounds.Width / 2,
                                           _contentBounds.Top + _contentBounds.Height / 2);
        var contentCentre = ToScreen(contentCentreImage);
        DrawCross(dc, contentCentre, 5, dimColour, 1.0);

        // Four margin dimensions, placed along the middle of the content box.
        var midY = contentCentreImage.Y;
        var midX = contentCentreImage.X;

        DrawDimension(dc, dimPen,
            ToScreen(new Point(0, midY)), ToScreen(new Point(_contentBounds.Left, midY)),
            $"L {layout.LeftMm:0.0} mm");
        DrawDimension(dc, dimPen,
            ToScreen(new Point(_contentBounds.Right, midY)), ToScreen(new Point(_width, midY)),
            $"R {layout.RightMm:0.0} mm");
        DrawDimension(dc, dimPen,
            ToScreen(new Point(midX, 0)), ToScreen(new Point(midX, _contentBounds.Top)),
            $"T {layout.TopMm:0.0} mm");
        DrawDimension(dc, dimPen,
            ToScreen(new Point(midX, _contentBounds.Bottom)), ToScreen(new Point(midX, _height)),
            $"B {layout.BottomMm:0.0} mm");

        // Centre-to-centre offset.
        var offsetLine = plateCentre - contentCentre;
        if (offsetLine.Length >= 6)
        {
            var offsetPen = new Pen(accent, 1.0) { DashStyle = DashStyles.Dot };
            offsetPen.Freeze();
            dc.DrawLine(offsetPen, plateCentre, contentCentre);
            DrawLabel(dc, layout.OffsetText, new Point((plateCentre.X + contentCentre.X) / 2,
                                                       (plateCentre.Y + contentCentre.Y) / 2));
        }
    }

    private Point ToScreen(Point imagePoint) => ImageToScreen(imagePoint);

    private static void DrawCross(DrawingContext dc, Point centre, double arm, Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        dc.DrawLine(pen, new Point(centre.X - arm, centre.Y), new Point(centre.X + arm, centre.Y));
        dc.DrawLine(pen, new Point(centre.X, centre.Y - arm), new Point(centre.X, centre.Y + arm));
    }

    /// <summary>Dimension line with end ticks and a centred millimetre label.</summary>
    private void DrawDimension(DrawingContext dc, Pen pen, Point from, Point to, string text)
    {
        var delta = to - from;
        if (delta.Length < 8)
            return; // margin too small to read; the label would cover the artwork

        dc.DrawLine(pen, from, to);

        var tick = Math.Abs(delta.X) > Math.Abs(delta.Y) ? new Vector(0, 4) : new Vector(4, 0);
        dc.DrawLine(pen, from - tick, from + tick);
        dc.DrawLine(pen, to - tick, to + tick);

        DrawLabel(dc, text, new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2));
    }

    private void DrawLabel(DrawingContext dc, string text, Point centre)
    {
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var origin = new Point(centre.X - formatted.Width / 2, centre.Y - formatted.Height / 2);
        var background = new SolidColorBrush(Color.FromArgb(225, 20, 26, 38));
        background.Freeze();
        dc.DrawRoundedRectangle(background, null,
            new Rect(origin.X - 4, origin.Y - 1, formatted.Width + 8, formatted.Height + 2), 3, 3);
        dc.DrawText(formatted, origin);
    }

    private void DrawResolutionBadge(DrawingContext dc)
    {
        if (_bitmap is null)
            return;

        string text;
        if (_refining && NativeZoom >= 0.75)
            text = "Refining…";
        else if (ShowsNativePixels)
            text = "Native pixels";
        else if (CurrentLevelFactor > 1)
            text = $"Overview 1/{CurrentLevelFactor}";
        else
            text = "Native pixels";

        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        // Badges live in the top-right lane; the X/Y/zoom readout owns the bottom-left lane.
        var top = _orientation.IsIdentity ? 10 : 32;
        var origin = new Point(Math.Max(10, ActualWidth - formatted.Width - 16), top);
        var background = new SolidColorBrush(Color.FromArgb(210, 15, 21, 32));
        dc.DrawRoundedRectangle(background, new Pen(new SolidColorBrush(Color.FromRgb(70, 82, 106)), 1),
            new Rect(origin.X - 6, origin.Y - 3, formatted.Width + 12, formatted.Height + 6), 4, 4);
        dc.DrawText(formatted, origin);
    }

    private void DrawDensitometerMarker(DrawingContext dc)
    {
        if (_tool != ViewerTool.Densitometer || _cursorScreen is not { } c) return;
        
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(74, 163, 214)), 1);
        dc.DrawLine(pen, new Point(c.X - 12, c.Y), new Point(c.X + 12, c.Y));
        dc.DrawLine(pen, new Point(c.X, c.Y - 12), new Point(c.X, c.Y + 12));
        
        var size = Math.Max(ApertureSize * Zoom, 6);
        dc.DrawRectangle(null, pen, new Rect(c.X - size / 2, c.Y - size / 2, size, size));
    }
    
    /// <summary>True when an image-space point lies on the plate itself (not the canvas margin).</summary>
    private bool IsInsidePlate(Point imagePoint) =>
        _width > 0 && _height > 0 &&
        imagePoint.X >= 0 && imagePoint.Y >= 0 &&
        imagePoint.X <= _width && imagePoint.Y <= _height;

    private void DrawCaliper(DrawingContext dc)
    {
        if (_caliperStart is not { } s || _caliperEnd is not { } e) return;
        
        var a = ImageToScreen(s);
        var b = ImageToScreen(e);
        var gold = new SolidColorBrush(Color.FromRgb(245, 180, 0));
        var pen = new Pen(gold, 1.5);

        if (_measureRectangle)
        {
            var box = new Rect(a, b);
            var fill = new SolidColorBrush(Color.FromArgb(28, 245, 180, 0));
            dc.DrawRectangle(fill, pen, box);
            dc.DrawEllipse(gold, null, box.TopLeft, 3, 3);
            dc.DrawEllipse(gold, null, box.BottomRight, 3, 3);

            var wMm = Math.Abs(e.X - s.X) / Math.Max(PixelsPerMm, 0.0001);
            var hMm = Math.Abs(e.Y - s.Y) / Math.Max(PixelsPerMm, 0.0001);
            var boxText = new FormattedText($"{wMm:0.00} × {hMm:0.00} mm",
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Consolas"), 12, Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            // The reading pill belongs to the plate, not to the grey margin.
            if (IsInsidePlate(new Point(Math.Min(s.X, e.X), Math.Min(s.Y, e.Y))))
            {
                var label = new Point(box.Left + 8, box.Top - boxText.Height - 8);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(220, 15, 21, 32)), null,
                    new Rect(label.X - 4, label.Y - 2, boxText.Width + 8, boxText.Height + 4), 3, 3);
                dc.DrawText(boxText, label);
            }
            return;
        }

        dc.DrawLine(pen, a, b);
        dc.DrawEllipse(gold, null, a, 3, 3);
        dc.DrawEllipse(gold, null, b, 3, 3);
        
        // End ticks perpendicular to the line
        var dir = b - a;
        if (dir.Length > 0.5)
        {
            dir.Normalize();
            var normal = new Vector(-dir.Y, dir.X) * 6;
            dc.DrawLine(pen, a - normal, a + normal);
            dc.DrawLine(pen, b - normal, b + normal);
        }
        
        var distMm = (e - s).Length / Math.Max(PixelsPerMm, 0.0001);
        var text = new FormattedText($"{distMm:0.00} mm",
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Consolas"), 12, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        
        var pillCentre = new Point((s.X + e.X) / 2, (s.Y + e.Y) / 2);
        if (IsInsidePlate(pillCentre))
        {
            var mid = new Point((a.X + b.X) / 2 + 8, (a.Y + b.Y) / 2 - text.Height - 6);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(220, 15, 21, 32)), null,
                new Rect(mid.X - 4, mid.Y - 2, text.Width + 8, text.Height + 4), 3, 3);
            dc.DrawText(text, mid);
        }
    }
}
