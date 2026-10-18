using Microsoft.Extensions.Logging.Abstractions;
using SoftProof.RIPParsers;
using SoftProof.RIPParsers.Raster;
using Xunit;

namespace SoftProof.Tests.Raster;

/// <summary>Multi-page plate TIFFs: one page per separation, as many RIPs write them.</summary>
public class MultiPageJobTests
{
    [Fact]
    public async Task Parse_MultiPageTiff_CreatesSeparationPerPage()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Fixtures", "multipage_2plates.tif");
        var parser = new OneBitTiffSeparationParser(
            NullLogger<OneBitTiffSeparationParser>.Instance);

        var job = await parser.ParseAsync(file);

        Assert.Equal(2, job.Separations.Count);
        Assert.Equal(new[] { 0, 1 }, job.Separations.Select(s => s.PageIndex).ToArray());
        Assert.Equal(32, job.Metadata.Width);
        Assert.Equal(16, job.Metadata.Height);
    }

    [Fact]
    public async Task ParseAndLoad_MultiPageTiff_ProducesOnePlatePerPage()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Fixtures", "multipage_2plates.tif");
        var parser = new OneBitTiffSeparationParser(
            NullLogger<OneBitTiffSeparationParser>.Instance);

        var job = await parser.ParseAsync(file);
        var warnings = new List<string>();
        var plates = await new PlateLoader().LoadAsync(job, warnings);

        Assert.Empty(warnings);
        Assert.Equal(2, plates.Count);

        // Page 0: left half inked; page 1: top half inked. Both are 50% of a 32x16 plate.
        Assert.Equal(50.0, plates[0].InkedAreaPercent, precision: 1);
        Assert.Equal(50.0, plates[1].InkedAreaPercent, precision: 1);

        Assert.Equal(255, plates[0].Coverage[0]);                  // x=0 -> ink
        Assert.Equal(0, plates[0].Coverage[31]);                   // x=31 -> paper
        Assert.Equal(255, plates[1].Coverage[0]);                  // y=0 -> ink
        Assert.Equal(0, plates[1].Coverage[15 * 32]);              // y=15 -> paper
    }
}
