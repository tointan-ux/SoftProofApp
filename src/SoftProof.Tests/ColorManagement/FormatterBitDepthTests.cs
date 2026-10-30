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
/// The bit depth is part of an lcms formatter. This application passes 8-bit buffers
/// (CMYK32 / BGRA32 / byte[]), so a 16-bit formatter makes cmsDoTransform walk past the end of
/// the pixel arrays — silent corruption, or a crash on small images.
/// </summary>
public class FormatterBitDepthTests
{
    private const string Folder = @"C:\Windows\System32\spool\drivers\color";
    private const string CmykProfile = "USWebCoatedSWOP.icc";
    private const string RgbProfile = "sRGB Color Space Profile.icm";

    private static bool ProfilesAvailable =>
        File.Exists(Path.Combine(Folder, CmykProfile)) && File.Exists(Path.Combine(Folder, RgbProfile));

    [Theory]
    [InlineData(ColorSpace.Gray, 1, 3, 0x030009)]
    [InlineData(ColorSpace.RGB, 3, 4, 0x040019)]
    [InlineData(ColorSpace.CMYK, 4, 6, 0x060021)]
    [InlineData(ColorSpace.Lab, 3, 10, 0x0A0019)]
    public void The_integer_formatter_is_eight_bits_per_sample(ColorSpace space, int channels, int ptCode, int expected)
    {
        var formatter = LittleCmsEngine.FormatterFor(space, isFloat: false);

        formatter.Should().Be(expected, $"TYPE_{space}_8 = COLORSPACE_SH({ptCode}) | CHANNELS_SH({channels}) | BYTES_SH(1)");
        (formatter & 0x7).Should().Be(1, "BYTES_SH lives in the low three bits and must say one byte per sample");
        ((formatter >> 3) & 0xF).Should().Be(channels);
        (formatter & LittleCmsNativeBits.FloatBit).Should().Be(0);
    }

    [Fact]
    public void A_float_formatter_asks_for_four_bytes_per_sample_and_the_float_bit()
    {
        var formatter = LittleCmsEngine.FormatterFor(ColorSpace.RGB, isFloat: true);

        (formatter & 0x7).Should().Be(4);
        (formatter & LittleCmsNativeBits.FloatBit).Should().Be(LittleCmsNativeBits.FloatBit);
    }

    [Fact]
    public void An_unsupported_colour_space_is_refused_instead_of_assuming_CMYK()
    {
        var act = () => LittleCmsEngine.FormatterFor(ColorSpace.MultiChannel, isFloat: false);

        act.Should().Throw<NotSupportedException>().WithMessage("*MultiChannel*");
    }

    [Fact]
    public async Task A_transform_over_an_eight_bit_buffer_produces_an_eight_bit_destination()
    {
        if (!ProfilesAvailable)
            return;

        using var engine = new LittleCmsEngine(NullLogger<LittleCmsEngine>.Instance);
        var cmyk = await engine.LoadProfileAsync(Path.Combine(Folder, CmykProfile));
        var rgb = await engine.LoadProfileAsync(Path.Combine(Folder, RgbProfile));

        var transform = await engine.CreateTransformAsync(cmyk, rgb, RenderingIntent.RelativeColorimetric, true);

        // 3 pixels, exactly 4 bytes each: a 16-bit formatter would want 24 bytes in and out.
        var source = new BitmapData
        {
            Width = 3,
            Height = 1,
            Format = PixelFormat.CMYK32,
            Stride = 12,
            Pixels = new byte[] { 0, 0, 0, 0, 255, 255, 255, 255, 128, 0, 0, 0 }
        };

        var result = await engine.ApplyTransformAsync(transform, source);

        result.Format.Should().Be(PixelFormat.RGB24);
        result.Stride.Should().Be(12, "3 RGB pixels = 9 bytes, padded to a 4-byte row");
        result.Pixels.Length.Should().Be(12);
    }

    /// <summary>The two bits of the formatter we assert against, kept in the test on purpose.</summary>
    private static class LittleCmsNativeBits
    {
        public const int FloatBit = 1 << 22;
    }
}
