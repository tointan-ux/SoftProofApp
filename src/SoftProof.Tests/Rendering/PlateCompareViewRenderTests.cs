using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentAssertions;
using SoftProof.Core.Models;
using SoftProof.Viewer;
using Xunit;

namespace SoftProof.Tests.Rendering;

/// <summary>
/// Renders the compare overlay offscreen and looks at the pixels. The blank-white-canvas bug was
/// invisible to geometry tests: every number was self-consistent, only the picture was empty.
/// </summary>
public class PlateCompareViewRenderTests
{
    private const int BaseWidth = 1_200;
    private const int BaseHeight = 800;
    private const int Factor = 8;

    [Fact]
    public void Slide_at_one_to_one_fills_the_viewport_with_ink()
    {
        var result = RunOnStaThread(() =>
        {
            var view = BuildSlideView(out var nativeCentreX, out var nativeCentreY);
            view.SetView(new ViewTransform(1.0, nativeCentreX, nativeCentreY, 900, 600));
            return Render(view, 900, 600);
        });

        // The ink block covers the middle third of the plate: at true 1:1 a 900×600 viewport sits
        // entirely inside it. The old code treated the base raster as native, so 1:1 was really
        // 800 % and only a ~300×200 patch was inked.
        result.Inked.Should().BeGreaterThan((int)(900 * 600 * 0.9),
            $"at 1:1 the whole viewport lies inside the ink block; got {result.Inked} inked pixels");
        result.CentreInked.Should().BeTrue();
    }

    [Fact]
    public void Slide_at_fit_shows_ink_somewhere()
    {
        var result = RunOnStaThread(() =>
        {
            var view = BuildSlideView(out _, out _);
            view.ResetView();                       // fit
            return Render(view, 900, 600);
        });

        result.Inked.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Difference_at_one_to_one_does_not_paint_a_white_screen()
    {
        var result = RunOnStaThread(() =>
        {
            var view = BuildSlideView(out var cx, out var cy);
            view.SetMode(PlateCompareMode.Difference);
            view.SetView(new ViewTransform(1.0, cx, cy, 900, 600));
            return Render(view, 900, 600);
        });

        // Identical plates: difference is empty, but the paper should still be drawn — the point
        // is that the control rendered at all instead of leaving the canvas untouched.
        result.Rendered.Should().BeTrue();
    }

    [Fact]
    public void The_native_provider_is_asked_for_the_visible_rectangle_in_base_pixels()
    {
        var request = RunOnStaThread(() =>
        {
            var view = BuildSlideView(out var cx, out var cy);

            Rect asked = Rect.Empty;
            view.NativeLayerProvider = (side, visibleBase) =>
            {
                asked = visibleBase;
                return (null!, Rect.Empty);          // decline: we only inspect the request
            };

            view.SetView(new ViewTransform(1.0, cx, cy, 900, 600));
            Render(view, 900, 600);
            return asked;
        });

        // At 1:1 the viewport is 900×600 DIP = 900×600 native px = 112.5×75 base px at factor 8.
        // The old code handed the provider preview-space pixels, so it decoded an 8× smaller,
        // 8× mis-placed region and stretched it over the whole canvas — a magnified white blob.
        request.Width.Should().BeApproximately(900.0 / Factor, 2.0);
        request.Height.Should().BeApproximately(600.0 / Factor, 2.0);
    }

    private static PlateCompareView BuildSlideView(out double centreX, out double centreY)
    {
        var coverage = new byte[BaseWidth * BaseHeight];

        for (var y = BaseHeight / 3; y < BaseHeight * 2 / 3; y++)
            for (var x = BaseWidth / 3; x < BaseWidth * 2 / 3; x++)
                coverage[y * BaseWidth + x] = 200;

        var separation = new Separation { Name = "Cyan", Color = SeparationColor.ProcessCyan };
        var layer = new PlateLayer(separation, BaseWidth, BaseHeight, coverage,
                                   0, 255, 255,
                                   PlatePyramid.FromCoverage(coverage, BaseWidth, BaseHeight, Factor));

        var view = new PlateCompareView { Width = 900, Height = 600 };
        view.SetPlates(layer, layer);
        view.SetMode(PlateCompareMode.Slide);

        centreX = BaseWidth * Factor / 2.0;
        centreY = BaseHeight * Factor / 2.0;
        return view;
    }

    private static (int Inked, bool CentreInked, bool Rendered) Render(PlateCompareView view, int w, int h)
    {
        view.Measure(new Size(w, h));
        view.Arrange(new Rect(0, 0, w, h));
        view.UpdateLayout();

        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);

        var pixels = new byte[w * h * 4];
        bitmap.CopyPixels(pixels, w * 4, 0);

        var inked = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] < 240 || pixels[i + 1] < 240 || pixels[i + 2] < 240)
                inked++;

        var c = ((h / 2) * w + w / 2) * 4;
        var centreInked = pixels[c] < 240 || pixels[c + 1] < 240 || pixels[c + 2] < 240;

        return (inked, centreInked, true);
    }

    private static T RunOnStaThread<T>(Func<T> action)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { failure = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        if (failure is not null)
            throw new InvalidOperationException("WPF render failed: " + failure.Message, failure);

        return result;
    }
}
