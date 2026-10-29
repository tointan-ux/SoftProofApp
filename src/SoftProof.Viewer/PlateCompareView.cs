using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoftProof.Core.Analysis;
using SoftProof.Core.Models;

namespace SoftProof.Viewer;

public enum PlateCompareMode
{
    Slide,
    Blink,
    Difference,
    RipVsReference
}

/// <summary>
/// Single-canvas comparison of two plates: a draggable wipe, a timed blink, or a difference map.
/// Bitmaps are built once per plate at a bounded size, so switching modes is free; difference is
/// computed at that same bounded resolution and reported as a percentage of the compared area.
/// </summary>
public sealed class PlateCompareView : FrameworkElement
{
    private const int MaxDimension = 1400;
    private const int DefaultDifferenceThreshold = 24;

    private BitmapSource? _left;
    private BitmapSource? _right;
    private BitmapSource? _difference;
    private byte[]? _leftCoverage;
    private byte[]? _rightCoverage;
    private int _leftW;
    private int _leftH;
    private int _rightW;
    private int _rightH;
    private double _differencePercent;
    private BitmapSource? _reference;
    private RipComparison? _comparison;
    private int _bw;
    private int _bh;
    private bool _showRight;
    private Rect _imageRect = Rect.Empty;
    private double _slide = 0.5;

    /// <summary>Raised with the native zoom (1.0 = 100 %) whenever the overlay zooms.</summary>
    public event EventHandler<double>? ZoomChanged;

    private WriteableBitmap? _deviceBitmap;
    private byte[]? _devicePixels;
    private int _deviceWidth;
    private int _deviceHeight;
    private double _pendingNativeZoom = double.NaN;
    private double _lastReportedNativeZoom = double.NaN;
    private PlateLayer? _leftLayer;
    private long _platesVersion;
    private long _cacheKey = long.MinValue;
    private double _requestedNativeZoom = 1.0;
    private WriteableBitmap? _blinkA, _blinkB;
    private long _blinkKeyA = long.MinValue, _blinkKeyB = long.MinValue;
    private PlateLayer? _rightLayer;
    private int _plateWidth;      // plate pixels, so 100 % means one plate pixel per DIP
    private bool _panning;
    private Point _panStart;
    private ViewTransform _panStartCamera;

    /// <summary>
    /// The overlay camera — one <see cref="ViewTransform"/> for zoom and pan, in the same
    /// native plate pixels the panes and the main canvas use (1.0 = 100 %).
    /// </summary>
    private ViewTransform _camera = new(1.0, 1.0, 1.0, 1.0, 1.0);
    private bool _cameraReady;
    private const double MinZoom = 0.25;
    private const double MaxZoom = 24.0;
    private readonly DispatcherTimer _blinkTimer;

    public PlateCompareView()
    {
        ClipToBounds = true;
        Focusable = true;

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _blinkTimer.Tick += (_, _) =>
        {
            _showRight = !_showRight;
            InvalidateVisual();
        };

        Loaded += (_, _) => { if (Mode == PlateCompareMode.Blink) _blinkTimer.Start(); };
        Unloaded += (_, _) => _blinkTimer.Stop();
    }

    public PlateCompareMode Mode { get; private set; } = PlateCompareMode.Slide;

    public double DifferencePercent { get; private set; }

    private int _differenceThreshold = DefaultDifferenceThreshold;

    /// <summary>Coverage delta that counts as a difference; changing it recomputes the map.</summary>
    public int DifferenceThreshold
    {
        get => _differenceThreshold;
        set
        {
            var clamped = Math.Clamp(value, 0, 128);
            if (clamped == _differenceThreshold)
                return;

            _differenceThreshold = clamped;
            _difference = null;
            _differencePercent = 0;
            InvalidateVisual();
        }
    }

    public event EventHandler<double>? DifferenceMeasured;

    public void SetMode(PlateCompareMode mode)
    {
        Mode = mode;
        _showRight = false;

        if (mode == PlateCompareMode.Blink && IsLoaded)
            _blinkTimer.Start();
        else
            _blinkTimer.Stop();

        Cursor = mode == PlateCompareMode.Slide ? Cursors.SizeWE : Cursors.Arrow;

        // Keep the current scale across mode switches (a mode change must not jump to fit).
        ResetView();
        SetNativeZoom(_requestedNativeZoom);
        InvalidateVisual();
    }

    /// <summary>Supply the RIP-vs-reference comparison; null clears it.</summary>
    public void SetReference(RipComparison? comparison)
    {
        _comparison = comparison;
        _reference = comparison is null ? null : BuildReferenceBitmap(comparison);
        InvalidateVisual();
    }

    /// <summary>Back to fit-to-window, no pan.</summary>
    public void ResetView()
    {
        _camera = FitCamera();
        _cameraReady = true;
        InvalidateVisual();
    }

    /// <summary>Reference width used for 100 % so the overlay matches the main canvas exactly.</summary>
    public void SetPlateWidth(int platePixels)
    {
        if (platePixels > 0 && platePixels != _plateWidth)
        {
            _plateWidth = platePixels;

            // Keep the current scale across a plate change; only the centre moves.
            if (_cameraReady)
                _camera = new ViewTransform(_camera.NativeScale, platePixels / 2.0, PlateHeight / 2.0,
                                            _camera.ViewportWidth, _camera.ViewportHeight);

            InvalidateVisual();
        }
    }

    public void SetPlates(PlateLayer? left, PlateLayer? right)
    {
        _leftLayer = left;
        _leftCoverage = left is null ? null : BuildCoverage(left, out _left);
        _rightLayer = right;
        _rightCoverage = right is null ? null : BuildCoverage(right, out _right);
        _leftW = _left?.PixelWidth ?? 0;
        _leftH = _left?.PixelHeight ?? 0;
        _rightW = _right?.PixelWidth ?? 0;
        _rightH = _right?.PixelHeight ?? 0;
        _difference = null;
        _differencePercent = 0;

        _bw = _left?.PixelWidth ?? _right?.PixelWidth ?? 0;
        _bh = _left?.PixelHeight ?? _right?.PixelHeight ?? 0;

        // Plate pixels drive the 100 % reference, so the overlay matches the main canvas.
        _plateWidth = left?.Width ?? right?.Width ?? _bw;
        _platesVersion++;
        _cacheKey = long.MinValue;
        _blinkA = _blinkB = null;
        _blinkKeyA = _blinkKeyB = long.MinValue;
        ResetView();
    }

    // ---------------------------------------------------------------- bitmaps

    private static byte[] BuildCoverage(PlateLayer plate, out BitmapSource bitmap)
    {
        var (tw, th) = TargetSize(plate.Width, plate.Height);
        var coverage = SampleCoverage(plate.Coverage, plate.Width, plate.Height, tw, th);
        var buffer = new byte[tw * th * 4];

        var kr = 1f - plate.R / 255f;
        var kg = 1f - plate.G / 255f;
        var kb = 1f - plate.B / 255f;

        for (var i = 0; i < coverage.Length; i++)
        {
            var c = coverage[i] / 255f;
            var o = i * 4;
            buffer[o] = (byte)(255f * (1f - c * kb));
            buffer[o + 1] = (byte)(255f * (1f - c * kg));
            buffer[o + 2] = (byte)(255f * (1f - c * kr));
            buffer[o + 3] = 255;
        }

        var image = BitmapSource.Create(tw, th, 96, 96, PixelFormats.Bgra32, null, buffer, tw * 4);
        image.Freeze();
        bitmap = image;
        return coverage;
    }

    private static (int Width, int Height) TargetSize(int width, int height)
    {
        var scale = Math.Min(1.0, MaxDimension / (double)Math.Max(width, height));
        return (Math.Max(1, (int)Math.Round(width * scale)),
                Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static byte[] SampleCoverage(byte[] source, int width, int height, int tw, int th)
    {
        var target = new byte[tw * th];
        for (var y = 0; y < th; y++)
        {
            var sy = Math.Min(height - 1, y * height / th);
            var row = sy * width;
            for (var x = 0; x < tw; x++)
            {
                var sx = Math.Min(width - 1, x * width / tw);
                target[y * tw + x] = source[row + sx];
            }
        }
        return target;
    }

    private static byte[] ResizeCoverage(byte[] source, int sw, int sh, int dw, int dh)
    {
        if (sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0)
            return new byte[dw * dh];

        var target = new byte[dw * dh];
        for (var y = 0; y < dh; y++)
        {
            var sy = Math.Min(sh - 1, y * sh / dh);
            for (var x = 0; x < dw; x++)
            {
                var sx = Math.Min(sw - 1, x * sw / dw);
                target[y * dw + x] = source[sy * sw + sx];
            }
        }

        return target;
    }

    private BitmapSource? BuildDifferenceBitmap(out double percent)
    {
        percent = 0;
        if (_bw <= 0 || _bh <= 0)
            return null;

        var left = _leftCoverage ?? new byte[_bw * _bh];

        var right = _rightCoverage ?? new byte[_bw * _bh];
        if (right.Length != left.Length)
            right = ResizeCoverage(right, _rightW, _rightH, _bw, _bh);

        var buffer = new byte[_bw * _bh * 4];
        long differing = 0;

        for (var i = 0; i < left.Length; i++)
        {
            var delta = Math.Abs(left[i] - right[i]);
            var o = i * 4;

            if (delta > DifferenceThreshold)
            {
                differing++;
                buffer[o] = 40;      // B
                buffer[o + 1] = 40;  // G
                buffer[o + 2] = 235; // R
            }
            else
            {
                var common = (byte)(245 - Math.Min(left[i], right[i]) / 3);
                buffer[o] = common;
                buffer[o + 1] = common;
                buffer[o + 2] = common;
            }

            buffer[o + 3] = 255;
        }

        percent = differing * 100.0 / left.Length;
        var bitmap = BitmapSource.Create(_bw, _bh, 96, 96, PixelFormats.Bgra32, null, buffer, _bw * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource BuildReferenceBitmap(RipComparison comparison)
    {
        var buffer = new byte[comparison.Width * comparison.Height * 4];

        for (var i = 0; i < comparison.Map.Length; i++)
        {
            var code = comparison.Map[i];
            var o = i * 4;

            switch (code)
            {
                case 1: // missing ink (RIP lost it) - red
                    buffer[o] = 45; buffer[o + 1] = 45; buffer[o + 2] = 235;
                    break;
                case 2: // extra ink (RIP added it) - blue
                    buffer[o] = 235; buffer[o + 1] = 120; buffer[o + 2] = 40;
                    break;
                default: // matches - neutral
                    buffer[o] = 238; buffer[o + 1] = 238; buffer[o + 2] = 238;
                    break;
            }

            buffer[o + 3] = 255;
        }

        var bitmap = BitmapSource.Create(comparison.Width, comparison.Height, 96, 96,
            PixelFormats.Bgra32, null, buffer, comparison.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    // ---------------------------------------------------------------- rendering

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        if (Mode == PlateCompareMode.Blink && IsVisible && !_blinkTimer.IsEnabled)
            _blinkTimer.Start();
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(15, 22, 38)), null,
                         new Rect(0, 0, ActualWidth, ActualHeight));

        if (_bw <= 0 || _bh <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            DrawCentred(dc, "Select two plates to compare", ActualHeight / 2);
            return;
        }

        EnsureCamera();

        _imageRect = ImageRectFor(_camera, _plateWidth, PlateHeight);

        var reported = NativeZoom;
        if (Math.Abs(reported - _lastReportedNativeZoom) > 0.0005)
        {
            _lastReportedNativeZoom = reported;
            ZoomChanged?.Invoke(this, reported);
        }

        switch (Mode)
        {
            case PlateCompareMode.Blink:
                DrawBlink(dc);
                break;

            case PlateCompareMode.Difference:
                DrawDifference(dc);
                break;

            case PlateCompareMode.RipVsReference:
                DrawReference(dc);
                break;

            default:
                DrawSlide(dc);
                break;
        }
    }

    /// <summary>
    /// Draw one overlay layer into a device-pixel buffer and blit it 1:1, so the
    /// single-canvas modes stay crisp instead of being resampled by WPF.
    /// </summary>
    private void DrawDevice(DrawingContext dc, ImageSource? image, Int32Rect? clip)
    {
        if (image is not BitmapSource bmp || bmp.PixelWidth <= 0 || bmp.PixelHeight <= 0 || _imageRect.Width <= 0)
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var pw = Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX));
        var ph = Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY));

        if (_deviceBitmap is null || _deviceWidth != pw || _deviceHeight != ph)
        {
            _deviceBitmap = new WriteableBitmap(pw, ph, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Bgra32, null);
            _devicePixels = new byte[pw * ph * 4];
            _deviceWidth = pw;
            _deviceHeight = ph;
            Array.Clear(_devicePixels, 0, _devicePixels.Length);
        }

        var m = new Matrix(_imageRect.Width / bmp.PixelWidth, 0, 0,
                           _imageRect.Height / bmp.PixelHeight, _imageRect.X, _imageRect.Y);

        ViewportResampler.Draw(bmp, new Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight), m,
            dpi.DpiScaleX, dpi.DpiScaleY, pw, ph, _devicePixels!, 1.0, clip);

        _deviceBitmap.WritePixels(new Int32Rect(0, 0, pw, ph), _devicePixels!, pw * 4, 0);
        RenderOptions.SetBitmapScalingMode(_deviceBitmap, BitmapScalingMode.NearestNeighbor);
        dc.DrawImage(_deviceBitmap, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    private int EnsureDevice(out int ph, bool clear = true)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var pw = Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX));
        ph = Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY));

        if (_deviceBitmap is null || _deviceWidth != pw || _deviceHeight != ph)
        {
            _deviceBitmap = new WriteableBitmap(pw, ph, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Bgra32, null);
            _devicePixels = new byte[pw * ph * 4];
            _deviceWidth = pw;
            _deviceHeight = ph;
        }

        if (clear)
            Array.Clear(_devicePixels!, 0, _devicePixels!.Length);

        return pw;
    }

    private void BlitDevice(DrawingContext dc)
    {
        var pw = _deviceWidth;
        var ph = _deviceHeight;
        _deviceBitmap!.WritePixels(new Int32Rect(0, 0, pw, ph), _devicePixels!, pw * 4, 0);
        RenderOptions.SetBitmapScalingMode(_deviceBitmap, BitmapScalingMode.NearestNeighbor);
        dc.DrawImage(_deviceBitmap, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    /// <summary>Coverage averaged over a plate-pixel rectangle (nearest when magnified).</summary>
    private static float SampleCoverage(byte[] cov, int w, int h, double sx0, double sy0, double sx1, double sy1)
    {
        var ix0 = (int)Math.Floor(sx0);
        var iy0 = (int)Math.Floor(sy0);
        var ix1 = (int)Math.Ceiling(sx1);
        var iy1 = (int)Math.Ceiling(sy1);

        if (ix1 <= ix0) ix1 = ix0 + 1;
        if (iy1 <= iy0) iy1 = iy0 + 1;

        ix0 = Math.Clamp(ix0, 0, w - 1);
        iy0 = Math.Clamp(iy0, 0, h - 1);
        ix1 = Math.Clamp(ix1, ix0 + 1, w);
        iy1 = Math.Clamp(iy1, iy0 + 1, h);

        long sum = 0;
        for (var y = iy0; y < iy1; y++)
        {
            var row = y * w;
            for (var x = ix0; x < ix1; x++)
                sum += cov[row + x];
        }

        return sum / (float)((ix1 - ix0) * (iy1 - iy0));
    }

    /// <summary>Draws one separation from its native coverage at device resolution.</summary>
    /// <summary>Viewport in base-image pixels, clamped to the plate.</summary>
    private Rect VisibleBaseRect()
    {
        if (_bw <= 0 || _bh <= 0 || _imageRect.Width <= 0)
            return Rect.Empty;

        var x0 = Math.Max(0, (0 - _imageRect.X) / _imageRect.Width * _bw);
        var y0 = Math.Max(0, (0 - _imageRect.Y) / _imageRect.Height * _bh);
        var x1 = Math.Min(_bw, (ActualWidth - _imageRect.X) / _imageRect.Width * _bw);
        var y1 = Math.Min(_bh, (ActualHeight - _imageRect.Y) / _imageRect.Height * _bh);
        return new Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    /// <summary>Draw a bitmap that covers <paramref name="baseRect"/> of the image, at device resolution.</summary>
    private void DrawNativeBitmap(DrawingContext dc, BitmapSource bmp, Rect baseRect, Int32Rect? clip, bool clear, bool blit)
    {
        if (_bw <= 0 || _bh <= 0 || _imageRect.Width <= 0 || bmp.PixelWidth <= 0)
            return;

        var pw = EnsureDevice(out var ph, clear);
        var dpi = VisualTreeHelper.GetDpi(this);

        var destX = _imageRect.X + baseRect.X / _bw * _imageRect.Width;
        var destY = _imageRect.Y + baseRect.Y / _bh * _imageRect.Height;
        var destW = baseRect.Width / _bw * _imageRect.Width;
        var destH = baseRect.Height / _bh * _imageRect.Height;

        if (destW <= 0 || destH <= 0)
            return;

        var m = new Matrix(destW / bmp.PixelWidth, 0, 0, destH / bmp.PixelHeight, destX, destY);

        ViewportResampler.Draw(bmp, new Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight), m,
            dpi.DpiScaleX, dpi.DpiScaleY, pw, ph, _devicePixels!, 1.0, clip);

        if (blit)
            BlitDevice(dc);
    }

    private void DrawCoverageDevice(DrawingContext dc, PlateLayer? layer, byte[]? cov, int cw, int ch, Int32Rect? clip, bool clear = true, bool blit = true)
    {
        if (layer is null || cov is null || cw <= 0 || ch <= 0 || _imageRect.Width <= 0)
            return;

        if (NativeLayerProvider is not null)
        {
            var side = ReferenceEquals(layer, _rightLayer) ? 1 : 0;
            var native = NativeLayerProvider(side, VisibleBaseRect());
            if (native is { } n && n.BaseRect.Width > 0)
            {
                DrawNativeBitmap(dc, n.Bitmap, n.BaseRect, clip, clear, blit);
                return;
            }
        }

        var pw = EnsureDevice(out var ph, clear);
        var dpi = VisualTreeHelper.GetDpi(this);
        var rect = _imageRect;

        var x0 = Math.Max(0, clip?.X ?? 0);
        var y0 = Math.Max(0, clip?.Y ?? 0);
        var x1 = Math.Min(pw, clip is { } c0 ? c0.X + c0.Width : pw);
        var y1 = Math.Min(ph, clip is { } c1 ? c1.Y + c1.Height : ph);

        var kr = 1f - layer.R / 255f;
        var kg = 1f - layer.G / 255f;
        var kb = 1f - layer.B / 255f;

        for (var py = y0; py < y1; py++)
        {
            var dy0 = py / dpi.DpiScaleY;
            var dy1 = (py + 1) / dpi.DpiScaleY;
            if (dy1 < rect.Top || dy0 > rect.Bottom)
                continue;

            var sy0 = (dy0 - rect.Top) / rect.Height * ch;
            var sy1 = (dy1 - rect.Top) / rect.Height * ch;

            for (var px = x0; px < x1; px++)
            {
                var dx0 = px / dpi.DpiScaleX;
                var dx1 = (px + 1) / dpi.DpiScaleX;
                if (dx1 < rect.Left || dx0 > rect.Right)
                    continue;

                var sx0 = (dx0 - rect.Left) / rect.Width * cw;
                var sx1 = (dx1 - rect.Left) / rect.Width * cw;

                var c = SampleCoverage(cov, cw, ch, sx0, sy0, sx1, sy1) / 255f;
                var o = (py * pw + px) * 4;
                _devicePixels![o] = (byte)(255f * (1f - c * kb));
                _devicePixels[o + 1] = (byte)(255f * (1f - c * kg));
                _devicePixels[o + 2] = (byte)(255f * (1f - c * kr));
                _devicePixels[o + 3] = 255;
            }
        }

        if (blit)
            BlitDevice(dc);
    }

    /// <summary>Per-pixel difference straight from both native coverages.</summary>
    private void DrawDifferenceDevice(DrawingContext dc)
    {
        if (_leftCoverage is null && _rightCoverage is null)
            return;

        var pw = EnsureDevice(out var ph);
        var dpi = VisualTreeHelper.GetDpi(this);
        var rect = _imageRect;

        var leftCov = _leftCoverage;
        var rightCov = _rightCoverage;
        var lw = _leftW;
        var lh = _leftH;
        var rw = _rightW;
        var rh = _rightH;

        for (var py = 0; py < ph; py++)
        {
            var dy0 = py / dpi.DpiScaleY;
            var dy1 = (py + 1) / dpi.DpiScaleY;
            if (dy1 < rect.Top || dy0 > rect.Bottom)
                continue;

            for (var px = 0; px < pw; px++)
            {
                var dx0 = px / dpi.DpiScaleX;
                var dx1 = (px + 1) / dpi.DpiScaleX;
                if (dx1 < rect.Left || dx0 > rect.Right)
                    continue;

                var nx0 = (dx0 - rect.Left) / rect.Width;
                var nx1 = (dx1 - rect.Left) / rect.Width;
                var ny0 = (dy0 - rect.Top) / rect.Height;
                var ny1 = (dy1 - rect.Top) / rect.Height;

                var l = leftCov is null ? 0f : SampleCoverage(leftCov, lw, lh, nx0 * lw, ny0 * lh, nx1 * lw, ny1 * lh);
                var r = rightCov is null ? 0f : SampleCoverage(rightCov, rw, rh, nx0 * rw, ny0 * rh, nx1 * rw, ny1 * rh);
                var delta = Math.Abs(l - r);

                var o = (py * pw + px) * 4;
                if (delta > DifferenceThreshold)
                {
                    _devicePixels![o] = 40;
                    _devicePixels[o + 1] = 40;
                    _devicePixels[o + 2] = 235;
                }
                else
                {
                    var common = (byte)(245 - Math.Min(l, r) / 3);
                    _devicePixels![o] = common;
                    _devicePixels[o + 1] = common;
                    _devicePixels[o + 2] = common;
                }

                _devicePixels[o + 3] = 255;
            }
        }

        BlitDevice(dc);
    }

    /// <summary>
    /// Signature of everything that changes the rendered overlay pixels (zoom, pan, size,
    /// mode, split, threshold, plates). Chrome like badges is not part of it.
    /// </summary>
    private long ComputeFrameKey(int modeTag, bool includeFlip = true)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var hash = new HashCode();
        hash.Add(modeTag);
        hash.Add(_platesVersion);
        hash.Add(ActualWidth);
        hash.Add(ActualHeight);
        hash.Add(dpi.DpiScaleX);
        hash.Add(dpi.DpiScaleY);
        hash.Add(_imageRect.X);
        hash.Add(_imageRect.Y);
        hash.Add(_imageRect.Width);
        hash.Add(_imageRect.Height);
        hash.Add(_slide);
        if (includeFlip)
            hash.Add(_showRight);
        hash.Add(DifferenceThreshold);
        return hash.ToHashCode();
    }

    /// <summary>Drops the cached frame, e.g. when native pixels for the region arrive.</summary>
    public void InvalidateFrame()
    {
        _cacheKey = long.MinValue;
        InvalidateVisual();
    }

    /// <summary>Shows the cached frame when nothing pixel-relevant changed.</summary>
    private bool TryBlitCached(long key, DrawingContext dc)
    {
        if (_deviceBitmap is null || key != _cacheKey)
            return false;

        dc.DrawImage(_deviceBitmap, new Rect(0, 0, ActualWidth, ActualHeight));
        return true;
    }

    private void DrawSlide(DrawingContext dc)
    {
        var splitX = _imageRect.Left + _imageRect.Width * _slide;
        var key = ComputeFrameKey(1) ^ splitX.GetHashCode();

        if (!TryBlitCached(key, dc))
        {
            DrawCoverageDevice(dc, _leftLayer, _leftCoverage, _leftW, _leftH, null, clear: true, blit: false);

            if (_right is not null)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                var clipWidth = (int)Math.Max(0, Math.Round(splitX * dpi.DpiScaleX));
                DrawCoverageDevice(dc, _rightLayer, _rightCoverage, _rightW, _rightH,
                    new Int32Rect(0, 0, clipWidth, _deviceHeight), clear: false, blit: false);
            }

            BlitDevice(dc);
            _cacheKey = key;
        }

        var pen = new Pen(new SolidColorBrush(Color.FromRgb(255, 190, 60)), 1.5);
        pen.Freeze();
        dc.DrawLine(pen, new Point(splitX, _imageRect.Top), new Point(splitX, _imageRect.Bottom));

        var handle = new SolidColorBrush(Color.FromRgb(255, 190, 60));
        handle.Freeze();
        dc.DrawEllipse(handle, null, new Point(splitX, _imageRect.Top + _imageRect.Height / 2), 7, 7);

        DrawBadge(dc, "A", _imageRect.Left + 10, _imageRect.Top + 10);
        DrawBadge(dc, "B", _imageRect.Right - 30, _imageRect.Top + 10);
    }

    private void DrawBlink(DrawingContext dc)
    {
        // Both frames are cached: the blink timer then only blits, never re-samples coverage.
        var key = ComputeFrameKey(2, includeFlip: false);
        var cachedFrame = _showRight ? _blinkB : _blinkA;
        var cachedKey = _showRight ? _blinkKeyB : _blinkKeyA;

        if (cachedFrame is not null && cachedKey == key)
        {
            dc.DrawImage(cachedFrame, new Rect(0, 0, ActualWidth, ActualHeight));
        }
        else
        {
            if (_showRight)
                DrawCoverageDevice(dc, _rightLayer, _rightCoverage, _rightW, _rightH, null);
            else
                DrawCoverageDevice(dc, _leftLayer, _leftCoverage, _leftW, _leftH, null);

            if (_deviceBitmap is not null)
            {
                var frozen = new WriteableBitmap(_deviceBitmap);
                frozen.Freeze();

                if (_showRight) { _blinkB = frozen; _blinkKeyB = key; }
                else { _blinkA = frozen; _blinkKeyA = key; }
            }
        }

        DrawBadge(dc, _showRight ? "B" : "A", _imageRect.Left + 10, _imageRect.Top + 10);
    }

    private void DrawDifference(DrawingContext dc)
    {
        if (_difference is null)
        {
            _difference = BuildDifferenceBitmap(out var built);
            _differencePercent = built;
        }

        if (_difference is null)
        {
            DrawCentred(dc, "Nothing to compare", ActualHeight / 2);
            return;
        }

        DifferencePercent = _differencePercent;
        DifferenceMeasured?.Invoke(this, _differencePercent);

        var diffKey = ComputeFrameKey(3);
        if (!TryBlitCached(diffKey, dc))
        {
            DrawDifferenceDevice(dc);
            _cacheKey = diffKey;
        }
        DrawBadge(dc, $"{_differencePercent:0.00}% differs", _imageRect.Left + 10, _imageRect.Top + 10);
    }

    private void DrawReference(DrawingContext dc)
    {
        if (_reference is null || _comparison is null)
        {
            DrawCentred(dc, "Load a reference PDF or image to compare", ActualHeight / 2);
            return;
        }

        DrawDevice(dc, _reference, null);

        var text = _comparison.IsClean
            ? "No RIP errors"
            : $"Missing {_comparison.Missing:N0}  Extra {_comparison.Extra:N0}  ({_comparison.ErrorPercentOfInk:0.00}% of ink)";

        DrawBadge(dc, text, _imageRect.Left + 10, _imageRect.Top + 10);
    }

    private void DrawCentred(DrawingContext dc, string text, double y)
    {
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12,
            new SolidColorBrush(Color.FromRgb(154, 167, 194)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(formatted, new Point((ActualWidth - formatted.Width) / 2, y - formatted.Height / 2));
    }

    private void DrawBadge(DrawingContext dc, string text, double x, double y)
    {
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var background = new SolidColorBrush(Color.FromArgb(215, 15, 21, 32));
        background.Freeze();
        dc.DrawRoundedRectangle(background, null,
            new Rect(x - 4, y - 2, formatted.Width + 8, formatted.Height + 4), 3, 3);
        dc.DrawText(formatted, new Point(x, y));
    }

    // ---------------------------------------------------------------- slide interaction

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (e.ClickCount == 2)
        {
            ResetView();
            e.Handled = true;
            return;
        }

        if (Mode != PlateCompareMode.Slide)
            return;

        CaptureMouse();
        UpdateSlide(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_panning)
        {
            var p = e.GetPosition(this);
            _camera = _panStartCamera.PannedBy(p.X - _panStart.X, p.Y - _panStart.Y);
            InvalidateVisual();
            return;
        }

        if (Mode == PlateCompareMode.Slide && IsMouseCaptured)
            UpdateSlide(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_panning && IsMouseCaptured)
            ReleaseMouseCapture();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_panning || e.ChangedButton != MouseButton.Middle)
            return;

        _panning = false;
        ReleaseMouseCapture();
        Cursor = Mode == PlateCompareMode.Slide ? Cursors.SizeWE : Cursors.Arrow;
    }

    /// <summary>Zoom in plate terms: 1.0 shows one plate pixel per DIP, exactly like the main canvas.</summary>
    /// <summary>
    /// Supplies a native-resolution bitmap for one side (0 = A, 1 = B) covering the
    /// requested base-image rect, plus the base rect that bitmap represents.
    /// </summary>
    public Func<int, Rect, (BitmapSource Bitmap, Rect BaseRect)?>? NativeLayerProvider;

    public double NativeZoom => _cameraReady ? _camera.NativeScale : _requestedNativeZoom;

    /// <summary>
    /// The overlay camera as a <see cref="ViewTransform"/>, in the same native plate pixels as
    /// the side-by-side panes, so the readout can come from one type in every mode.
    /// </summary>
    public ViewTransform View
    {
        get
        {
            var w = Math.Max(1.0, ActualWidth);
            var h = Math.Max(1.0, ActualHeight);

            if (!_cameraReady)
                return FitCamera();

            return Math.Abs(_camera.ViewportWidth - w) > 0.5 || Math.Abs(_camera.ViewportHeight - h) > 0.5
                ? _camera.Resized(w, h)
                : _camera;
        }
    }

    /// <summary>
    /// Where the plate lands on screen for a camera. Pure, so the overlay geometry that the
    /// render path depends on (100 % = one plate pixel per DIP) can be tested without a window.
    /// </summary>
    internal static Rect ImageRectFor(ViewTransform camera, double plateWidth, double plateHeight)
    {
        var scale = camera.NativeScale;

        return new Rect(camera.ViewportWidth / 2.0 - camera.CentreX * scale,
                        camera.ViewportHeight / 2.0 - camera.CentreY * scale,
                        Math.Max(1.0, plateWidth) * scale,
                        Math.Max(1.0, plateHeight) * scale);
    }

    /// <summary>Native height of the plate, derived from the decoded bitmap's aspect.</summary>
    private double PlateHeight =>
        _bw > 0 && _bh > 0 ? Math.Max(1, _plateWidth) * (_bh / (double)_bw) : Math.Max(1, _plateWidth);

    /// <summary>Fit the whole plate in the viewport with the same 92 % fill the panes use.</summary>
    private ViewTransform FitCamera() =>
        ViewTransform.Fit(Math.Max(1, _plateWidth), PlateHeight,
                          Math.Max(1.0, ActualWidth), Math.Max(1.0, ActualHeight), 0.92);

    private double FitNativeScale() => FitCamera().NativeScale;

    /// <summary>
    /// Overlay zoom range. The floor keeps the old 0.25× of fit, but 100 % must always be
    /// reachable: capping at 24× fit left the overlay stuck well below the panes' 1:1 zoom
    /// whenever the fitted plate was small on screen (e.g. 42 % for a 1.75 % fit).
    /// </summary>
    private double MinNativeScale => Math.Max(ViewTransform.MinNativeScale, FitNativeScale() * MinZoom);

    private double MaxNativeScale => ViewTransform.MaxNativeScale;

    /// <summary>Keeps the camera's viewport in step with the control, fitting on first use.</summary>
    private void EnsureCamera()
    {
        var w = Math.Max(1.0, ActualWidth);
        var h = Math.Max(1.0, ActualHeight);

        if (!_cameraReady)
        {
            _camera = FitCamera();
            _cameraReady = true;

            if (!double.IsNaN(_pendingNativeZoom))
            {
                ApplyNativeZoom(_pendingNativeZoom);
                _pendingNativeZoom = double.NaN;
            }

            return;
        }

        if (Math.Abs(_camera.ViewportWidth - w) > 0.5 || Math.Abs(_camera.ViewportHeight - h) > 0.5)
            _camera = _camera.Resized(w, h);
    }

    /// <summary>Set the scale (clamped) and centre on the plate — the old SetNativeZoom semantics.</summary>
    private void ApplyNativeZoom(double nativeZoom)
    {
        var scale = Math.Clamp(nativeZoom, MinNativeScale, MaxNativeScale);
        _camera = new ViewTransform(scale,
                                    Math.Max(1, _plateWidth) / 2.0,
                                    PlateHeight / 2.0,
                                    _camera.ViewportWidth, _camera.ViewportHeight);
    }

    /// <summary>Zoom so the overlay matches the main canvas at <paramref name="nativeZoom"/>.</summary>
    public void SetNativeZoom(double nativeZoom)
    {
        _requestedNativeZoom = nativeZoom;

        if (!_cameraReady || _bw <= 0 || _bh <= 0 || _plateWidth <= 0)
        {
            _pendingNativeZoom = nativeZoom;
            return;
        }

        EnsureCamera();
        ApplyNativeZoom(nativeZoom);
        _pendingNativeZoom = double.NaN;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_bw <= 0 || _bh <= 0)
            return;

        EnsureCamera();

        var pos = e.GetPosition(this);
        var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        var current = _camera.NativeScale;
        var next = Math.Clamp(current * factor, MinNativeScale, MaxNativeScale);
        if (Math.Abs(next - current) < current * 1e-6)
            return;

        // Keep the plate pixel under the cursor stationary while zooming.
        _camera = _camera.ZoomedAt(next / current, pos.X, pos.Y);

        ZoomChanged?.Invoke(this, NativeZoom);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle)
            return;

        CaptureMouse();
        _panning = true;
        EnsureCamera();
        _panStart = e.GetPosition(this);
        _panStartCamera = _camera;
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void UpdateSlide(double x)
    {
        if (_imageRect.Width <= 0)
            return;

        _slide = Math.Clamp((x - _imageRect.Left) / _imageRect.Width, 0.02, 0.98);
        InvalidateVisual();
    }
}
