using SoftProof.RIPParsers.Raster;
using SoftProof.RIPParsers.Raster.Tiff;
using Xunit;

namespace SoftProof.Tests.Raster;

/// <summary>
/// Fixtures in ./Fixtures were written by libtiff (PIL) with known pixel formulas, so these
/// tests validate the decoder against an independent implementation, not against itself.
/// </summary>
public class TiffReaderTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static int ExpectedGray(int x, int y) => (x * 7 + y * 11) % 256;

    private static bool ExpectedBilevel(int x, int y) => (x / 3 + y / 2) % 2 == 1;

    [Theory]
    [InlineData("gray8_none.tif")]
    [InlineData("gray8_packbits.tif")]
    [InlineData("gray8_lzw.tif")]
    public void Read_GrayFixture_MatchesLibtiffPixels(string file)
    {
        var image = TiffReader.Read(Fixture(file));

        Assert.Equal(32, image.Width);
        Assert.Equal(8, image.Height);
        Assert.Equal(1, image.SamplesPerPixel);
        Assert.False(image.IsWhiteIsZero);

        var plane = image.Planes[0];
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
                Assert.Equal(ExpectedGray(x, y), plane[y * image.Width + x]);
    }

    [Fact]
    public void InkCoverage_InvertsBlackIsZeroSamples()
    {
        var image = TiffReader.Read(Fixture("gray8_lzw.tif"));
        var coverage = image.InkCoverage();

        Assert.Equal(255 - ExpectedGray(0, 0), coverage[0]);
        Assert.Equal(255 - ExpectedGray(31, 7), coverage[7 * 32 + 31]);
    }

    [Fact]
    public void Read_BilevelBlackIsZero_UnpacksMsbFirstBits()
    {
        var image = TiffReader.Read(Fixture("bilevel_blackiszero.tif"));

        Assert.True(image.IsBilevel);
        Assert.Equal(1, image.BitsPerSample[0]);

        // Photometric 1 (BlackIsZero): sample 0 is black, so ink is where the bit is CLEAR.
        var coverage = image.InkCoverage();
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var expected = ExpectedBilevel(x, y) ? 0 : 255;
                Assert.Equal(expected, coverage[y * image.Width + x]);
            }
    }

    [Fact]
    public void InkCoverage_WhiteIsZeroTreatsSetBitsAsInk()
    {
        var image = TiffReader.Read(Fixture("bilevel_miniswhite.tif"));
        Assert.True(image.IsWhiteIsZero);

        // Photometric 0 (WhiteIsZero): sample 0 is paper, sample max is ink.
        var coverage = image.InkCoverage();
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var expected = ExpectedBilevel(x, y) ? 255 : 0;
                Assert.Equal(expected, coverage[y * image.Width + x]);
            }
    }

    [Fact]
    public void InkCoverage_SameBitsUnderOppositePhotometric_AreComplements()
    {
        // Both fixtures store the same bits; only the photometric tag differs, so the
        // decoded plates must be exact complements of each other.
        var blackIsZero = TiffReader.Read(Fixture("bilevel_blackiszero.tif")).InkCoverage();
        var whiteIsZero = TiffReader.Read(Fixture("bilevel_miniswhite.tif")).InkCoverage();

        for (var i = 0; i < blackIsZero.Length; i++)
            Assert.Equal((byte)(255 - blackIsZero[i]), whiteIsZero[i]);
    }

    [Fact]
    public void Read_BilevelWithLzwCompression_MatchesPattern()
    {
        var image = TiffReader.Read(Fixture("bilevel_lzw.tif"));
        var coverage = image.InkCoverage();

        // libtiff wrote this fixture with photometric 1 (BlackIsZero).
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var expected = ExpectedBilevel(x, y) ? 0 : 255;
                Assert.Equal(expected, coverage[y * image.Width + x]);
            }
    }

    [Fact]
    public void Read_SeparatedCmykLzw_KeepsEachChannelAsInk()
    {
        var image = TiffReader.Read(Fixture("cmyk_separated_lzw.tif"));

        Assert.Equal(16, image.Width);
        Assert.Equal(8, image.Height);
        Assert.Equal(4, image.SamplesPerPixel);
        Assert.True(image.IsSeparated);

        for (var s = 0; s < 4; s++)
        {
            var coverage = image.InkCoverage(s);
            for (var y = 0; y < image.Height; y++)
                for (var x = 0; x < image.Width; x++)
                {
                    var expected = s switch
                    {
                        0 => (x * 9) % 256,
                        1 => (y * 17) % 256,
                        2 => (x * y) % 256,
                        _ => ((x + y) * 5) % 256
                    };
                    Assert.Equal((byte)expected, coverage[y * image.Width + x]);
                }
        }
    }

    [Fact]
    public void Read_NonTiff_ThrowsWithDiagnostic()
    {
        var path = Path.Combine(Path.GetTempPath(), $"softproof-not-a-tiff-{Guid.NewGuid():N}.tif");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });

        try
        {
            var ex = Assert.Throws<PbfFormatException>(() => TiffReader.Read(path));
            Assert.Contains("TIFF", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
