using System.Windows;
using SoftProof.Viewer;
using Xunit;

namespace SoftProof.Tests.Analysis;

/// <summary>
/// Overlay geometry: margins are measured from each plate edge to the content box, and the
/// offset is plate centre to content centre — the number that catches an "even margins,
/// shifted artwork" mistake that four margins alone can hide.
/// </summary>
public class ContentBoxOverlayTests
{
    [Fact]
    public void ComputesFourMarginsAndCentreOffset()
    {
        // 1000 x 800 px plate at 10 px/mm; content 800 x 600 at (100, 50).
        var layout = ContentBoxOverlay.Compute(new Rect(100, 50, 800, 600), 1000, 800, 10);

        Assert.NotNull(layout);
        var value = layout.Value;

        Assert.Equal(10.0, value.LeftMm, 3);
        Assert.Equal(5.0, value.TopMm, 3);
        Assert.Equal(10.0, value.RightMm, 3);
        Assert.Equal(15.0, value.BottomMm, 3);

        // Plate centre (500, 400); content centre (500, 350): 5 mm up, no horizontal shift.
        Assert.Equal(0.0, value.OffsetXMm, 3);
        Assert.Equal(-5.0, value.OffsetYMm, 3);
        Assert.Equal(5.0, value.OffsetDistanceMm, 3);
        Assert.Contains("Δx 0.0", value.OffsetText);
    }

    [Fact]
    public void OffsetCapturesAsymmetricMarginsThatLookEven()
    {
        // Left 10 / right 10 mm looks centred horizontally, but with 30 mm top and 50 mm bottom
        // the box sits 10 mm above the plate centre (negative dy).
        var layout = ContentBoxOverlay.Compute(new Rect(100, 300, 800, 200), 1000, 1000, 10);
        Assert.NotNull(layout);

        Assert.Equal(10.0, layout!.Value.LeftMm, 3);
        Assert.Equal(10.0, layout.Value.RightMm, 3);
        Assert.Equal(30.0, layout.Value.TopMm, 3);
        Assert.Equal(50.0, layout.Value.BottomMm, 3);
        Assert.Equal(-10.0, layout.Value.OffsetYMm, 3);
        Assert.Equal(0.0, layout.Value.OffsetXMm, 3);
    }

    [Fact]
    public void ClampsContentThatRunsPastThePlateEdge()
    {
        var layout = ContentBoxOverlay.Compute(new Rect(-50, -20, 1200, 900), 1000, 800, 10);

        Assert.NotNull(layout);
        Assert.Equal(0.0, layout!.Value.LeftMm, 3);
        Assert.Equal(0.0, layout.Value.TopMm, 3);
        Assert.Equal(0.0, layout.Value.RightMm, 3);
        Assert.Equal(0.0, layout.Value.BottomMm, 3);
    }

    [Theory]
    [InlineData(0, 800, 10)]     // unknown resolution
    [InlineData(1000, 0, 10)]    // degenerate plate
    [InlineData(1000, 800, 0)]   // no pixels-per-mm
    public void RejectsUnusableInputs(double width, double height, double pixelsPerMm)
    {
        Assert.Null(ContentBoxOverlay.Compute(new Rect(100, 100, 200, 200), width, height, pixelsPerMm));
    }

    [Fact]
    public void RejectsEmptyContentBox()
    {
        Assert.Null(ContentBoxOverlay.Compute(Rect.Empty, 1000, 800, 10));
    }
}
