using SoftProof.RIPParsers.Raster.Tiff;
using Xunit;

namespace SoftProof.Tests.Raster;

/// <summary>
/// Regression cover for the panning crash: a 60000 x 73229 plate makes y * width exceed
/// int.MaxValue well before the last row. Reads must stay long-safe at every edge.
/// </summary>
public class LargePlateReadTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public async Task LastRow_LastStrips_AndBottomRightCorner_ReadWithoutOverflow()
    {
        using var source = TiffStripSource.Open(Fixture("holobox_background_g4.tif"));

        Assert.Equal(60000, source.Info.Width);
        Assert.Equal(73229, source.Info.Height);

        var rowBytes = source.Info.RowBytes;
        var lastRow = new byte[rowBytes];
        await source.ReadRowsAsync(source.Info.Height - 1, 1, lastRow);
        Assert.Equal(rowBytes, lastRow.Length);

        const int rows = 8;
        var corner = new byte[(long)rowBytes * rows];
        await source.ReadRowsAsync(source.Info.Height - rows, rows, corner);
        Assert.Equal(corner.Length, corner.Length);
    }

    [Fact]
    public async Task ReadsPastTheLastRow_AreRejectedWithoutTouchingMemory()
    {
        using var source = TiffStripSource.Open(Fixture("holobox_background_g4.tif"));
        var rowBytes = source.Info.RowBytes;
        var buffer = new byte[(long)rowBytes * 2];

        Func<Task> read = async () =>
            await source.ReadRowsAsync(source.Info.Height - 1, 2, buffer).AsTask();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(read);
    }

    [Fact]
    public async Task RandomPanRegions_ReadCleanlyAcrossTheWholePlate()
    {
        using var source = TiffStripSource.Open(Fixture("holobox_background_g4.tif"));
        var random = new Random(7);
        var rowBytes = source.Info.RowBytes;

        for (var i = 0; i < 12; i++)
        {
            var y = random.Next(0, source.Info.Height - 128);
            var rows = random.Next(32, 128);
            var buffer = new byte[(long)rowBytes * rows];

            await source.ReadRowsAsync(y, rows, buffer);
            Assert.Equal((long)rowBytes * rows, buffer.Length);
        }
    }
}
