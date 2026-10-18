using SoftProof.RIPParsers.Raster.Compression;
using Xunit;

namespace SoftProof.Tests.Raster;

/// <summary>
/// The word-at-a-time run extractor must agree with a naive per-pixel scan for every width and
/// both polarities — including widths that are not multiples of 64, where the boundary between
/// words is the easy place to lose or invent a transition.
/// </summary>
public class PackedRowRunsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(127)]
    [InlineData(130)]
    [InlineData(6000)]
    public void MatchesNaiveScan_ForBothPolarities(int width)
    {
        var random = new Random(1234 + width);
        var rowBytes = (width + 7) / 8;
        var row = new byte[rowBytes];
        random.NextBytes(row);

        var transitions = new int[width + 2];

        foreach (var invert in new[] { false, true })
        {
            var count = PackedRowRuns.ToTransitions(row, width, transitions, invert);

            var expected = new List<int>();
            var previous = 0;
            for (var x = 0; x < width; x++)
            {
                var raw = (row[x >> 3] >> (7 - (x & 7))) & 1;
                var bit = invert ? 1 - raw : raw;
                if (bit != previous)
                    expected.Add(x);
                previous = bit;
            }

            Assert.Equal(expected.Count, count);
            for (var i = 0; i < count; i++)
                Assert.Equal(expected[i], transitions[i]);
        }
    }

    [Fact]
    public void MostlySolidRows_ProduceFewTransitions()
    {
        const int width = 4096;
        var row = new byte[width / 8];
        Array.Fill(row, (byte)0xFF);       // solid ink
        var transitions = new int[width + 2];

        var count = PackedRowRuns.ToTransitions(row, width, transitions);

        // One transition at 0 (paper -> ink) and nothing else.
        Assert.Equal(1, count);
        Assert.Equal(0, transitions[0]);
    }
}
