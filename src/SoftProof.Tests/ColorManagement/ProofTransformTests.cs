using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SoftProof.ColorManagement;
using SoftProof.Core.Interfaces;
using SoftProof.Core.Models;
using Xunit;

namespace SoftProof.Tests.ColorManagement;

/// <summary>
/// Exercises the real LittleCMS proofing transform. The old code passed the simulation profile as
/// the transform's *output* and the display profile as its *proofing* profile, so a "soft proof"
/// came back in press space, and it built a second transform from a null handle that it never used.
/// </summary>
public class ProofTransformTests
{
    private const string ColorFolder = @"C:\Windows\System32\spool\drivers\color";
    private const string SrgbProfile = "sRGB Color Space Profile.icm";
    private const string CoatedProfile = "USWebCoatedSWOP.icc";
    private const string UncoatedProfile = "USWebUncoated.icc";

    private static bool ProfilesAvailable =>
        File.Exists(Path.Combine(ColorFolder, SrgbProfile)) &&
        File.Exists(Path.Combine(ColorFolder, CoatedProfile));

    [Fact]
    public async Task Proofing_transform_runs_document_CMYK_to_display_RGB_in_one_pass()
    {
        if (!ProfilesAvailable)
            return;                      // colour profiles live with the OS; nothing to prove without them

        using var engine = new LittleCmsEngine(NullLogger<LittleCmsEngine>.Instance);

        var display = await engine.LoadProfileAsync(Path.Combine(ColorFolder, SrgbProfile));
        var simulation = await engine.LoadProfileAsync(Path.Combine(ColorFolder, CoatedProfile));

        var settings = new ProofSettings
        {
            RenderingIntent = RenderingIntent.RelativeColorimetric,
            BlackPointCompensation = true,
            SimulatePaperColor = true,
            InkLimit = 300
        };

        using var proof = await engine.CreateProofTransformAsync(display, simulation, settings);

        proof.ProofingTransform.Should().NotBeNull();
        proof.ProofingTransform!.Handle.Should().NotBe(IntPtr.Zero);
        proof.ProofingTransform.DestProfile.ColorSpace.Should().Be(ColorSpace.RGB,
            "a soft proof ends on the monitor, so the output side is display space");
        proof.SimulationTransform!.Handle.Should().NotBe(IntPtr.Zero,
            "the document -> simulation leg must be a real transform, not a null-handle placeholder");
        proof.DisplayTransform!.Handle.Should().NotBe(IntPtr.Zero);

        // 2x2 CMYK where 0 = no ink: paper, solid CMYK, 50 % magenta, 50 % cyan.
        var source = new BitmapData
        {
            Width = 2,
            Height = 2,
            Format = PixelFormat.CMYK32,
            Stride = 8,
            Pixels = new byte[]
            {
                0, 0, 0, 0,
                255, 255, 255, 255,
                0, 128, 0, 0,
                128, 0, 0, 0
            }
        };

        var result = await engine.ApplyProofTransformAsync(proof, source);

        result.Format.Should().Be(PixelFormat.RGB24);
        result.Stride.Should().Be(8, "2 RGB pixels pad to 4-byte rows");
        result.Pixels.Length.Should().Be(result.Stride * 2);

        var white = Luma(result.Pixels, 0);
        var black = Luma(result.Pixels, 3);

        black.Should().BeLessThan(60, "a 100 % CMYK patch proofs near black");
        white.Should().BeGreaterThan(200, "paper white stays light after proofing");
        black.Should().BeLessThan(white);
    }

    [Fact]
    public async Task A_separate_document_profile_is_supported()
    {
        if (!ProfilesAvailable || !File.Exists(Path.Combine(ColorFolder, UncoatedProfile)))
            return;

        using var engine = new LittleCmsEngine(NullLogger<LittleCmsEngine>.Instance);

        var display = await engine.LoadProfileAsync(Path.Combine(ColorFolder, SrgbProfile));
        var simulation = await engine.LoadProfileAsync(Path.Combine(ColorFolder, UncoatedProfile));
        var document = await engine.LoadProfileAsync(Path.Combine(ColorFolder, CoatedProfile));

        using var proof = await engine.CreateProofTransformAsync(
            display, simulation, new ProofSettings(), document);

        proof.ProofingTransform!.SourceProfile.Should().BeSameAs(document);
        proof.ProofingTransform.DestProfile.Should().BeSameAs(display);
    }

    [Fact]
    public async Task A_CMYK_display_profile_is_rejected_with_an_explanation()
    {
        if (!ProfilesAvailable)
            return;

        using var engine = new LittleCmsEngine(NullLogger<LittleCmsEngine>.Instance);

        var cmyk = await engine.LoadProfileAsync(Path.Combine(ColorFolder, CoatedProfile));

        var act = async () => await engine.CreateProofTransformAsync(cmyk, cmyk, new ProofSettings());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*display profile*must be an RGB*");
    }

    [Fact]
    public async Task An_unloaded_profile_is_rejected_instead_of_building_a_null_transform()
    {
        using var engine = new LittleCmsEngine(NullLogger<LittleCmsEngine>.Instance);

        var placeholder = new IccProfile { Name = "unloaded", ColorSpace = ColorSpace.CMYK };
        var display = new IccProfile { Name = "display", ColorSpace = ColorSpace.RGB };

        var act = async () => await engine.CreateProofTransformAsync(display, placeholder, new ProofSettings());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*simulationProfile is not loaded*");
    }

    private static double Luma(byte[] pixels, int offset) =>
        (0.2126 * pixels[offset]) + (0.7152 * pixels[offset + 1]) + (0.0722 * pixels[offset + 2]);
}
