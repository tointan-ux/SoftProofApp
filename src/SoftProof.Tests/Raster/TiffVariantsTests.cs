using SoftProof.RIPParsers.Raster.Tiff;
using Xunit;

namespace SoftProof.Tests.Raster;

/// <summary>
/// Real RIP outputs beyond plain single-strip TIFF: tiled rasters, FillOrder 2 and multi-page
/// files (one page per plate). Fixtures are libtiff-written with known pixel formulas.
/// </summary>
public class TiffVariantsTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Read_TiledTiff_ReassemblesEveryTile()
    {
        var image = TiffReader.Read(Fixture("gray8_tiled.tif"));

        Assert.Equal(64, image.Width);
        Assert.Equal(48, image.Height);

        var plane = image.Planes[0];
        for (var y = 0; y < 48; y++)
            for (var x = 0; x < 64; x++)
                Assert.Equal((x * 3 + y * 5) % 256, plane[y * 64 + x]);
    }

    [Fact]
    public void Read_FillOrder2_UnpacksLsbFirstBits()
    {
        var image = TiffReader.Read(Fixture("bilevel_fillorder2.tif"));

        Assert.Equal(32, image.Width);
        Assert.Equal(8, image.Height);
        Assert.True(image.IsBilevel);

        // Photometric 1: the stored 0 bits are ink. Artwork: (x/2 + y/2) odd.
        var coverage = image.InkCoverage();
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 32; x++)
            {
                var ink = (x / 2 + y / 2) % 2 == 1;
                Assert.Equal(ink ? 255 : 0, coverage[y * 32 + x]);
            }
    }

    [Fact]
    public void PageCount_And_PageName_WorkOnMultiPageFiles()
    {
        var path = Fixture("multipage_2plates.tif");
        Assert.Equal(2, TiffReader.PageCount(path));
        Assert.Null(TiffReader.ReadPageName(path, 0)); // no PageName tag in this fixture
    }

    [Fact]
    public void Read_MultiPage_ReturnsEachPageIndependently()
    {
        var path = Fixture("multipage_2plates.tif");
        var first = TiffReader.Read(path, 0);
        var second = TiffReader.Read(path, 1);

        Assert.Equal(16, first.Height);
        Assert.Equal(32, first.Width);

        var coverage0 = first.InkCoverage();
        var coverage1 = second.InkCoverage();

        for (var y = 0; y < 16; y++)
            for (var x = 0; x < 32; x++)
            {
                Assert.Equal(x < 16 ? 255 : 0, coverage0[y * 32 + x]);  // left half ink
                Assert.Equal(y < 8 ? 255 : 0, coverage1[y * 32 + x]);   // top half ink
            }
    }

    [Fact]
    public void Read_MissingPage_ThrowsDiagnostic()
    {
        var path = Fixture("multipage_2plates.tif");
        var ex = Assert.Throws<SoftProof.RIPParsers.Raster.PbfFormatException>(
            () => TiffReader.Read(path, 5));
        Assert.Contains("no page 5", ex.Message);
    }
}
