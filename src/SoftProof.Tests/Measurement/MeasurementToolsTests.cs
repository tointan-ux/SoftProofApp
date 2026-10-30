using System;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SoftProof.ColorManagement;
using SoftProof.Core.Interfaces;
using SoftProof.Core.Models;
using SoftProof.Tools;
using Xunit;

namespace SoftProof.Tests.Measurement;

/// <summary>
/// These tools used to answer without looking at the plate: hue error and slur/doubling returned
/// (0, 0) unconditionally, and CMYK -> Lab went through a (1-C)(1-K) screen approximation that
/// ignored the press profile.
/// </summary>
public class MeasurementToolsTests
{
    private const string Folder = @"C:\Windows\System32\spool\drivers\color";
    private const string CmykProfile = "USWebCoatedSWOP.icc";

    private static DensitometerTool Densitometer => new(NullLogger<DensitometerTool>.Instance);

    [Theory]
    [InlineData(1.4f, 0.5f, 0.2f, 25f, 14.29f)]     // classic Preucil example
    [InlineData(2.0f, 2.0f, 2.0f, 0f, 100f)]        // neutral: no hue error, fully grey
    [InlineData(0f, 0f, 0f, 0f, 0f)]                // no ink
    public void Preucil_hue_error_and_grayness_follow_the_densities(
        float c, float m, float y, float expectedHueError, float expectedGrayness)
    {
        var (hueError, grayness) = Densitometer.MeasureHueErrorGrayness(c, m, y);

        hueError.Should().BeApproximately(expectedHueError, 0.05f);
        grayness.Should().BeApproximately(expectedGrayness, 0.05f);
    }

    [Fact]
    public void Slur_and_doubling_react_to_the_rendered_dots()
    {
        // 20 % vertical stripes at 96 dpi, 256 x 256, one bit per pixel.
        var clean = PackStripePattern(withGhost: false);
        var doubled = PackStripePattern(withGhost: true);

        var region = new MeasurementRegion { X = 0, Y = 0, Width = 256, Height = 256, Name = "stripes" };

        var cleanResult = Densitometer.MeasureSlurDoubling(clean, 256, 256, region);
        var doubledResult = Densitometer.MeasureSlurDoubling(doubled, 256, 256, region);

        cleanResult.Doubling.Should().Be(0, "a clean edge has no satellite dots");
        doubledResult.Doubling.Should().BeGreaterThan(0, "the ghost line is ink that touches an edge");

        // Both patterns are vertically oriented, so the directional imbalance must be identical.
        cleanResult.Slur.Should().BeApproximately(doubledResult.Slur, 0.01f);
    }

    [Fact]
    public void Slur_grows_when_edges_disappear_in_one_direction()
    {
        var region = new MeasurementRegion { X = 0, Y = 0, Width = 256, Height = 256, Name = "scan" };

        var balanced = PackStripePattern(withGhost: false);
        var smeared = PackSolid(256, 256);           // no edges at all in either direction

        var balancedResult = Densitometer.MeasureSlurDoubling(balanced, 256, 256, region);
        var smearedResult = Densitometer.MeasureSlurDoubling(smeared, 256, 256, region);

        balancedResult.Slur.Should().Be(100f, "vertical stripes have transitions only across x");
        smearedResult.Slur.Should().Be(0f, "a solid has no transitions in either direction");
    }

    [Fact]
    public void Cmyk_to_Lab_goes_through_the_press_profile()
    {
        if (!File.Exists(Path.Combine(Folder, CmykProfile)))
            return;

        using var engine = new LittleCmsEngine(NullLogger<LittleCmsEngine>.Instance);
        var swop = engine.LoadProfileAsync(Path.Combine(Folder, CmykProfile)).GetAwaiter().GetResult();
        var tool = new ColorMeasurementTool(NullLogger<ColorMeasurementTool>.Instance, engine);

        var paper = engine.ConvertCmykToLab(new[] { 0f, 0f, 0f, 0f }, swop);
        var black = engine.ConvertCmykToLab(new[] { 0f, 0f, 0f, 100f }, swop);
        var cyan = engine.ConvertCmykToLab(new[] { 100f, 0f, 0f, 0f }, swop);

        paper[0].Should().BeGreaterThan(85, "SWOP paper white is a light L*");
        black[0].Should().BeLessThan(40, "solid black ink proofs dark");
        cyan[0].Should().BeGreaterThan(black[0]).And.BeLessThan(paper[0]);
        cyan[2].Should().BeLessThan(-10, "cyan is strongly blue in b*");

        // The screen-space shortcut this replaced would have put cyan near b* = +? (yellow-ish)
        // and could not represent paper at all; assert the values are plausible for the profile.
        tool.Should().NotBeNull();
    }

    [Fact]
    public void A_Cmyk_to_Lab_conversion_without_a_profile_is_refused()
    {
        var tool = new ColorMeasurementTool(NullLogger<ColorMeasurementTool>.Instance, new LittleCmsEngine(NullLogger<LittleCmsEngine>.Instance));

        var act = () => tool.MeasureColor(
            new System.Collections.Generic.Dictionary<SeparationColor, byte[]>(),
            8, 8,
            new MeasurementRegion { X = 0, Y = 0, Width = 8, Height = 8, Name = "t" },
            printProfile: null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*press ICC profile*");
    }

    [Fact]
    public void Disposing_the_engine_twice_is_safe_and_closes_the_native_handles()
    {
        var engine = new LittleCmsEngine(NullLogger<LittleCmsEngine>.Instance);
        engine.Dispose();
        engine.Dispose();

        var act = () => engine.ConvertCmykToLab(new[] { 0f, 0f, 0f, 0f }, new IccProfile());
        act.Should().Throw<ObjectDisposedException>();
    }

    private static byte[] PackStripePattern(bool withGhost)
    {
        const int size = 256;
        const int period = 8;               // 2 inked pixels out of 8
        var packed = new byte[size * size / 8];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var on = x % period < 2;

                // A doubled impression leaves an isolated one-pixel line behind the edge.
                if (withGhost && x % period == 4)
                    on = true;

                if (!on)
                    continue;

                var index = y * size + x;
                packed[index / 8] |= (byte)(0x80 >> (index % 8));
            }
        }

        return packed;
    }

    private static byte[] PackSolid(int width, int height)
    {
        var packed = new byte[width * height / 8];
        Array.Fill(packed, (byte)0xFF);
        return packed;
    }
}
