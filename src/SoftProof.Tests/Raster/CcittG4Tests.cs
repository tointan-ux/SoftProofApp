using SoftProof.RIPParsers;
using SoftProof.RIPParsers.Raster;
using SoftProof.RIPParsers.Raster.Tiff;
using Xunit;

namespace SoftProof.Tests.Raster;

/// <summary>
/// CCITT Group 4 (T.6 MMR): the compression real bitonal plates use. The small fixture is
/// libtiff-written and pixel-checked; the large one is an actual 60000 x 73229 production
/// background whose byte size is 34 KB but whose full raster would be 4.4 GB.
/// </summary>
public class CcittG4Tests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Read_G4Tiff_MatchesLibtiffPixels()
    {
        var image = TiffReader.Read(Fixture("bilevel_g4.tif"));

        Assert.Equal(128, image.Width);
        Assert.Equal(64, image.Height);
        Assert.True(image.IsBilevel);

        // libtiff wrote the fixture with photometric 1; PIL confirms black where (x/8+y/8) is odd.
        var coverage = image.InkCoverage();
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var black = (x / 8 + y / 8) % 2 == 1;
                Assert.Equal(black ? 255 : 0, coverage[y * image.Width + x]);
            }
    }

    [Fact]
    public void ReadG4Coverage_HugeProductionPlate_DownsamplesInsteadOfAllocating()
    {
        var path = Fixture("holobox_background_g4.tif");
        var info = TiffReader.GetInfo(path);

        Assert.Equal(60000, info.Width);
        Assert.Equal(73229, info.Height);
        Assert.Equal(4, info.Compression); // CCITT Group 4
        Assert.Equal(1, info.BitsPerSample);
        Assert.Equal(0, info.Photometric); // WhiteIsZero: black is ink

        var coverage = TiffReader.ReadG4Coverage(path, pageIndex: 0, maxPixels: 8_000_000,
                                                 out var width, out var height, out var factor);

        Assert.True(factor > 1, "a 4.4 Gpx plate must be downsampled");
        Assert.Equal((60000 + factor - 1) / factor, width);
        Assert.Equal((73229 + factor - 1) / factor, height);
        Assert.Equal((long)width * height, coverage.LongLength);
        Assert.True((long)width * height <= 8_000_000);

        // A holobox background is mostly paper: ink coverage must be a small fraction.
        long inked = 0;
        foreach (var value in coverage)
            if (value >= 128) inked++;

        var percent = inked * 100.0 / coverage.Length;
        Assert.InRange(percent, 0.0, 60.0);
    }

    [Fact]
    public void Read_FullResolutionOnHugeG4_ThrowsActionableMessage()
    {
        var ex = Assert.Throws<PbfFormatException>(
            () => TiffReader.Read(Fixture("holobox_background_g4.tif")));

        Assert.Contains("60000", ex.Message);
        Assert.Contains("ReadG4Coverage", ex.Message);
    }

    [Fact]
    public void ReadG4Coverage_SmallPlate_KeepsFullResolution()
    {
        var coverage = TiffReader.ReadG4Coverage(Fixture("bilevel_g4.tif"), 0, 8_000_000,
                                                 out var width, out var height, out var factor);

        Assert.Equal(1, factor);
        Assert.Equal(128, width);
        Assert.Equal(64, height);
        Assert.Equal(128 * 64, coverage.Length);
    }

    [Fact]
    public async Task ParserAndLoader_HugeG4Plate_ProducesAPlateInsteadOfAnError()
    {
        // The exact path the UI takes for the attached file.
        var file = Fixture("holobox_background_g4.tif");
        var parser = new Microsoft.Extensions.Logging.Abstractions.NullLogger<OneBitTiffSeparationParser>();
        var job = await new OneBitTiffSeparationParser(parser).ParseAsync(file);

        Assert.Single(job.Separations);
        Assert.Equal(60000, job.Metadata.Width);
        Assert.Equal(73229, job.Metadata.Height);

        var warnings = new List<string>();
        var plates = await new PlateLoader().LoadAsync(job, warnings);

        Assert.Single(plates);
        // The job keeps the native plate metadata; only the decoded raster is smaller.
        Assert.Equal(60000, job.Metadata.Width);
        Assert.Equal(73229, job.Metadata.Height);
        Assert.Equal(2400, job.Metadata.Resolution);
        Assert.True(plates[0].Width < 60000, "the decoded raster must be downsampled");
        Assert.True(plates[0].Height < 73229);

        // The overview decimation the UI uses for "view 300 dpi" and "Overview 1/8".
        Assert.Equal(8, plates[0].Pyramid!.Base.Factor);
        Assert.Equal(300, 2400 / plates[0].Pyramid!.Base.Factor);

        Assert.Contains(warnings, w => w.Contains("decoded"));
        Assert.InRange(plates[0].InkedAreaPercent, 0.0, 60.0);
    }
}
