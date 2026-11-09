using SoftProof.Viewer;
using Xunit;

namespace SoftProof.Tests.Rendering;

/// <summary>
/// Per-plate flips: the mirror is applied when sampling a plate's coverage, so a flipped plate
/// sits in the same canvas rectangle as before.
/// </summary>
public class PlateFlipTests
{
    [Fact]
    public void WithoutFlip_MapsEveryPixelToItself()
    {
        for (var x = 0; x < 7; x++)
            Assert.Equal(x, PlateFlip.MapX(x, 7, flip: false));

        for (var y = 0; y < 5; y++)
            Assert.Equal(y, PlateFlip.MapY(y, 5, flip: false));
    }

    [Fact]
    public void HorizontalFlip_MirrorsColumnsAndKeepsRows()
    {
        Assert.Equal(6, PlateFlip.MapX(0, 7, flip: true));
        Assert.Equal(0, PlateFlip.MapX(6, 7, flip: true));
        Assert.Equal(3, PlateFlip.MapX(3, 7, flip: true));   // centre stays put
        Assert.Equal(2, PlateFlip.MapY(2, 5, flip: false));
    }

    [Fact]
    public void VerticalFlip_MirrorsRowsAndKeepsColumns()
    {
        Assert.Equal(4, PlateFlip.MapY(0, 5, flip: true));
        Assert.Equal(0, PlateFlip.MapY(4, 5, flip: true));
        Assert.Equal(1, PlateFlip.MapX(1, 7, flip: false));
    }

    [Fact]
    public void FlipIsItsOwnInverse()
    {
        for (var x = 0; x < 11; x++)
            Assert.Equal(x, PlateFlip.MapX(PlateFlip.MapX(x, 11, true), 11, true));

        for (var y = 0; y < 6; y++)
            Assert.Equal(y, PlateFlip.MapY(PlateFlip.MapY(y, 6, true), 6, true));
    }

    [Fact]
    public void EmptyPlate_DoesNotProduceNegativeIndices()
    {
        Assert.Equal(0, PlateFlip.MapX(0, 0, flip: true));
        Assert.Equal(0, PlateFlip.MapY(0, 0, flip: true));
    }

    [Fact]
    public void BufferRow_ReversesOnlyWhenFlipped()
    {
        Assert.Equal(0, PlateFlip.BufferRow(0, 10, flip: false));
        Assert.Equal(9, PlateFlip.BufferRow(9, 10, flip: false));

        Assert.Equal(9, PlateFlip.BufferRow(0, 10, flip: true));
        Assert.Equal(0, PlateFlip.BufferRow(9, 10, flip: true));
    }

    [Fact]
    public void BufferRow_MatchesTheMirroredSourceRow()
    {
        // Display row 2 of a 100-row read starting at native row 200, in a 1000-row plate,
        // must come from source row 797 (mirror of 202) — i.e. buffer index 97 of 100.
        const int sourceHeight = 1000;
        const int nativeY = 200;
        const int nativeRows = 100;

        var readY = sourceHeight - (nativeY + nativeRows);
        Assert.Equal(700, readY);

        var bufferRow = PlateFlip.BufferRow(displayRow: 2, nativeRows, flip: true);
        Assert.Equal(sourceHeight - 1 - (nativeY + 2), readY + bufferRow);
    }
}
