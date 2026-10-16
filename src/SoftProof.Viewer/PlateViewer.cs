using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SoftProof.Core.Models;

namespace SoftProof.Viewer;

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
    private const double MinZoom = 0.02;
    private const double MaxZoom = 64.0;
    
    private IReadOnlyList<PlateLayer> _plates = Array.Empty<PlateLayer>();
    private int _width;
    private int _height;
    private WriteableBitmap? _bitmap;
    private byte[]? _buffer;
    
    private Matrix _view = Matrix.Identity;
    private bool _fitPending;
    
    private bool _panning;
    private Point _panLast;
    
    private bool _measuring;
    private Point? _caliperStart;
    private Point? _caliperEnd;
    
    private Point? _cursorScreen;
    private ViewerTool _tool = ViewerTool.Select;
    private PlateViewMode _viewMode = PlateViewMode.Composite;
    private int _progressiveCount = int.MaxValue;
    
    public PlateViewer()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Arrow;
    }
    
    /// <summary>Raised whenever zoom or pan changes.</summary>
    public event EventHandler? ViewChanged;
    /// <summary>Raised when the cursor moves over the image (image pixel coordinates) or leaves (null).</summary>
    public event EventHandler<Point?>? CursorMoved;
    /// <summary>Raised while a caliper measurement is dragged and when it completes.</summary>
    public event EventHandler<CaliperEventArgs>? CaliperChanged;
    
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
            Recomposite();
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
                Recomposite();
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
        _buffer = new byte[checked(width * height * 4)];
        _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        _caliperStart = _caliperEnd = null;
        
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
        _caliperStart = _caliperEnd = null;
        InvalidateVisual();
    }
    
    /// <summary>Re-composite after plate flags (visible / solo / inverted) changed.</summary>
    public void Refresh() => Recomposite();
    
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
        if (_bitmap == null || _buffer == null)
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
            coverage[i] = layers[i].Coverage;
            inverted[i] = layers[i].Inverted;
            // How strongly this ink absorbs each channel at full coverage.
            kr[i] = 1f - layers[i].R / 255f;
            kg[i] = 1f - layers[i].G / 255f;
            kb[i] = 1f - layers[i].B / 255f;
        }
        
        var film = _viewMode == PlateViewMode.FilmBlackWhite;
        var buffer = _buffer;
        var w = _width;
        
        Parallel.For(0, _height, y =>
        {
            var rowStart = y * w;
            for (var x = 0; x < w; x++)
            {
                var idx = rowStart + x;
                float r = 1f, g = 1f, b = 1f;
                
                for (var i = 0; i < n; i++)
                {
                    var c = coverage[i][idx] * (1f / 255f);
                    if (inverted[i]) c = 1f - c;
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
                
                var o = idx * 4;
                buffer[o] = (byte)(b * 255f + 0.5f);
                buffer[o + 1] = (byte)(g * 255f + 0.5f);
                buffer[o + 2] = (byte)(r * 255f + 0.5f);
                buffer[o + 3] = 255;
            }
        });
        
        _bitmap.WritePixels(new Int32Rect(0, 0, _width, _height), buffer, _width * 4, 0);
        InvalidateVisual();
    }
    
    // ------------------------------------------------------------------------------------------
    // View transform
    // ------------------------------------------------------------------------------------------
    
    public Point ScreenToImage(Point screen)
    {
        var inv = _view;
        inv.Invert();
        return inv.Transform(screen);
    }
    
    public Point ImageToScreen(Point image) => _view.Transform(image);
    
    public void FitToView()
    {
        if (_width == 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
        
        var scale = Math.Min(ActualWidth / _width, ActualHeight / _height) * 0.95;
        _view = new Matrix(scale, 0, 0, scale,
            (ActualWidth - _width * scale) / 2,
            (ActualHeight - _height * scale) / 2);
        _fitPending = false;
        NotifyViewChanged();
    }
    
    /// <summary>Set the zoom (1.0 = 100%), keeping <paramref name="anchor"/> (screen coords) fixed.</summary>
    public void SetZoom(double zoom, Point? anchor = null)
    {
        if (_width == 0) return;
        var a = anchor ?? new Point(ActualWidth / 2, ActualHeight / 2);
        var target = Math.Clamp(zoom, MinZoom, MaxZoom);
        var factor = target / _view.M11;
        _view.ScaleAt(factor, factor, a.X, a.Y);
        NotifyViewChanged();
    }
    
    private void NotifyViewChanged()
    {
        // Nearest-neighbour when magnified so individual pixels stay crisp.
        RenderOptions.SetBitmapScalingMode(this,
            Zoom >= 2.0 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Linear);
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }
    
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_fitPending)
            FitToView();
        else
            ViewChanged?.Invoke(this, EventArgs.Empty);
    }
    
    // ------------------------------------------------------------------------------------------
    // Mouse
    // ------------------------------------------------------------------------------------------
    
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_width == 0) return;
        
        var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        SetZoom(Zoom * factor, e.GetPosition(this));
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
        else if (e.ChangedButton == MouseButton.Left && _tool == ViewerTool.Measure)
        {
            _measuring = true;
            _caliperStart = _caliperEnd = ScreenToImage(pos);
            CaptureMouse();
            RaiseCaliper(false);
            InvalidateVisual();
            e.Handled = true;
        }
    }
    
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pos = e.GetPosition(this);
        _cursorScreen = pos;
        
        if (_panning)
        {
            _view.Translate(pos.X - _panLast.X, pos.Y - _panLast.Y);
            _panLast = pos;
            NotifyViewChanged();
        }
        else if (_measuring)
        {
            _caliperEnd = ScreenToImage(pos);
            RaiseCaliper(false);
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
        
        if (_measuring)
        {
            _measuring = false;
            _caliperEnd = ScreenToImage(e.GetPosition(this));
            RaiseCaliper(true);
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
    
    private void RaiseCaliper(bool completed)
    {
        if (_caliperStart is { } s && _caliperEnd is { } en)
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
        
        if (_bitmap == null) return;
        
        dc.PushTransform(new MatrixTransform(_view));
        dc.DrawImage(_bitmap, new Rect(0, 0, _width, _height));
        dc.Pop();
        
        // Page border
        var topLeft = ImageToScreen(new Point(0, 0));
        var bottomRight = ImageToScreen(new Point(_width, _height));
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(60, 72, 96)), 1),
            new Rect(topLeft, bottomRight));
        
        DrawDensitometerMarker(dc);
        DrawCaliper(dc);
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
    
    private void DrawCaliper(DrawingContext dc)
    {
        if (_caliperStart is not { } s || _caliperEnd is not { } e) return;
        
        var a = ImageToScreen(s);
        var b = ImageToScreen(e);
        var gold = new SolidColorBrush(Color.FromRgb(245, 180, 0));
        var pen = new Pen(gold, 1.5);
        
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
        
        var mid = new Point((a.X + b.X) / 2 + 8, (a.Y + b.Y) / 2 - text.Height - 6);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(220, 15, 21, 32)), null,
            new Rect(mid.X - 4, mid.Y - 2, text.Width + 8, text.Height + 4), 3, 3);
        dc.DrawText(text, mid);
    }
}
