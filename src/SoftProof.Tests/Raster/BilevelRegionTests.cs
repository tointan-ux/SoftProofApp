using SoftProof.RIPParsers.Raster.Tiff;
using Xunit;

namespace SoftProof.Tests.Raster;

/// <summary>
/// Native-resolution region decode: the "original pixels" layer must equal the corresponding
/// rectangle of a full decode, at native resolution, without materialising the whole page.
/// </summary>
public class BilevelRegionTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Region_MatchesFullDecodeRectangle()
    {
        var path = Fixture("bilevel_lzw.tif");
        var full = TiffReader.Read(path);
        var coverage = full.InkCoverage();

        // The fixture is 32x8 (1-bit LZW, BlackIsZero), so stay inside the page.
        const int x0 = 4, y0 = 2, w = 16, h = 4;
        var region = TiffReader.ReadBilevelRegion(path, 0, x0, y0, w, h);

        Assert.Equal(w * h, region.Length);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                Assert.Equal(coverage[(y + y0) * full.Width + (x + x0)], region[y * w + x]);
    }

    [Fact]
    public void Region_ClampsToPageBounds()
    {
        var path = Fixture("bilevel_lzw.tif");

        // Ask for a region that runs off the right/bottom edge of a 32x8 page.
        var region = TiffReader.ReadBilevelRegion(path, 0, 28, 6, 16, 16);

        Assert.Equal(4 * 2, region.Length);
    }

    [Fact]
    public void Region_NonBitonalPlate_IsRejectedWithClearMessage()
    {
        // gray8_lzw.tif is 8 bits per sample: region decode is for bitonal plates only.
        var ex = Assert.Throws<SoftProof.RIPParsers.Raster.PbfFormatException>(
            () => TiffReader.ReadBilevelRegion(Fixture("gray8_lzw.tif"), 0, 0, 0, 8, 8));

        Assert.Contains("1-bit", ex.Message);
    }
}
