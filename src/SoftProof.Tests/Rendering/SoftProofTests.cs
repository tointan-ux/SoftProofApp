using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentAssertions;
using SoftProof.Core.Interfaces;
using SoftProof.Core.Models;
using SoftProof.Core.Utilities;
using SoftProof.Viewer;
using Xunit;
using CorePixelFormat = SoftProof.Core.Interfaces.PixelFormat;

namespace SoftProof.Tests.Rendering;

/// <summary>
/// Soft proofing used to be unreachable: the only implementation lived in a control nothing
/// displayed, and it fed a CMYK→CMYK transform into a BGRA bitmap. These tests pin the two things
/// that make it real — the plate data is packed into CMYK correctly, and what the viewer draws
/// after a proof is the transform's output, not the screen composite.
/// </summary>
public class SoftProofTests
{
    private const int Width = 64;
    private const int Height = 32;

    [Fact]
    public void Process_plates_map_to_their_cmyk_channels()
    {
        var coverage = new[] { Fill(200), Fill(150), Fill(100), Fill(50) };
        var colours = new[]
        {
            SeparationColor.ProcessCyan, SeparationColor.ProcessMagenta,
            SeparationColor.ProcessYellow, SeparationColor.ProcessBlack
        };

        var cmyk = ProofComposer.BuildCmyk(coverage, colours, new bool[4],
                                           new[] { 1.0, 1.0, 1.0, 1.0 }, Width, Height);

        cmyk.Format.Should().Be(CorePixelFormat.CMYK32);
        cmyk.Stride.Should().Be(Width * 4);
        cmyk.Pixels.Length.Should().Be(Width * Height * 4);

        // Pixel 0 carries all four inks in C, M, Y, K order.
        cmyk.Pixels[0].Should().Be(200);
        cmyk.Pixels[1].Should().Be(150);
        cmyk.Pixels[2].Should().Be(100);
        cmyk.Pixels[3].Should().Be(50);
    }

    [Fact]
    public void Inversion_and_opacity_reach_the_proof()
    {
        var coverage = new[] { Fill(200) };

        var inverted = ProofComposer.BuildCmyk(coverage, new[] { SeparationColor.ProcessCyan },
                                               new[] { true }, new[] { 1.0 }, Width, Height);
        inverted.Pixels[0].Should().Be(55, "an inverted plate proves what it prints, not what it stores");

        var half = ProofComposer.BuildCmyk(coverage, new[] { SeparationColor.ProcessCyan },
                                           new[] { false }, new[] { 0.5 }, Width, Height);
        half.Pixels[0].Should().Be(100);
    }

    [Fact]
    public void Spot_inks_are_not_forced_into_a_cmyk_channel()
    {
        var coverage = new[] { Fill(255), Fill(128) };

        var cmyk = ProofComposer.BuildCmyk(
            coverage,
            new[] { SeparationColor.SpotColor, SeparationColor.ProcessBlack },
            new bool[2], new[] { 1.0, 1.0 }, Width, Height);

        // Only the black plate contributes; the spot would need its own profile.
        cmyk.Pixels[0].Should().Be(0);
        cmyk.Pixels[3].Should().Be(128);
    }

    [Fact]
    public void A_spot_only_job_has_no_channels_to_proof()
    {
        ProofComposer.ProcessChannelCount(new[] { SeparationColor.SpotColor, SeparationColor.Varnish })
            .Should().Be(0, "proofing a spot-only job would render bare paper");

        ProofComposer.ProcessChannelCount(
                new[] { SeparationColor.ProcessCyan, SeparationColor.ProcessBlack, SeparationColor.SpotColor })
            .Should().Be(2);
    }

    [Fact]
    public void Rgb_output_is_converted_to_bgra_in_the_right_order()
    {
        var rgb = new BitmapData
        {
            Width = 2,
            Height = 1,
            Stride = 8,                       // 2 px * 3 bytes, padded to 4-byte rows
            Format = CorePixelFormat.RGB24,
            Pixels = new byte[] { 10, 20, 30, 40, 50, 60, 0, 0 }
        };

        var bgra = RunOnStaThread(() => ProofComposer.ToBgra(rgb));

        var pixels = new byte[8];
        bgra.CopyPixels(new Int32Rect(0, 0, 2, 1), pixels, 8, 0);

        pixels[0].Should().Be(30);   // B
        pixels[1].Should().Be(20);   // G
        pixels[2].Should().Be(10);   // R
        pixels[3].Should().Be(255);

        pixels[4].Should().Be(60);
        pixels[5].Should().Be(50);
        pixels[6].Should().Be(40);
    }

    [Fact]
    public void The_viewer_draws_the_proof_instead_of_the_screen_composite()
    {
        var result = RunOnStaThread(() =>
        {
            var view = BuildViewer();
            view.UpdateLayout();

            // A proof that returns solid red proves the transform output is what gets drawn.
            view.EnableProofAsync((cmyk, _) => Task.FromResult(SolidRgb(cmyk.Width, cmyk.Height, 255, 0, 0)))
                .GetAwaiter().GetResult()
                .Should().BeTrue();

            var proofed = Sample(view);
            view.DisableProof();
            var composite = Sample(view);

            return (Proofed: proofed, Composite: composite, WasEnabled: view.ProofEnabled);
        });

        // Red: R high, G/B low. The cyan plate composite is the opposite.
        result.Proofed.R.Should().BeGreaterThan(200);
        result.Proofed.G.Should().BeLessThan(60);
        result.Proofed.B.Should().BeLessThan(60);

        result.Composite.R.Should().BeLessThan(60, "without the proof the plate composites as cyan");

        result.WasEnabled.Should().BeFalse("DisableProof restores the composite");
    }

    [Fact]
    public void A_proof_transform_is_required_to_have_a_display_profile()
    {
        var act = () => ProofProfileLocator.Resolve(
            new ProofSettings { DisplayProfilePath = @"C:\does\not\exist.icc" });

        act.Should().Throw<FileNotFoundException>().WithMessage("*does not exist*");
    }

    [Fact]
    public void Missing_default_profiles_are_reported_not_replaced()
    {
        var empty = Path.Combine(Path.GetTempPath(), "softproof-no-profiles-" + Guid.NewGuid().ToString("N"));

        var act = () => ProofProfileLocator.Resolve(new ProofSettings(), empty);

        act.Should().Throw<FileNotFoundException>().WithMessage("*sRGB Color Space Profile.icm*");
    }

    [Fact]
    public void The_windows_profiles_resolve_when_present()
    {
        if (!File.Exists(Path.Combine(ProofProfileLocator.SystemProfileDirectory, ProofProfileLocator.DefaultSimulationProfile)))
            return;

        var (display, simulation) = ProofProfileLocator.Resolve(new ProofSettings());

        File.Exists(display).Should().BeTrue();
        File.Exists(simulation).Should().BeTrue();
    }

    private static PlateViewer BuildViewer()
    {
        const int factor = 4;
        var coverage = Fill(255, Width * factor, Height * factor);
        var separation = new Separation { Name = "Cyan", Color = SeparationColor.ProcessCyan };
        var layer = new PlateLayer(separation, Width * factor, Height * factor, coverage,
                                  0, 255, 255,
                                  PlatePyramid.FromCoverage(coverage, Width * factor, Height * factor, factor));

        var view = new PlateViewer { Width = 160, Height = 160 };
        view.SetPlates(new List<PlateLayer> { layer }, Width * factor, Height * factor);
        view.Measure(new Size(160, 160));
        view.Arrange(new Rect(0, 0, 160, 160));
        return view;
    }

    private static (int R, int G, int B) Sample(PlateViewer view)
    {
        view.Measure(new Size(160, 160));
        view.Arrange(new Rect(0, 0, 160, 160));
        view.UpdateLayout();

        var bitmap = new RenderTargetBitmap(160, 160, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);

        var pixels = new byte[160 * 160 * 4];
        bitmap.CopyPixels(pixels, 160 * 4, 0);

        var centre = (80 * 160 + 80) * 4;
        return (pixels[centre + 2], pixels[centre + 1], pixels[centre]);
    }

    private static BitmapData SolidRgb(int width, int height, byte r, byte g, byte b)
    {
        var stride = (width * 3 + 3) / 4 * 4;
        var pixels = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = y * stride + x * 3;
                pixels[o] = r;
                pixels[o + 1] = g;
                pixels[o + 2] = b;
            }
        }

        return new BitmapData
        {
            Width = width, Height = height, Stride = stride,
            Format = CorePixelFormat.RGB24, Pixels = pixels
        };
    }

    private static byte[] Fill(byte value) => Fill(value, Width, Height);

    private static byte[] Fill(byte value, int width, int height)
    {
        var data = new byte[width * height];
        Array.Fill(data, value);
        return data;
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
