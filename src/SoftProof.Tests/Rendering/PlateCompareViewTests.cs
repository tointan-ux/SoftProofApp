using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SoftProof.Core.Models;
using SoftProof.Viewer;
using Xunit;

namespace SoftProof.Tests.Rendering;

/// <summary>
/// Headless renders of the single-canvas compare control. These catch "the canvas went blank"
/// regressions, which are otherwise only visible on a screen.
/// </summary>
public class PlateCompareViewTests
{
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null)
            throw new Xunit.Sdk.XunitException(error.ToString());
    }

    private static PlateLayer Plate(SeparationColor color, int width, int height,
                                    Func<int, int, bool> ink)
    {
        var coverage = new byte[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (ink(x, y))
                    coverage[y * width + x] = 255;

        var separation = new Separation { Name = color.ToString(), Color = color };
        return new PlateLayer(separation, width, height, coverage, 0, 174, 239, resolution: 2400);
    }

    private static byte[] Render(PlateCompareView view, int width, int height)
    {
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();

        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(view);

        var pixels = new byte[width * height * 4];
        bmp.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    /// <summary>Share of pixels that are ink-coloured rather than white paper or background.</summary>
    private static double InkedShare(byte[] pixels)
    {
        var inked = 0;
        var total = pixels.Length / 4;

        for (var i = 0; i < total; i++)
        {
            var b = pixels[i * 4];
            var g = pixels[i * 4 + 1];
            var r = pixels[i * 4 + 2];

            // Cyan ink darkens red heavily; white paper and the dark canvas background do not
            // land in this range.
            if (r < 120 && b > 150)
                inked++;
        }

        return inked / (double)total;
    }

    /// <summary>Mirrors what MainWindow.ApplyOverlay does after SetPlates/SetMode.</summary>
    private static void ApplyOverlayState(PlateCompareView view, PlateLayer plate, double nativeZoom = 1.0)
    {
        view.SetPlateWidth(plate.Width);
        view.SetNativeZoom(nativeZoom);
    }

    [Fact]
    public void SlideMode_DrawsBothPlates_RatherThanABlankSheet()
    {
        RunSta(() =>
        {
            var left = Plate(SeparationColor.ProcessCyan, 64, 32, (x, _) => x < 32);
            var right = Plate(SeparationColor.ProcessMagenta, 64, 32, (_, _) => true);

            var view = new PlateCompareView { Width = 400, Height = 300 };
            view.SetPlates(left, right);
            view.SetMode(PlateCompareMode.Slide);
            ApplyOverlayState(view, left);

            var pixels = Render(view, 400, 300);

            // A blank sheet renders as paper-white or as the background; both leave no ink.
            Assert.True(InkedShare(pixels) > 0.005,
                        $"expected ink in slide mode, got {InkedShare(pixels):P1}");
        });
    }

    [Fact]
    public void SlideMode_WithoutARightPlate_StillShowsTheLeft()
    {
        RunSta(() =>
        {
            var left = Plate(SeparationColor.ProcessCyan, 64, 32, (_, _) => true);
            var view = new PlateCompareView { Width = 300, Height = 200 };
            view.SetPlates(left, null);
            view.SetMode(PlateCompareMode.Slide);
            ApplyOverlayState(view, left);

            var pixels = Render(view, 300, 200);

            Assert.True(InkedShare(pixels) > 0.005,
                        $"expected the left plate, got {InkedShare(pixels):P1}");
        });
    }
}
