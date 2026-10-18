using System.Windows;

namespace SoftProof.Viewer;

/// <summary>
/// Margins and offset of a content box relative to the plate, in millimetres.
/// <para>
/// Four margins are measured from each plate edge to the corresponding content-box edge.
/// The offset is the vector from the plate centre to the content centre, because a box can have
/// even margins and still sit off-centre, and that is what a press operator checks.
/// </para>
/// </summary>
public readonly record struct ContentBoxLayout(
    double LeftMm,
    double TopMm,
    double RightMm,
    double BottomMm,
    double OffsetXMm,
    double OffsetYMm)
{
    /// <summary>Straight-line distance between plate centre and content centre, in mm.</summary>
    public double OffsetDistanceMm => Math.Sqrt(OffsetXMm * OffsetXMm + OffsetYMm * OffsetYMm);

    public string OffsetText => $"Δx {OffsetXMm:0.0}  Δy {OffsetYMm:0.0} mm";
}

public static class ContentBoxOverlay
{
    /// <summary>
    /// Compute margins/offset, or null when the inputs cannot describe a box (empty rect,
    /// unknown resolution, degenerate plate).
    /// </summary>
    public static ContentBoxLayout? Compute(Rect content, double plateWidthPx, double plateHeightPx,
                                            double pixelsPerMm)
    {
        if (pixelsPerMm <= 0 || plateWidthPx <= 0 || plateHeightPx <= 0)
            return null;
        if (content.IsEmpty || content.Width <= 0 || content.Height <= 0)
            return null;

        // Clamp to the plate so a bad bounds value cannot produce negative margins.
        var left = Math.Clamp(content.Left, 0, plateWidthPx);
        var top = Math.Clamp(content.Top, 0, plateHeightPx);
        var right = Math.Clamp(content.Right, 0, plateWidthPx);
        var bottom = Math.Clamp(content.Bottom, 0, plateHeightPx);
        if (right <= left || bottom <= top)
            return null;

        var plateCentreX = plateWidthPx / 2;
        var plateCentreY = plateHeightPx / 2;
        var contentCentreX = (left + right) / 2;
        var contentCentreY = (top + bottom) / 2;

        return new ContentBoxLayout(
            LeftMm: left / pixelsPerMm,
            TopMm: top / pixelsPerMm,
            RightMm: (plateWidthPx - right) / pixelsPerMm,
            BottomMm: (plateHeightPx - bottom) / pixelsPerMm,
            OffsetXMm: (contentCentreX - plateCentreX) / pixelsPerMm,
            OffsetYMm: (contentCentreY - plateCentreY) / pixelsPerMm);
    }
}
