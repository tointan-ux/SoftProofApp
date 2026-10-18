using SoftProof.RIPParsers.Raster.Tiff;
using Xunit;

namespace SoftProof.Tests.Raster;

/// <summary>
/// Single-strip G4 has no random access inside the stream; checkpoints (bit offset + reference
/// line every N rows) make later seeks cost one interval instead of the whole page.
/// </summary>
public class G4CheckpointTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public async Task ReverseReads_MatchOracle_AndWriteReusableSidecar()
    {
        var path = Fixture("bilevel_g4.tif");
        var oracle = TiffReader.Read(path);
        var coverage = oracle.InkCoverage();

        using var source = TiffStripSource.Open(path);
        source.CheckpointInterval = 8;

        Assert.NotNull(source.CheckpointPath);
        if (File.Exists(source.CheckpointPath))
            File.Delete(source.CheckpointPath);

        var rowBytes = source.Info.RowBytes;
        var packed = new byte[rowBytes * source.Info.Height];

        // Walk backwards: every read starts beyond the previous one, which forces the source to
        // resume from a checkpoint rather than replaying the stream from row 0.
        for (var start = source.Info.Height - 4; start >= 0; start -= 4)
        {
            var n = Math.Min(4, source.Info.Height - start);
            await source.ReadRowsAsync(start, n, packed.AsMemory(start * rowBytes, n * rowBytes));

            // Proof that the read resumed from a checkpoint instead of replaying from row 0.
            if (start == 32)
                Assert.Equal(32, source.LastDecodeStartRow);
        }

        for (var y = 0; y < source.Info.Height; y++)
            for (var x = 0; x < source.Info.Width; x++)
            {
                var bit = (packed[y * rowBytes + (x >> 3)] >> (7 - (x & 7))) & 1;
                Assert.True(bit == 1 == (coverage[y * oracle.Width + x] > 0), $"pixel ({x},{y})");
            }

        Assert.True(File.Exists(source.CheckpointPath), "checkpoint sidecar was not written");

        // A new source instance must load the sidecar and still return identical rows.
        using var reopened = TiffStripSource.Open(path);
        var again = new byte[rowBytes * 8];
        await reopened.ReadRowsAsync(32, 8, again);
        Assert.Equal(packed.AsSpan(32 * rowBytes, 8 * rowBytes).ToArray(), again);
    }

    [Fact]
    public async Task MultiStripPlate_DoesNotUseCheckpoints()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sp_nockpt_{Guid.NewGuid():N}.tif");
        try
        {
            TiffBuilder.WriteClassic(path, 40, 20, photometric: 0, rowsPerStrip: 4,
                                    ink: (x, y) => ((x + y) % 7) == 0);

            using var source = TiffStripSource.Open(path);

            Assert.Null(source.CheckpointPath);

            var rowBytes = source.Info.RowBytes;
            var packed = new byte[rowBytes * 20];
            await source.ReadRowsAsync(0, 20, packed);
            Assert.Equal(TiffBuilder.Render(40, 20, (x, y) => ((x + y) % 7) == 0), packed);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
