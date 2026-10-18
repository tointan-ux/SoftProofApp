using SoftProof.Core.Analysis;
using Xunit;

namespace SoftProof.Tests.Analysis;

/// <summary>
/// RIP-vs-reference comparison: lost dots, spurious dots, and the registration tolerance that
/// keeps a rasterised PDF from reporting every edge as an error.
/// </summary>
public class RipReferenceComparerTests
{
    private static byte[] Mask(int width, int height, params (int X, int Y, int W, int H)[] blocks)
    {
        var mask = new byte[width * height];
        foreach (var (bx, by, bw, bh) in blocks)
            for (var y = by; y < by + bh; y++)
                for (var x = bx; x < bx + bw; x++)
                    mask[y * width + x] = 1;

        return mask;
    }

    private static byte[] Rip(int width, int height, params (int X, int Y, int W, int H)[] blocks)
    {
        var coverage = new byte[width * height];
        foreach (var (bx, by, bw, bh) in blocks)
            for (var y = by; y < by + bh; y++)
                for (var x = bx; x < bx + bw; x++)
                    coverage[y * width + x] = 255;

        return coverage;
    }

    [Fact]
    public void IdenticalMasks_AreClean()
    {
        const int w = 50, h = 40;
        var reference = Mask(w, h, (10, 10, 20, 10));
        var rip = Rip(w, h, (10, 10, 20, 10));

        var result = RipReferenceComparer.Compare(rip, reference, w, h, tolerance: 1);

        Assert.True(result.IsClean);
        Assert.Equal(0, result.Missing);
        Assert.Equal(0, result.Extra);
        Assert.All(result.Map, code => Assert.Equal(0, code));
    }

    [Fact]
    public void MissingBlock_IsCountedAsLostInk()
    {
        const int w = 50, h = 40;
        var reference = Mask(w, h, (10, 10, 10, 10));
        var rip = new byte[w * h];

        var result = RipReferenceComparer.Compare(rip, reference, w, h, tolerance: 1);

        Assert.Equal(100, result.Missing);
        Assert.Equal(0, result.Extra);
        Assert.Equal(100, result.TotalErrors);
    }

    [Fact]
    public void ExtraBlock_IsCountedAsAddedInk()
    {
        const int w = 50, h = 40;
        var reference = new byte[w * h];
        var rip = Rip(w, h, (10, 10, 10, 10));

        var result = RipReferenceComparer.Compare(rip, reference, w, h, tolerance: 1);

        Assert.Equal(0, result.Missing);
        Assert.Equal(100, result.Extra);
        Assert.Equal(100.0, result.ErrorPercentOfInk); // blank reference: all ink is an error
    }

    [Fact]
    public void OnePixelShift_IsToleratedAtRadius1_AndReportedAtRadius0()
    {
        const int w = 60, h = 40;
        var reference = Mask(w, h, (20, 10, 10, 10));
        var shifted = Rip(w, h, (21, 10, 10, 10));

        var tolerant = RipReferenceComparer.Compare(shifted, reference, w, h, tolerance: 1);
        Assert.True(tolerant.IsClean);

        var strict = RipReferenceComparer.Compare(shifted, reference, w, h, tolerance: 0);
        Assert.False(strict.IsClean);
        Assert.True(strict.TotalErrors > 0);
    }

    [Fact]
    public void FaintCoverageBelowThreshold_IsTreatedAsPaper()
    {
        const int w = 30, h = 30;
        var rip = new byte[w * h];
        Array.Fill(rip, (byte)10); // 4% tone, below the 32 threshold
        var reference = new byte[w * h];

        var result = RipReferenceComparer.Compare(rip, reference, w, h, tolerance: 0);

        Assert.True(result.IsClean);
        Assert.Equal(0, result.RipInkPixels);
    }

    [Fact]
    public void ErrorsArePercentOfReferenceInk()
    {
        const int w = 40, h = 40;
        var reference = Mask(w, h, (0, 0, 10, 10)); // 100 ink px
        var rip = Rip(w, h, (0, 0, 10, 10), (20, 20, 5, 5)); // +25 extra

        var result = RipReferenceComparer.Compare(rip, reference, w, h, tolerance: 0);

        Assert.Equal(25, result.Extra);
        Assert.Equal(25.0, result.ErrorPercentOfInk, 3);
    }
}
