using System;
using System.Windows;
using System.Windows.Media;
using FluentAssertions;
using SoftProof.Viewer;
using Xunit;

namespace SoftProof.Tests.Rendering;

/// <summary>
/// Golden-invariant tests for the device-resolution viewport renderer.
/// These assert the properties the spec cares about (byte-exact at 1:1, exact N×N blocks
/// at integer magnification, honest coverage when downscaling) rather than pixel blobs.
/// </summary>
public class ViewportResamplerTests
{
    private static byte[] MakeSource(int w, int h, Func<int, int, (byte B, byte G, byte R)> shader)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var (b, g, r) = shader(x, y);
            var o = (y * w + x) * 4;
            px[o + 0] = b;
            px[o + 1] = g;
            px[o + 2] = r;
            px[o + 3] = 255;
        }
        return px;
    }

    private static byte[] Render(byte[] src, int sw, int sh, Matrix view, int dw, int dh, double dpi = 1.0)
    {
        var dest = new byte[dw * dh * 4];
        ViewportResampler.DrawRaw(src, sw, sh, new Rect(0, 0, sw, sh), view, dpi, dpi, dw, dh, dest);
        return dest;
    }

    private static (byte B, byte G, byte R) At(byte[] px, int w, int x, int y)
    {
        var o = (y * w + x) * 4;
        return (px[o], px[o + 1], px[o + 2]);
    }

    [Fact]
    public void AtOneToOne_IsByteExactCopy()
    {
        var src = MakeSource(8, 8, (x, y) => ((byte)(x * 30), (byte)(y * 30), (byte)((x ^ y) * 20)));
        var dest = Render(src, 8, 8, Matrix.Identity, 8, 8);

        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
            At(dest, 8, x, y).Should().Be(At(src, 8, x, y), $"pixel {x},{y} must be the plate byte value");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(16)]
    public void AtIntegerMagnification_EachPlatePixelBecomesUniformNByNBlock(int n)
    {
        var src = MakeSource(4, 4, (x, y) => (x % 2 == 0 && y % 2 == 0) ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255));
        var dest = Render(src, 4, 4, new Matrix(n, 0, 0, n, 0, 0), 4 * n, 4 * n);

        for (var y = 0; y < 4 * n; y++)
        for (var x = 0; x < 4 * n; x++)
            At(dest, 4 * n, x, y).Should().Be(At(src, 4, x / n, y / n), "block pixels must be identical, no blur");
    }

    [Fact]
    public void DownscaledHalf_ReportsHonestCoverage()
    {
        // Left two columns black, right two white; at 50 % each output pixel averages 2x2.
        var src = MakeSource(4, 4, (x, _) => x < 2 ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255));
        var dest = Render(src, 4, 4, new Matrix(0.5, 0, 0, 0.5, 0, 0), 2, 2);

        At(dest, 2, 0, 0).R.Should().Be(0, "both columns are ink");
        At(dest, 2, 1, 0).R.Should().Be(255, "both columns are paper");
    }

    [Fact]
    public void ThinLine_KeepsUniformWeightAcrossTheFrame()
    {
        // Single-pixel vertical line at x = 5 on a 32x32 plate, downscaled to 60 %.
        var src = MakeSource(32, 32, (x, _) => x == 5 ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255));
        var dest = Render(src, 32, 32, new Matrix(0.6, 0, 0, 0.6, 0, 0), 19, 19);

        var darkestPerRow = new int[19];
        for (var y = 0; y < 19; y++)
        {
            var min = 255;
            for (var x = 0; x < 19; x++)
                min = Math.Min(min, At(dest, 19, x, y).R);
            darkestPerRow[y] = min;
        }

        darkestPerRow.Should().OnlyContain(v => v == darkestPerRow[0],
            "a 1-px line must have the same weight on every row (no shimmer)");
    }
}
