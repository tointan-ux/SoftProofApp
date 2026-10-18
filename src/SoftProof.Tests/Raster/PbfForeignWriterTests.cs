using SoftProof.Core.Models;
using SoftProof.RIPParsers;
using SoftProof.RIPParsers.Raster;
using SoftProof.RIPParsers.Raster.Pbf;
using Xunit;

namespace SoftProof.Tests.Raster;

/// <summary>
/// Fixtures/pagebuffer_v2.pbuf was produced by an independent Python implementation of the
/// documented v2 layout (not by PbfWriter). Reading it proves the container spec is implemented
/// as written rather than merely being self-consistent with our own writer.
/// </summary>
public class PbfForeignWriterTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static int CountInk(byte[] coverage)
    {
        var count = 0;
        foreach (var value in coverage)
            if (value >= 128) count++;
        return count;
    }

    [Fact]
    public void ReadDocument_ForeignWrittenPageBuffer_ExposesHeaderAndDirectory()
    {
        var (header, separations) = PbfReader.ReadDocument(Fixture("pagebuffer_v2.pbuf"));

        Assert.Equal(2, header.Version);
        Assert.Equal(1200, header.Width);
        Assert.Equal(900, header.Height);
        Assert.Equal(300, header.Resolution);
        Assert.Equal(1, header.BitDepth);
        Assert.Equal(4, header.SeparationCount);
        Assert.Equal(new[] { "Cyan", "Magenta", "Yellow", "Black" },
                     header.Names.ToArray());

        Assert.Equal(4, separations.Count);
        Assert.Equal(PbfCompression.None, separations[0].Compression);
        Assert.Equal(PbfCompression.PackBits, separations[1].Compression);
        Assert.Equal(PbfCompression.PackBits, separations[2].Compression);
        Assert.Equal(PbfCompression.None, separations[3].Compression);

        // Directory offsets must be ascending and inside the file.
        var length = new FileInfo(Fixture("pagebuffer_v2.pbuf")).Length;
        var previous = 0L;
        foreach (var separation in separations)
        {
            Assert.True(separation.DataOffset > previous);
            Assert.True(separation.DataOffset + separation.CompressedSize <= length);
            previous = separation.DataOffset;
        }
    }

    [Fact]
    public async Task PlateLoader_ForeignPageBuffer_ProducesFourPlatesWithExpectedInk()
    {
        var path = Fixture("pagebuffer_v2.pbuf");

        var parser = new HarlequinPageBufferParser(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HarlequinPageBufferParser>.Instance);
        var job = await parser.ParseAsync(path);

        Assert.Equal(4, job.Separations.Count);
        Assert.Equal(SeparationColor.ProcessCyan, job.Separations[0].Color);
        Assert.Equal(SeparationColor.ProcessBlack, job.Separations[3].Color);

        var plates = await new PlateLoader().LoadAsync(job);

        Assert.Equal(4, plates.Count);
        Assert.All(plates, p =>
        {
            Assert.Equal(1200, p.Width);
            Assert.Equal(900, p.Height);
        });

        // Ink pixel counts painted by the generator.
        Assert.Equal(339569, CountInk(plates[0].Coverage));
        Assert.Equal(322400, CountInk(plates[1].Coverage));
        Assert.Equal(187200, CountInk(plates[2].Coverage));
        Assert.Equal(136000, CountInk(plates[3].Coverage));

        Assert.All(plates, p => Assert.InRange(p.InkedAreaPercent, 1, 99));
    }
}
