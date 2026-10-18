using Microsoft.Extensions.Logging.Abstractions;
using SoftProof.Core.Models;
using SoftProof.RIPParsers;
using SoftProof.RIPParsers.Raster;
using SoftProof.RIPParsers.Raster.Pbf;
using Xunit;

namespace SoftProof.Tests.Raster;

/// <summary>
/// End-to-end: the same path the UI takes for a real file — parser registry first, then the
/// plate loader. This is the regression test for the "opens a 1-bit plate but shows a blank
/// page" bug, where metadata parsed fine but no PlateLayer was ever produced.
/// </summary>
public class ParsedJobLoadsRasterTests
{
    [Fact]
    public async Task PageBuffer_ParsedThenLoaded_YieldsNonEmptyPlates()
    {
        const int width = 48, height = 16;
        var cyan = new byte[width * height];
        var magenta = new byte[width * height];

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                cyan[y * width + x] = x < width / 2 ? (byte)255 : (byte)0;
                magenta[y * width + x] = y < height / 2 ? (byte)255 : (byte)0;
            }

        var path = Path.Combine(Path.GetTempPath(), $"softproof-e2e-{Guid.NewGuid():N}.pbuf");

        try
        {
            PbfWriter.Write(path, width, height, 2400, 2, new[]
            {
                new PbfPlateInput { Name = "Cyan", Coverage = cyan, Compression = PbfCompression.PackBits },
                new PbfPlateInput { Name = "Magenta", Coverage = magenta, Compression = PbfCompression.Lzw }
            });

            var parser = new HarlequinPageBufferParser(NullLogger<HarlequinPageBufferParser>.Instance);

            Assert.True(await parser.CanParseAsync(path));

            var job = await parser.ParseAsync(path);

            Assert.Equal(RipFormat.HarlequinPageBuffer, job.Format);
            Assert.Equal(JobStatus.Ready, job.Status);
            Assert.Equal(2, job.Separations.Count);
            Assert.Equal(width, job.Metadata.Width);
            Assert.Equal(height, job.Metadata.Height);
            Assert.Contains("Cyan", job.Metadata.SeparationNames);
            Assert.Equal(SeparationColor.ProcessCyan, job.Separations[0].Color);
            Assert.All(job.Separations, s => Assert.True(s.DataSize > 0));
            Assert.All(job.Separations, s => Assert.NotNull(s.Data)); // eagerly decoded

            var warnings = new List<string>();
            var plates = await new PlateLoader().LoadAsync(job, warnings);

            Assert.Empty(warnings);
            Assert.Equal(2, plates.Count);
            Assert.Equal(cyan, plates[0].Coverage);
            Assert.Equal(magenta, plates[1].Coverage);
            Assert.Equal(50.0, plates[0].InkedAreaPercent, precision: 1);
            Assert.Equal(50.0, plates[1].InkedAreaPercent, precision: 1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OneBitTiff_ParsedThenLoaded_YieldsAPlateWithInk()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Fixtures", "gray8_lzw.tif");
        var parser = new OneBitTiffSeparationParser(
            NullLogger<OneBitTiffSeparationParser>.Instance);

        var job = await parser.ParseAsync(file);
        var plates = await new PlateLoader().LoadAsync(job);

        Assert.Single(job.Separations);
        Assert.Single(plates);
        Assert.Equal(32, plates[0].Width);
        Assert.Equal(8, plates[0].Height);
        Assert.True(plates[0].InkedAreaPercent > 0,
            "A parsed 1-bit TIFF must produce a plate with ink, not an empty page.");
    }
}
