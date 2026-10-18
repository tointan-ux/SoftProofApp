using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SoftProof.Core.Models;

namespace SoftProof.Viewer;

/// <summary>
/// Overview thumbnail of the page with a draggable viewport rectangle. Essential at plate sizes
/// where the visible region is a tiny fraction of the sheet.
/// </summary>
public class PlateNavigator : FrameworkElement
{
    private const int ThumbnailSize = 320;

    private IReadOnlyList<PlateLayer> _plates = Array.Empty<PlateLayer>();
    private int _imageWidth;
    private int _imageHeight;

    private WriteableBitmap? _thumbnail;
    private Rect _thumbRect = Rect.Empty;
    private Rect _viewport = Rect.Empty;

    private bool _dragging;
    private Point _dragLast;

    public PlateNavigator()
    {
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        MinHeight = 90;
        TextureOptions();
    }

    private static void TextureOptions() { }

    /// <summary>Raised when the user drags or clicks the viewport; payload is an image-space centre.</summary>
    public event EventHandler<Point>? CenterRequested;

    public void SetPlates(IReadOnlyList<PlateLayer> plates, int width, int height)
    {
        _plates = plates;
        _imageWidth = width;
        _imageHeight = height;
        RebuildThumbnail();
        InvalidateVisual();
    }

    public void Clear()
    {
        _plates = Array.Empty<PlateLayer>();
        _imageWidth = _imageHeight = 0;
        _thumbnail = null;
        _viewport = Rect.Empty;
        InvalidateVisual();
    }

    /// <summary>Visible part of the page, in image pixels.</summary>
    public void SetViewport(Rect imageRect)
    {
        _viewport = imageRect;
        InvalidateVisual();
    }

    private void RebuildThumbnail()
    {
        if (_imageWidth <= 0 || _imageHeight <= 0 || _plates.Count == 0)
        {
            _thumbnail = null;
            return;
        }

        // Prefer the coarsest pyramid level: same look, a fraction of the work.
        var layers = _plates.Select(p => p.Pyramid is { } py ? LowestLevelLayer(p, py) : p).ToList();
        var pixels = PlateThumbnail.BuildComposite(layers, _imageWidth, _imageHeight, ThumbnailSize);

        _thumbnail = new WriteableBitmap(ThumbnailSize, ThumbnailSize, 96, 96, PixelFormats.Bgra32, null);
        _thumbnail.WritePixels(new Int32Rect(0, 0, ThumbnailSize, ThumbnailSize),
                               pixels, ThumbnailSize * 4, 0);
        _thumbnail.Freeze();
    }

    private static PlateLayer LowestLevelLayer(PlateLayer source, PlatePyramid pyramid)
    {
        var level = pyramid.Levels[^1];
        return new PlateLayer(source.Separation, level.Width, level.Height, level.Coverage,
                              source.R, source.G, source.B);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        _thumbRect = NavigatorMapping.Fit(_imageWidth, _imageHeight, ActualWidth, ActualHeight);
        if (_thumbnail is null || _thumbRect.IsEmpty)
            return;

        dc.DrawImage(_thumbnail, _thumbRect);
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(70, 82, 106)), 1), _thumbRect);

        if (_viewport.IsEmpty)
            return;

        var vp = NavigatorMapping.Viewport(_viewport, _thumbRect, _imageWidth, _imageHeight);
        var accent = new SolidColorBrush(Color.FromRgb(74, 163, 214));
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(48, 74, 163, 214)), new Pen(accent, 1.5), vp);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_thumbRect.IsEmpty)
            return;

        var p = e.GetPosition(this);
        var centre = NavigatorMapping.ToImage(p, _thumbRect, _imageWidth, _imageHeight);
        CenterRequested?.Invoke(this, centre);

        _dragging = true;
        _dragLast = p;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_dragging || _thumbRect.IsEmpty)
            return;

        var p = e.GetPosition(this);
        var delta = p - _dragLast;
        _dragLast = p;

        var current = _viewport.IsEmpty
            ? new Point(_imageWidth / 2.0, _imageHeight / 2.0)
            : new Point(_viewport.X + _viewport.Width / 2, _viewport.Y + _viewport.Height / 2);

        var centre = NavigatorMapping.CenterAfterDrag(current, delta, _thumbRect, _imageWidth, _imageHeight);
        CenterRequested?.Invoke(this, centre);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }
}
