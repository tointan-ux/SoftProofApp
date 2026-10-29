using System.Windows;
using FluentAssertions;
using SoftProof.Viewer;
using Xunit;

namespace SoftProof.Tests.Rendering;

/// <summary>
/// Geometry of the single-canvas compare overlay (Slide / Blink / Difference). These are the
/// numbers the render path uses to place the plate, so they decide whether "100 %" really is
/// one plate pixel per DIP and whether a wheel notch keeps the point under the cursor fixed.
/// </summary>
public class CompareViewGeometryTests
{
    private const double PlateWidth = 11_811;      // 2400 dpi plate
    private const double PlateHeight = 8_268;
    private const double ViewportWidth = 1_600;
    private const double ViewportHeight = 1_000;

    [Fact]
    public void At_one_hundred_percent_the_plate_is_one_pixel_per_dip()
    {
        var camera = new ViewTransform(1.0, PlateWidth / 2, PlateHeight / 2, ViewportWidth, ViewportHeight);

        var rect = PlateCompareView.ImageRectFor(camera, PlateWidth, PlateHeight);

        rect.Width.Should().BeApproximately(PlateWidth, 1e-9);
        rect.Height.Should().BeApproximately(PlateHeight, 1e-9);
    }

    [Theory]
    [InlineData(9331, 8, 74648)]
    [InlineData(1477, 1, 1477)]
    [InlineData(2000, 4, 8000)]
    public void The_hundred_percent_reference_is_the_native_width(int baseWidth, int factor, int expected)
    {
        // The overlay's coverage arrays are base-raster; using that width as the 100 % reference
        // put 1:1 at 8x on an 8x pyramid and left the viewport on a blank margin.
        PlateCompareView.NativeWidth(baseWidth, factor).Should().Be(expected);
    }

    [Fact]
    public void A_centred_camera_puts_the_plate_in_the_middle_of_the_viewport()
    {
        var camera = new ViewTransform(0.25, PlateWidth / 2, PlateHeight / 2, ViewportWidth, ViewportHeight);

        var rect = PlateCompareView.ImageRectFor(camera, PlateWidth, PlateHeight);

        (rect.X + rect.Width / 2).Should().BeApproximately(ViewportWidth / 2, 1e-9);
        (rect.Y + rect.Height / 2).Should().BeApproximately(ViewportHeight / 2, 1e-9);
    }

    [Fact]
    public void Panning_moves_the_rect_by_the_drag_distance()
    {
        var camera = new ViewTransform(0.5, PlateWidth / 2, PlateHeight / 2, ViewportWidth, ViewportHeight);

        var before = PlateCompareView.ImageRectFor(camera, PlateWidth, PlateHeight);
        var after = PlateCompareView.ImageRectFor(camera.PannedBy(-120, 45), PlateWidth, PlateHeight);

        // Drag left by 120 DIP: the plate slides left on screen by the same distance.
        (before.X - after.X).Should().BeApproximately(120, 1e-9);
        (before.Y - after.Y).Should().BeApproximately(-45, 1e-9);
        after.Width.Should().BeApproximately(before.Width, 1e-12);
    }

    [Fact]
    public void Zooming_at_the_cursor_keeps_that_plate_pixel_under_it()
    {
        // The overlay's wheel handler is exactly ViewTransform.ZoomedAt + ImageRectFor; if the
        // two disagree the image creeps under the cursor on every notch.
        var camera = new ViewTransform(0.3, PlateWidth / 2, PlateHeight / 2, ViewportWidth, ViewportHeight);
        const double cursorX = 1_180;
        const double cursorY = 260;

        var (plateX, plateY) = camera.DipToNative(cursorX, cursorY);
        var zoomed = camera.ZoomedAt(1.15, cursorX, cursorY);

        var rect = PlateCompareView.ImageRectFor(zoomed, PlateWidth, PlateHeight);
        var backX = (cursorX - rect.X) / zoomed.NativeScale;
        var backY = (cursorY - rect.Y) / zoomed.NativeScale;

        backX.Should().BeApproximately(plateX, 1e-6);
        backY.Should().BeApproximately(plateY, 1e-6);
    }

    [Fact]
    public void Fit_shows_the_whole_plate_inside_the_viewport()
    {
        var camera = ViewTransform.Fit(PlateWidth, PlateHeight, ViewportWidth, ViewportHeight, 0.92);

        var rect = PlateCompareView.ImageRectFor(camera, PlateWidth, PlateHeight);

        rect.X.Should().BeGreaterThanOrEqualTo(0);
        rect.Y.Should().BeGreaterThanOrEqualTo(0);
        rect.Right.Should().BeLessThanOrEqualTo(ViewportWidth);
        rect.Bottom.Should().BeLessThanOrEqualTo(ViewportHeight);
    }
}
