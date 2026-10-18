using Microsoft.Extensions.Logging.Abstractions;
using SoftProof.Core.Models;
using SoftProof.RIPParsers;
using SoftProof.RIPParsers.Raster.Tiff;
using Xunit;

namespace SoftProof.Tests.Raster;

/// <summary>
/// Huge strip-based 1-bit plates (the 7 Gpx case) must stream and downsample, plus plate
/// identity recovery from filenames/descriptions.
/// </summary>
public class LargeBilevelTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void ReadBilevelCoverage_LargeLzwPlate_StreamsAndDownsamples()
    {
        // 6000 x 4000 (24 Mpx) LZW plate, right half inked, 46 strips.
        var coverage = TiffReader.ReadBilevelCoverage(
            Fixture("large_bilevel_lzw.tif"), pageIndex: 0, maxPixels: 2_000_000,
            out var width, out var height, out var factor);

        Assert.Equal(4, factor);
        Assert.Equal(1500, width);
        Assert.Equal(1000, height);
        Assert.Equal(1500 * 1000, coverage.Length);

        Assert.Equal(0, coverage[0]);                       // left half: paper
        Assert.Equal(255, coverage[width - 1]);             // right half: solid ink
        Assert.Equal(0, coverage[(height - 1) * width]);    // left half, last row
        Assert.Equal(255, coverage[(height - 1) * width + width - 1]);

        var inked = coverage.Count(v => v >= 128);
        var percent = inked * 100.0 / coverage.Length;
        Assert.InRange(percent, 49.0, 51.0);
    }

    [Fact]
    public void ReadBilevelCoverage_G4Plate_UsesTheSameStreamingPath()
    {
        var coverage = TiffReader.ReadBilevelCoverage(
            Fixture("holobox_background_g4.tif"), 0, 8_000_000,
            out var width, out var height, out var factor);

        Assert.True(factor > 1);
        Assert.Equal((long)width * height, coverage.LongLength);
    }

    [Theory]
    [InlineData(@"C:\jobs\Ace Kid mod tuck in DE_C.tif", "Cyan")]
    [InlineData(@"C:\jobs\Ace Kid mod tuck in DE_M.tiff", "Magenta")]
    [InlineData(@"C:\jobs\job.Yellow.tif", "Yellow")]
    [InlineData(@"C:\jobs\job(K)01.tif", "Black")]
    [InlineData(@"C:\jobs\job.cmyk-K.tif", "Black")]
    [InlineData(@"C:\jobs\artwork_final.tif", "artwork_final")]
    public void ResolveName_ReadsColourSuffixes(string path, string expected)
    {
        Assert.Equal(expected, SeparationNaming.ResolveName(path));
    }

    [Fact]
    public void ResolveName_PrefersDescriptionTokenOverFileName()
    {
        var name = SeparationNaming.ResolveName(@"C:\jobs\plate_01.tif", "Separation: Spot Orange");
        Assert.Equal("Orange", name);
    }

    [Fact]
    public async Task Parser_UsesFileNameSuffixForSeparationIdentity()
    {
        // Same bytes, name carries the plate colour.
        var source = Fixture("gray8_lzw.tif");
        var temp = Path.Combine(Path.GetTempPath(), $"job_C_{Guid.NewGuid():N}.tif");
        File.Copy(source, temp);

        try
        {
            var parser = new OneBitTiffSeparationParser(
                NullLogger<OneBitTiffSeparationParser>.Instance);
            var job = await parser.ParseAsync(temp);

            Assert.Single(job.Separations);
            Assert.Equal("Cyan", job.Separations[0].Name);
            Assert.Equal(SeparationColor.ProcessCyan, job.Separations[0].Color);
            Assert.False(job.Separations[0].IsSpotColor);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
