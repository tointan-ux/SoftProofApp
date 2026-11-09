using SoftProof.Viewer;
using Xunit;

namespace SoftProof.Tests.Rendering;

/// <summary>
/// Level selection for the canvas. The bug this guards against: on a plate whose base is 1/64,
/// a 50 % view must request half-resolution tiles (1200 dpi), not stretch the 37 dpi base.
/// </summary>
public class ViewLevelsTests
{
    [Fact]
    public void AtFiftyPercent_AsksForHalfResolution_NotTheDecodedBase()
    {
        // 2400 dpi plate decoded at 1/64 (37.5 dpi base).
        const int nativeFactor = 64;

        var level = ViewLevels.DetailLevel(nativeZoom: 0.5, nativeFactor);

        Assert.Equal(1, level);                       // decimation 2 -> 1200 dpi
        Assert.True(ViewLevels.DetailLevel(0.5, nativeFactor) < ViewLevels.MaxDetailLevel(nativeFactor));
    }

    [Theory]
    [InlineData(1.0, 0)]    // native
    [InlineData(0.5, 1)]    // half
    [InlineData(0.25, 2)]   // quarter
    [InlineData(0.125, 3)]  // eighth
    [InlineData(2.0, 0)]    // above native stays native
    public void LevelFollowsTheZoomScale(double nativeZoom, int expected)
    {
        Assert.Equal(expected, ViewLevels.DetailLevel(nativeZoom, nativeFactor: 64));
    }

    [Fact]
    public void DetailIsRequestedUntilTheBaseIsSharpEnough()
    {
        const int nativeFactor = 64;   // base is 1/64

        // At 1/32 the base would be stretched 2x, so tiles are still worth fetching.
        Assert.True(ViewLevels.DetailLevel(1.0 / 32, nativeFactor) < ViewLevels.MaxDetailLevel(nativeFactor));

        // At 1/64 the base is exactly 1:1, so it is sharp enough.
        Assert.Equal(ViewLevels.MaxDetailLevel(nativeFactor), ViewLevels.DetailLevel(1.0 / 64, nativeFactor));
    }

    [Fact]
    public void PlateDecodedAtNativeNeverRequestsTiles()
    {
        Assert.Equal(0, ViewLevels.MaxDetailLevel(nativeFactor: 1));
        Assert.Equal(0, ViewLevels.DetailLevel(0.01, nativeFactor: 1));
    }

    [Fact]
    public void BaseFactorsMapToTheirPowerOfTwo()
    {
        Assert.Equal(3, ViewLevels.MaxDetailLevel(8));    // 300 dpi base on a 2400 dpi plate
        Assert.Equal(6, ViewLevels.MaxDetailLevel(64));   // 37.5 dpi base
        Assert.Equal(2, ViewLevels.MaxDetailLevel(4));
        Assert.Equal(0, ViewLevels.MaxDetailLevel(0));    // unknown factor: no tiles
    }
}
