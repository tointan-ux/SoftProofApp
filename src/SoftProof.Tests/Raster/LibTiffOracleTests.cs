using BitMiracle.LibTiff.Classic;
using SoftProof.RIPParsers.Raster.Tiff;
using Xunit;

namespace SoftProof.Tests.Raster;

/// <summary>
/// Cross-checks our strip decoder against libtiff (the managed LibTiff.NET port): same files,
/// same rows, pixel for pixel, with polarity normalised to "1 = ink" on both sides.
/// This is the independent oracle — the production decoder is never used to validate itself.
/// </summary>
public class LibTiffOracleTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Theory]
    [InlineData("bilevel_g4.tif")]          // CCITT Group 4, BlackIsZero
    [InlineData("bilevel_lzw.tif")]         // LZW, BlackIsZero
    [InlineData("bilevel_blackiszero.tif")] // uncompressed, BlackIsZero
    [InlineData("bilevel_miniswhite.tif")]  // uncompressed, WhiteIsZero
    public async Task Rows_MatchLibTiffOracle(string fixture)
    {
        var path = Fixture(fixture);

        using var tiff = Tiff.Open(path, "r");
        Assert.NotNull(tiff);

        var width = tiff.GetField(TiffTag.IMAGEWIDTH)![0].ToInt();
        var height = tiff.GetField(TiffTag.IMAGELENGTH)![0].ToInt();
        var photometric = tiff.GetField(TiffTag.PHOTOMETRIC)![0].ToInt();
        var scanlineSize = tiff.ScanlineSize();
        var rowBytes = (width + 7) / 8;

        using var source = TiffStripSource.Open(path);
        Assert.Equal(width, source.Info.Width);
        Assert.Equal(height, source.Info.Height);

        var ours = new byte[rowBytes * height];
        await source.ReadRowsAsync(0, height, ours);

        var libRow = new byte[scanlineSize];
        for (var y = 0; y < height; y++)
        {
            Assert.True(tiff.ReadScanline(libRow, y), $"{fixture}: libtiff failed at row {y}");

            for (var x = 0; x < width; x++)
            {
                var libBit = (libRow[x >> 3] >> (7 - (x & 7))) & 1;

                // libtiff returns the sample; photometric decides which sample is ink.
                var libInk = photometric == 0 ? libBit == 1 : libBit == 0;
                var ourBit = (ours[y * rowBytes + (x >> 3)] >> (7 - (x & 7))) & 1;

                Assert.True(ourBit == 1 == libInk,
                    $"{fixture}: mismatch at ({x},{y}) ours={ourBit} libtiffInk={libInk}");
            }
        }
    }

    [Fact]
    public async Task RandomRowWindows_MatchLibTiff()
    {
        var path = Fixture("bilevel_g4.tif");

        using (var probe = Tiff.Open(path, "r"))
        {
            var probeWidth = probe.GetField(TiffTag.IMAGEWIDTH)![0].ToInt();
            var probeHeight = probe.GetField(TiffTag.IMAGELENGTH)![0].ToInt();
            var probePhoto = probe.GetField(TiffTag.PHOTOMETRIC)![0].ToInt();
            var probeRowBytes = (probeWidth + 7) / 8;
            var random = new Random(7);

            using var source = TiffStripSource.Open(path);

            for (var attempt = 0; attempt < 8; attempt++)
            {
                var start = random.Next(0, probeHeight - 4);
                var count = random.Next(1, 4);

                var ours = new byte[probeRowBytes * count];
                await source.ReadRowsAsync(start, count, ours);

                // libtiff's scanline reader is sequential: replay from row 0 for this window.
                using var tiff = Tiff.Open(path, "r");
                var scanlineSize = tiff.ScanlineSize();
                var libRow = new byte[scanlineSize];

                for (var y = 0; y <= start + count - 1; y++)
                {
                    Assert.True(tiff.ReadScanline(libRow, y), $"libtiff failed at row {y}");

                    if (y < start)
                        continue;

                    var r = y - start;
                    for (var x = 0; x < probeWidth; x++)
                    {
                        var libBit = (libRow[x >> 3] >> (7 - (x & 7))) & 1;
                        var libInk = probePhoto == 0 ? libBit == 1 : libBit == 0;
                        var ourBit = (ours[r * probeRowBytes + (x >> 3)] >> (7 - (x & 7))) & 1;
                        Assert.True(ourBit == 1 == libInk, $"window {start}+{count}: ({x},{y})");
                    }
                }
            }
        }
    }
}
