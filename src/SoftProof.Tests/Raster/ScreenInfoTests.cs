using SoftProof.RIPParsers;
using Xunit;

namespace SoftProof.Tests.Raster;

/// <summary>
/// Screening data recovered from RIP free text. When it is absent the screen-angle check must
/// report "not run" rather than a made-up angle.
/// </summary>
public class ScreenInfoTests
{
    [Theory]
    [InlineData("Ace Kid mod tuck 175lpi 15deg", 175f, 15f)]
    [InlineData("AM 175 LPI, 15°", 175f, 15f)]
    [InlineData("Screen: 150 lines per inch at 45 degrees", 150f, 45f)]
    [InlineData("133,3 lpi 75 Deg", 133.3f, 75f)]
    public void TryParse_ReadsLpiAndAngle(string text, float expectedLpi, float expectedAngle)
    {
        Assert.True(ScreenInfo.TryParse(text, out var lpi, out var angle));

        Assert.Equal(expectedLpi, lpi, precision: 1);
        Assert.Equal(expectedAngle, angle, precision: 1);
    }

    [Fact]
    public void TryParse_WithOnlyFrequency_LeavesAngleUnset()
    {
        Assert.True(ScreenInfo.TryParse("175 lpi", out var lpi, out var angle));

        Assert.Equal(175f, lpi, precision: 1);
        Assert.Equal(0f, angle);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ace Kid mod tuck in DE")]      // the real-world plate name from the review
    [InlineData("Cyan plate, no screening text")]
    public void TryParse_WithoutScreeningText_ReturnsFalse(string? text)
    {
        Assert.False(ScreenInfo.TryParse(text, out var lpi, out var angle));
        Assert.Equal(0f, lpi);
        Assert.Equal(0f, angle);
    }

    [Theory]
    [InlineData("9999 lpi 15 deg")]             // implausible frequency
    [InlineData("175 lpi 720 deg")]             // angle out of range
    public void TryParse_RejectsImplausibleValues(string text)
    {
        ScreenInfo.TryParse(text, out var lpi, out var angle);

        Assert.True(lpi == 0 || angle == 0);
    }
}
