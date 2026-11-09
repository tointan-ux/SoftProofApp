using Microsoft.Extensions.Logging.Abstractions;
using SoftProof.Core.Models;
using SoftProof.RIPParsers;
using SoftProof.RIPParsers.Raster;
using SoftProof.RIPParsers.Raster.Tiff;
using Xunit;

namespace SoftProof.Tests.Raster;

/// <summary>
/// Resolution handling: the plate's own XResolution/YResolution must reach the job, because
/// every physical measurement (mm ruler, densitometer aperture, lpi, dot gain) scales from it.
/// A hardcoded 2400 dpi makes all of them wrong on any other plate.
/// </summary>
public class TiffResolutionTests
{
    private static string TempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"softproof-dpi-{Guid.NewGuid():N}.tif");
        return path;
    }

    private static string WritePlate(int dpi, int resolutionUnit = 2)
    {
        var path = TempFile();
        TiffBuilder.WriteClassic(path, 32, 8, photometric: 1, rowsPerStrip: 8,
                                 ink: (x, y) => (x + y) % 3 == 0,
                                 dpi: dpi, resolutionUnit: resolutionUnit);
        return path;
    }

    [Fact]
    public void GetInfo_ReadsResolutionFromTags()
    {
        var path = WritePlate(1200);
        try
        {
            Assert.Equal(1200, TiffReader.GetInfo(path).Dpi);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetInfo_ConvertsCentimetreUnitToDpi()
    {
        // 118 dots/cm is the usual way a 300 dpi plate is written with unit 3.
        var path = WritePlate(118, resolutionUnit: 3);
        try
        {
            Assert.Equal(300, TiffReader.GetInfo(path).Dpi);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetInfo_WithoutResolutionTag_ReportsUnknownRatherThanZeroDpi()
    {
        var path = WritePlate(dpi: 0);
        try
        {
            // 0 is the "unknown" sentinel; callers decide the fallback.
            Assert.Equal(0, TiffReader.GetInfo(path).Dpi);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Parser_UsesFileResolution_NotTheHardcodedDefault()
    {
        var path = WritePlate(1200);
        try
        {
            var parser = new OneBitTiffSeparationParser(
                NullLogger<OneBitTiffSeparationParser>.Instance);

            var job = await parser.ParseAsync(path);

            Assert.Equal(1200, job.Metadata.Resolution);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Parser_WithoutResolutionTag_FallsBackToTheDocumentedDefault()
    {
        var path = WritePlate(dpi: 0);
        try
        {
            var parser = new OneBitTiffSeparationParser(
                NullLogger<OneBitTiffSeparationParser>.Instance);

            var job = await parser.ParseAsync(path);

            Assert.Equal(JobMetadata.FallbackResolution, job.Metadata.Resolution);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Loader_KeepsPlatesAtTheirOwnResolution()
    {
        // Two plates from one job at different DPIs: the loader must not flatten them to the
        // job-level value, or the smaller plate's mm scale is wrong.
        var slow = WritePlate(1200);
        var fast = WritePlate(2400);
        try
        {
            var job = new PrintJob
            {
                Format = RipFormat.OneBitTiffSeparations,
                Metadata = new JobMetadata { Width = 32, Height = 8, Resolution = 1200 }
            };
            job.Separations.Add(new Separation
            {
                Name = "Cyan", Index = 0, FilePath = slow, PageIndex = 0,
                Color = SeparationColor.ProcessCyan
            });
            job.Separations.Add(new Separation
            {
                Name = "Magenta", Index = 1, FilePath = fast, PageIndex = 0,
                Color = SeparationColor.ProcessMagenta
            });

            var plates = await new PlateLoader().LoadAsync(job);

            Assert.Equal(2, plates.Count);
            Assert.Equal(1200, plates[0].Resolution);
            Assert.Equal(2400, plates[1].Resolution);
        }
        finally
        {
            File.Delete(slow);
            File.Delete(fast);
        }
    }
}
