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
    private double _zoom = 1.0;
    private double _pendingNativeZoom = double.NaN;
    private double _panX;
    private double _panY;
    private bool _panning;
    private Point _panStart;
    private double _panStartX;
    private double _panStartY;
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
        _zoom = 1.0;
        _panX = 0;
        _panY = 0;
        InvalidateVisual();
    }

    public void SetPlates(PlateLayer? left, PlateLayer? right)
    {
        _leftCoverage = left is null ? null : BuildCoverage(left, out _left);
        _rightCoverage = right is null ? null : BuildCoverage(right, out _right);
        _leftW = _left?.PixelWidth ?? 0;
        _leftH = _left?.PixelHeight ?? 0;
        _rightW = _right?.PixelWidth ?? 0;
        _rightH = _right?.PixelHeight ?? 0;
        _difference = null;
        _differencePercent = 0;

        _bw = _left?.PixelWidth ?? _right?.PixelWidth ?? 0;
        _bh = _left?.PixelHeight ?? _right?.PixelHeight ?? 0;
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
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(15, 22, 38)), null,
                         new Rect(0, 0, ActualWidth, ActualHeight));

        if (_bw <= 0 || _bh <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            DrawCentred(dc, "Select two plates to compare", ActualHeight / 2);
            return;
        }

        if (!double.IsNaN(_pendingNativeZoom))
        {
            var pending = BaseScale();
            if (pending > 0)
            {
                _zoom = Math.Clamp(_pendingNativeZoom / pending, MinZoom, MaxZoom);
                _pendingNativeZoom = double.NaN;
            }
        }

        var scale = Math.Min(ActualWidth / _bw, ActualHeight / _bh) * 0.92 * _zoom;
        var w = _bw * scale;
        var h = _bh * scale;
        _imageRect = new Rect((ActualWidth - w) / 2 + _panX, (ActualHeight - h) / 2 + _panY, w, h);

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

    private void DrawSlide(DrawingContext dc)
    {
        if (_left is not null)
            dc.DrawImage(_left, _imageRect);

        var splitX = _imageRect.Left + _imageRect.Width * _slide;

        if (_right is not null)
        {
            dc.PushClip(new RectangleGeometry(new Rect(_imageRect.Left, _imageRect.Top,
                                                       Math.Max(0, splitX - _imageRect.Left), _imageRect.Height)));
            dc.DrawImage(_right, _imageRect);
            dc.Pop();
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
        var bitmap = _showRight ? _right : _left;
        if (bitmap is not null)
            dc.DrawImage(bitmap, _imageRect);

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

        dc.DrawImage(_difference, _imageRect);
        DrawBadge(dc, $"{_differencePercent:0.00}% differs", _imageRect.Left + 10, _imageRect.Top + 10);
    }

    private void DrawReference(DrawingContext dc)
    {
        if (_reference is null || _comparison is null)
        {
            DrawCentred(dc, "Load a reference PDF or image to compare", ActualHeight / 2);
            return;
        }

        dc.DrawImage(_reference, _imageRect);

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
            _panX = _panStartX + (p.X - _panStart.X);
            _panY = _panStartY + (p.Y - _panStart.Y);
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

    /// <summary>Zoom expressed as native plate pixels per device pixel (1.0 = 100 %).</summary>
    public double NativeZoom
    {
        get
        {
            var baseScale = BaseScale();
            return baseScale <= 0 ? 1.0 : _zoom * baseScale;
        }
    }

    private double BaseScale() =>
        _bw <= 0 || _bh <= 0 ? 0 : Math.Min(ActualWidth / _bw, ActualHeight / _bh) * 0.92;

    /// <summary>Zoom to a native scale (1.0 = 100 %) so the toolbar drives overlay modes too.</summary>
    public void SetNativeZoom(double nativeZoom)
    {
        _pendingNativeZoom = nativeZoom;   // applied in OnRender once the bitmaps are sized
        _panX = 0;
        _panY = 0;

        var baseScale = BaseScale();
        if (baseScale > 0)
        {
            _zoom = Math.Clamp(nativeZoom / baseScale, MinZoom, MaxZoom);
            _pendingNativeZoom = double.NaN;
        }

        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_bw <= 0 || _bh <= 0)
            return;

        var pos = e.GetPosition(this);
        var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        var next = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(next - _zoom) < 1e-6)
            return;

        // Keep the image point under the cursor stationary while zooming.
        var cx = (pos.X - ActualWidth / 2 - _panX) / _zoom;
        var cy = (pos.Y - ActualHeight / 2 - _panY) / _zoom;

        _zoom = next;
        _panX = pos.X - ActualWidth / 2 - cx * _zoom;
        _panY = pos.Y - ActualHeight / 2 - cy * _zoom;

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
        _panStart = e.GetPosition(this);
        _panStartX = _panX;
        _panStartY = _panY;
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
