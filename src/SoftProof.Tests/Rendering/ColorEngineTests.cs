using Microsoft.Extensions.Logging.Abstractions;
using SoftProof.ColorManagement;
using SoftProof.Core.Interfaces;
using SoftProof.Core.Models;
using Xunit;

namespace SoftProof.Tests.Rendering;

/// <summary>
/// LittleCMS interop contracts. The pixel-format word and the native signatures are asserted
/// directly because a wrong value produces a transform that succeeds and then reads or writes a
/// fraction of each pixel, with no error anywhere.
/// </summary>
public class ColorEngineTests
{
    private const string SrgbPath = @"C:\Windows\System32\spool\drivers\color\sRGB Color Space Profile.icm";
    private const string CmykPath = @"C:\Windows\System32\spool\drivers\color\CoatedFOGRA39.icc";

    private static bool HaveProfiles => File.Exists(SrgbPath) && File.Exists(CmykPath);

    // --- the format word ---------------------------------------------------------------------

    [Fact]
    public void GetFormatter_16bitCmyk_MatchesLcmsEncoding()
    {
        // COLORSPACE_SH(PT_CMYK=6) | CHANNELS_SH(4)<<3 | BYTES_SH(2)
        Assert.Equal(0x00060022, LittleCmsEngine.GetFormatter(ColorSpace.CMYK));
    }

    [Fact]
    public void GetFormatter_16bitRgb_MatchesLcmsEncoding()
    {
        Assert.Equal(0x0004001A, LittleCmsEngine.GetFormatter(ColorSpace.RGB));
    }

    [Fact]
    public void GetFormatter_16bitGrayAndLab_MatchLcmsEncoding()
    {
        Assert.Equal(0x0003000A, LittleCmsEngine.GetFormatter(ColorSpace.Gray));
        Assert.Equal(0x000A001A, LittleCmsEngine.GetFormatter(ColorSpace.Lab));
    }

    [Fact]
    public void GetFormatter_DecodesBackToFourCmykChannels()
    {
        // The old encoder produced 0x40A here, which lcms decoded as one 16-bit channel.
        var formatter = LittleCmsEngine.GetFormatter(ColorSpace.CMYK);

        Assert.Equal(2, formatter & 0x7);           // two bytes per sample
        Assert.Equal(4, (formatter >> 3) & 0xF);    // four channels
        Assert.Equal(6, (formatter >> 16) & 0xF);   // PT_CMYK
    }

    // --- transforms --------------------------------------------------------------------------

    [Fact]
    public async Task Transform_SameProfile_IsAnIdentity()
    {
        if (!File.Exists(SrgbPath))
            return; // Windows profile store absent: nothing to transform with.

        using var engine = new LittleCmsEngine(NullLogger<LittleCmsEngine>.Instance);
        var srgb = await engine.LoadProfileAsync(SrgbPath);
        var transform = await engine.CreateTransformAsync(
            srgb, srgb, RenderingIntent.RelativeColorimetric, blackPointCompensation: false);

        const int width = 4;
        var input = new byte[width * 6];
        for (var i = 0; i < width; i++)
        {
            input[i * 6 + 0] = 0x20; input[i * 6 + 1] = 0x40;
            input[i * 6 + 2] = 0x60; input[i * 6 + 3] = 0x80;
            input[i * 6 + 4] = 0xC0; input[i * 6 + 5] = 0xFF;
        }

        var source = new BitmapData
        {
            Width = width,
            Height = 1,
            Format = PixelFormat.RGB48,
            Stride = width * 6,
            Pixels = input
        };

        var result = await engine.ApplyTransformAsync(transform, source);

        Assert.Equal(PixelFormat.RGB48, result.Format);
        Assert.Equal(width * 6, result.Pixels.Length);
        // Every byte of every pixel is written; before the formatter fix four of six were left 0.
        Assert.Equal(input, result.Pixels);
    }

    [Fact]
    public async Task CreateProofTransform_WithRealProfiles_Succeeds()
    {
        if (!HaveProfiles)
            return;

        using var engine = new LittleCmsEngine(NullLogger<LittleCmsEngine>.Instance);
        var display = await engine.LoadProfileAsync(SrgbPath);
        var press = await engine.LoadProfileAsync(CmykPath);

        var proof = await engine.CreateProofTransformAsync(display, press, new ProofSettings
        {
            RenderingIntent = RenderingIntent.RelativeColorimetric,
            BlackPointCompensation = true
        });

        Assert.NotNull(proof.SimulationTransform);
        Assert.NotEqual(IntPtr.Zero, proof.SimulationTransform!.Handle);
    }

    [Fact]
    public async Task ApplyProofTransform_Cmyk64_ProducesFullRgb48Pixels()
    {
        if (!HaveProfiles)
            return;

        using var engine = new LittleCmsEngine(NullLogger<LittleCmsEngine>.Instance);
        var display = await engine.LoadProfileAsync(SrgbPath);
        var press = await engine.LoadProfileAsync(CmykPath);

        var proof = await engine.CreateProofTransformAsync(display, press, new ProofSettings());

        const int width = 3;
        var pixels = new byte[width * 8];
        // 100% cyan, 100% magenta, mid grey — 16-bit samples, little-endian.
        pixels[0] = 0xFF; pixels[1] = 0xFF;                 // C
        pixels[8 + 2] = 0xFF; pixels[8 + 3] = 0xFF;         // M
        for (var c = 0; c < 4; c++)
        {
            pixels[16 + c * 2] = 0x80;
        }

        var source = new BitmapData
        {
            Width = width,
            Height = 1,
            Format = PixelFormat.CMYK64,
            Stride = width * 8,
            Pixels = pixels
        };

        var result = await engine.ApplyProofTransformAsync(proof, source);

        Assert.Equal(PixelFormat.RGB48, result.Format);
        Assert.Equal(width * 6, result.Pixels.Length);
        Assert.Contains(result.Pixels, b => b != 0);   // not a blank buffer
    }

    [Fact]
    public async Task ApplyTransform_RejectsUndersizedBuffer()
    {
        if (!File.Exists(SrgbPath))
            return;

        using var engine = new LittleCmsEngine(NullLogger<LittleCmsEngine>.Instance);
        var srgb = await engine.LoadProfileAsync(SrgbPath);
        var transform = await engine.CreateTransformAsync(
            srgb, srgb, RenderingIntent.RelativeColorimetric, false);

        var tooSmall = new BitmapData
        {
            Width = 8,
            Height = 8,
            Format = PixelFormat.RGB48,
            Stride = 8 * 6,
            Pixels = new byte[16]
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => engine.ApplyTransformAsync(transform, tooSmall));
    }

    [Fact]
    public async Task Dispose_ReleasesHandlesAndIsIdempotent()
    {
        if (!File.Exists(SrgbPath))
            return;

        var engine = new LittleCmsEngine(NullLogger<LittleCmsEngine>.Instance);
        var srgb = await engine.LoadProfileAsync(SrgbPath);
        await engine.CreateTransformAsync(srgb, srgb, RenderingIntent.Perceptual, false);

        engine.Dispose();
        engine.Dispose();   // must not double-free or throw
    }
}
