using SoftProof.Core.Models;
using SoftProof.RIPParsers.Raster;
using Xunit;

namespace SoftProof.Tests.Raster;

public class PlateLoaderTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static int ExpectedGray(int x, int y) => (x * 7 + y * 11) % 256;

    private static PrintJob OneBitJob(params (string Name, string File, SeparationColor Color)[] plates)
    {
        var job = new PrintJob
        {
            Name = "tiff-job",
            Format = RipFormat.OneBitTiffSeparations,
            Metadata = new JobMetadata { Width = 32, Height = 8, Resolution = 2400, BitDepth = 1 }
        };

        var index = 0;
        foreach (var (name, file, color) in plates)
        {
            job.Separations.Add(new Separation
            {
                Name = name,
                Index = index++,
                FilePath = Fixture(file),
                Color = color
            });
        }

        return job;
    }

    [Fact]
    public async Task LoadAsync_OneBitTiffJob_ProducesOneLayerPerSeparation()
    {
        var job = OneBitJob(
            ("Cyan", "gray8_lzw.tif", SeparationColor.ProcessCyan),
            ("Magenta", "gray8_packbits.tif", SeparationColor.ProcessMagenta));

        var warnings = new List<string>();
        var plates = await new PlateLoader().LoadAsync(job, warnings);

        Assert.Empty(warnings);
        Assert.Equal(2, plates.Count);

        Assert.Equal(32, plates[0].Width);
        Assert.Equal(8, plates[0].Height);
        Assert.Equal((0, 174, 239), (plates[0].R, plates[0].G, plates[0].B));
        Assert.Equal((236, 0, 140), (plates[1].R, plates[1].G, plates[1].B));

        // Ink coverage = 255 - stored luminance.
        Assert.Equal(255 - ExpectedGray(0, 0), plates[0].Coverage[0]);
        Assert.True(plates[0].InkedAreaPercent > 0);
        Assert.InRange(plates[0].InkedAreaPercent, 0, 100);
    }

    [Fact]
    public async Task LoadAsync_MultiChannelTiffJob_SplitsCmykSamplesIntoFourPlates()
    {
        var job = new PrintJob
        {
            Name = "cmyk",
            FilePath = Fixture("cmyk_separated_lzw.tif"),
            Format = RipFormat.MultiChannelTiff,
            Metadata = new JobMetadata { Width = 16, Height = 8, Resolution = 2400, BitDepth = 8, ColorSpace = ColorSpace.MultiChannel }
        };

        for (var i = 0; i < 4; i++)
        {
            job.Separations.Add(new Separation
            {
                Name = $"Channel_{i + 1}",
                Index = i,
                FilePath = job.FilePath,
                Color = (SeparationColor)i
            });
        }

        var plates = await new PlateLoader().LoadAsync(job);

        Assert.Equal(4, plates.Count);
        for (var s = 0; s < 4; s++)
        {
            for (var y = 0; y < 8; y++)
                for (var x = 0; x < 16; x++)
                {
                    var expected = s switch
                    {
                        0 => (x * 9) % 256,
                        1 => (y * 17) % 256,
                        2 => (x * y) % 256,
                        _ => ((x + y) * 5) % 256
                    };
                    Assert.Equal((byte)expected, plates[s].Coverage[y * 16 + x]);
                }
        }
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ReportsWarningInsteadOfThrowing()
    {
        var job = OneBitJob(("Cyan", "does-not-exist.tif", SeparationColor.ProcessCyan));
        var warnings = new List<string>();

        var plates = await new PlateLoader().LoadAsync(job, warnings);

        Assert.Empty(plates);
        Assert.Single(warnings);
        Assert.Contains("no readable file", warnings[0]);
    }

    [Fact]
    public async Task LoadAsync_UnsupportedFormat_ReturnsEmptyWithWarning()
    {
        var job = new PrintJob { Format = RipFormat.PDF, Name = "pdf" };
        job.Separations.Add(new Separation { Name = "Cyan" });
        var warnings = new List<string>();

        var plates = await new PlateLoader().LoadAsync(job, warnings);

        Assert.Empty(plates);
        Assert.Contains("no plate decoder", warnings[0]);
    }
}
