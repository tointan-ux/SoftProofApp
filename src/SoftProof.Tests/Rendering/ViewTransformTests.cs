using FluentAssertions;
using SoftProof.Viewer;
using Xunit;

namespace SoftProof.Tests.Rendering;

public class ViewTransformTests
{
    private const double PlateWidth = 11_811;    // 2400 dpi A3-ish plate in native pixels
    private const double PlateHeight = 8_268;
    private const double ViewportWidth = 1_600;
    private const double ViewportHeight = 1_000;

    [Fact]
    public void Fit_centres_the_plate_and_keeps_it_inside_the_viewport()
    {
        var view = ViewTransform.Fit(PlateWidth, PlateHeight, ViewportWidth, ViewportHeight);

        view.CentreX.Should().Be(PlateWidth / 2);
        view.CentreY.Should().Be(PlateHeight / 2);

        var (left, top) = view.NativeToDip(0, 0);
        var (right, bottom) = view.NativeToDip(PlateWidth, PlateHeight);

        left.Should().BeGreaterThan(0);
        top.Should().BeGreaterThan(0);
        right.Should().BeLessThan(ViewportWidth);
        bottom.Should().BeLessThan(ViewportHeight);
        (right - left).Should().BeLessThanOrEqualTo(ViewportWidth);
        (bottom - top).Should().BeLessThanOrEqualTo(ViewportHeight);
    }

    [Fact]
    public void Fit_of_a_small_plate_does_not_exceed_one_hundred_percent()
    {
        var view = ViewTransform.Fit(300, 200, ViewportWidth, ViewportHeight);

        view.NativeScale.Should().BeLessThanOrEqualTo(ViewTransform.MaxNativeScale);
    }

    [Fact]
    public void Centre_of_the_plate_maps_to_the_centre_of_the_viewport()
    {
        var view = new ViewTransform(0.5, 500, 400, ViewportWidth, ViewportHeight);

        var (x, y) = view.NativeToDip(500, 400);

        x.Should().BeApproximately(ViewportWidth / 2, 1e-9);
        y.Should().BeApproximately(ViewportHeight / 2, 1e-9);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(123.5, 987.25)]
    [InlineData(PlateWidth, PlateHeight)]
    public void Native_to_dip_round_trips(double nativeX, double nativeY)
    {
        var view = new ViewTransform(0.37, 4_000, 3_000, ViewportWidth, ViewportHeight);

        var (dipX, dipY) = view.NativeToDip(nativeX, nativeY);
        var (backX, backY) = view.DipToNative(dipX, dipY);

        backX.Should().BeApproximately(nativeX, 1e-6);
        backY.Should().BeApproximately(nativeY, 1e-6);
    }

    [Fact]
    public void Zooming_keeps_the_anchor_pixel_still()
    {
        var view = new ViewTransform(0.25, 5_000, 4_000, ViewportWidth, ViewportHeight);
        const double anchorX = 900;
        const double anchorY = 300;

        var (beforeX, beforeY) = view.DipToNative(anchorX, anchorY);
        var zoomed = view.ZoomedAt(1.6, anchorX, anchorY);
        var (afterX, afterY) = zoomed.DipToNative(anchorX, anchorY);

        afterX.Should().BeApproximately(beforeX, 1e-6);
        afterY.Should().BeApproximately(beforeY, 1e-6);
        zoomed.NativeScale.Should().BeApproximately(0.4, 1e-9);
    }

    [Fact]
    public void Zoom_never_passes_one_hundred_percent()
    {
        var view = new ViewTransform(0.9, 1_000, 1_000, ViewportWidth, ViewportHeight);

        view.ZoomedBy(4).NativeScale.Should().Be(ViewTransform.MaxNativeScale);
        view.WithNativeScale(3).NativeScale.Should().Be(ViewTransform.MaxNativeScale);
    }

    [Fact]
    public void Zoom_never_goes_below_the_floor()
    {
        var view = new ViewTransform(0.05, 1_000, 1_000, ViewportWidth, ViewportHeight);

        view.ZoomedBy(0.001).NativeScale.Should().Be(ViewTransform.MinNativeScale);
        view.WithNativeScale(0).NativeScale.Should().Be(ViewTransform.MinNativeScale);
    }

    [Fact]
    public void Panning_moves_the_centre_by_drag_distance_over_scale()
    {
        var view = new ViewTransform(0.5, 1_000, 1_000, ViewportWidth, ViewportHeight);

        var panned = view.PannedBy(100, -50);

        panned.CentreX.Should().BeApproximately(800, 1e-9);
        panned.CentreY.Should().BeApproximately(1_100, 1e-9);
        panned.NativeScale.Should().Be(view.NativeScale);
    }

    [Fact]
    public void Visible_rect_at_fit_is_the_whole_plate()
    {
        var view = ViewTransform.Fit(PlateWidth, PlateHeight, ViewportWidth, ViewportHeight);

        var (x, y, w, h) = view.VisiblePlateRect(PlateWidth, PlateHeight);

        x.Should().Be(0);
        y.Should().Be(0);
        w.Should().BeApproximately(PlateWidth, 1e-6);
        h.Should().BeApproximately(PlateHeight, 1e-6);
    }

    [Fact]
    public void Visible_rect_is_clamped_to_the_plate_when_panned_past_the_edge()
    {
        var view = new ViewTransform(1.0, 40, 40, ViewportWidth, ViewportHeight);

        var (x, y, w, h) = view.VisiblePlateRect(PlateWidth, PlateHeight);

        x.Should().Be(0);
        y.Should().Be(0);
        w.Should().BeInRange(0, PlateWidth);
        h.Should().BeInRange(0, PlateHeight);
    }

    [Fact]
    public void Resizing_keeps_the_native_centre_and_scale()
    {
        var view = new ViewTransform(0.6, 2_000, 1_500, ViewportWidth, ViewportHeight);

        var resized = view.Resized(2_000, 1_200);

        resized.NativeScale.Should().Be(0.6);
        resized.CentreX.Should().Be(2_000);
        resized.CentreY.Should().Be(1_500);
        resized.ViewportWidth.Should().Be(2_000);
    }

    [Fact]
    public void Clamping_keeps_the_plate_in_view_but_centres_when_it_fits()
    {
        var zoomedIn = new ViewTransform(1.0, -5_000, -5_000, ViewportWidth, ViewportHeight)
            .ClampedTo(PlateWidth, PlateHeight);

        zoomedIn.CentreX.Should().BeApproximately(ViewportWidth / 2.0, 1e-9);
        zoomedIn.CentreY.Should().BeApproximately(ViewportHeight / 2.0, 1e-9);

        var fitted = new ViewTransform(0.05, 0, 0, ViewportWidth, ViewportHeight)
            .ClampedTo(PlateWidth, PlateHeight);

        fitted.CentreX.Should().Be(PlateWidth / 2);
        fitted.CentreY.Should().Be(PlateHeight / 2);
    }

    [Fact]
    public void Percent_matches_the_native_scale()
    {
        new ViewTransform(1.0, 0, 0, 100, 100).Percent.Should().Be(100);
        new ViewTransform(0.25, 0, 0, 100, 100).Percent.Should().Be(25);
    }

    [Theory]
    [InlineData(0.02)]
    [InlineData(0.05)]
    [InlineData(0.245)]
    [InlineData(0.875)]
    [InlineData(0.9994)]
    [InlineData(1.0)]
    public void Zoom_text_is_identical_to_the_legacy_readout(double nativeZoom)
    {
        // The step-1 migration must not change a single character of the status bar.
        var legacy = $"{nativeZoom * 100:0}%";

        new ViewTransform(nativeZoom, 0, 0, 100, 100).ZoomText.Should().Be(legacy);
    }

    [Fact]
    public void The_constructor_reports_the_scale_it_was_given()
    {
        // Readout fidelity: a fit below the zoom floor must still display what it is.
        new ViewTransform(0.008, 0, 0, 1600, 1000).Percent.Should().BeApproximately(0.8, 1e-9);
        new ViewTransform(2.5, 0, 0, 1600, 1000).Percent.Should().BeApproximately(250, 1e-9);
    }
}
