using System.Windows;

namespace SoftProof.Viewer;

/// <summary>
/// Pure geometry for the navigator: fitting the page thumbnail into a control and converting
/// between thumbnail and image coordinates. Kept out of the WPF control so it can be tested.
/// </summary>
public static class NavigatorMapping
{
    /// <summary>Letterboxed rectangle of the page inside the control (aspect preserved).</summary>
    public static Rect Fit(int imageWidth, int imageHeight, double boxWidth, double boxHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || boxWidth <= 0 || boxHeight <= 0)
            return Rect.Empty;

        var scale = Math.Min(boxWidth / imageWidth, boxHeight / imageHeight);
        var width = imageWidth * scale;
        var height = imageHeight * scale;
        return new Rect((boxWidth - width) / 2, (boxHeight - height) / 2, width, height);
    }

    /// <summary>Image point under a thumbnail point, clamped to the page.</summary>
    public static Point ToImage(Point thumbPoint, Rect thumbRect, int imageWidth, int imageHeight)
    {
        if (thumbRect.IsEmpty || thumbRect.Width <= 0 || thumbRect.Height <= 0)
            return new Point(imageWidth / 2.0, imageHeight / 2.0);

        var x = (thumbPoint.X - thumbRect.X) / thumbRect.Width * imageWidth;
        var y = (thumbPoint.Y - thumbRect.Y) / thumbRect.Height * imageHeight;
        return new Point(Math.Clamp(x, 0, imageWidth), Math.Clamp(y, 0, imageHeight));
    }

    /// <summary>Rectangle in thumbnail space for the visible part of the page.</summary>
    public static Rect Viewport(Rect visibleImage, Rect thumbRect, int imageWidth, int imageHeight)
    {
        if (thumbRect.IsEmpty || imageWidth <= 0 || imageHeight <= 0)
            return Rect.Empty;

        var scaleX = thumbRect.Width / imageWidth;
        var scaleY = thumbRect.Height / imageHeight;

        var left = thumbRect.X + Math.Clamp(visibleImage.Left, 0, imageWidth) * scaleX;
        var top = thumbRect.Y + Math.Clamp(visibleImage.Top, 0, imageHeight) * scaleY;
        var right = thumbRect.X + Math.Clamp(visibleImage.Right, 0, imageWidth) * scaleX;
        var bottom = thumbRect.Y + Math.Clamp(visibleImage.Bottom, 0, imageHeight) * scaleY;

        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    /// <summary>New view centre after dragging the viewport rectangle by a thumbnail delta.</summary>
    public static Point CenterAfterDrag(Point currentCenter, Vector thumbDelta, Rect thumbRect,
                                        int imageWidth, int imageHeight)
    {
        if (thumbRect.IsEmpty || thumbRect.Width <= 0 || thumbRect.Height <= 0)
            return currentCenter;

        var dx = thumbDelta.X / thumbRect.Width * imageWidth;
        var dy = thumbDelta.Y / thumbRect.Height * imageHeight;

        return new Point(
            Math.Clamp(currentCenter.X + dx, 0, imageWidth),
            Math.Clamp(currentCenter.Y + dy, 0, imageHeight));
    }
}
