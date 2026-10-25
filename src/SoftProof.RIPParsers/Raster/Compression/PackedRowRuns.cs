using System.Numerics;

namespace SoftProof.RIPParsers.Raster.Compression;

/// <summary>
/// Derives colour-change positions from a packed 1 bpp row using 64-bit word tricks instead of
/// a per-pixel loop. A run boundary is a bit that differs from its left neighbour, so
/// <c>word ^ (word << 1)</c> marks them all at once and
/// <see cref="BitOperations.LeadingZeroCount"/> enumerates them MSB-first.
/// </summary>
public static class PackedRowRuns
{
    /// <summary>
    /// Write transitions (positions where the ink state changes, ascending) into
    /// <paramref name="transitions"/> and return how many there are. With
    /// <paramref name="invert"/> the row is complemented first, which is how a WhiteIsZero
    /// sample stream becomes "1 = ink" without a temporary buffer.
    /// </summary>
    public static int ToTransitions(ReadOnlySpan<byte> row, int width, Span<int> transitions,
                                    bool invert = false)
    {
        if (width <= 0 || transitions.IsEmpty)
            return 0;

        var rowBytes = (width + 7) / 8;
        var previousBit = 0; // outside the row is paper
        var count = 0;

        for (var byteStart = 0; byteStart < rowBytes; byteStart += 8)
        {
            ulong word = 0;
            for (var k = 0; k < 8; k++)
            {
                var index = byteStart + k;
                var value = index < row.Length ? row[index] : (byte)0;
                if (invert)
                    value ^= 0xFF; // Avoid overflow in checked context: ~byte promotes to int
                word = (word << 8) | value;
            }

            // Bits are MSB-first, so pixel i's left neighbour is bit i+1: shift DOWN and feed the
            // previous word's last pixel into the top bit for the word boundary.
            var changes = word ^ ((word >> 1) | ((ulong)previousBit << 63));
            previousBit = (int)(word & 1UL); // last pixel of this word

            var wordStart = byteStart * 8;
            while (changes != 0)
            {
                var lzc = BitOperations.LeadingZeroCount(changes);
                var bit = 63 - lzc;
                // Debug: check for invalid bit position
                if (bit < 0 || bit >= 64)
                {
                    throw new InvalidOperationException($"Invalid bit position: bit={bit}, lzc={lzc}, changes=0x{changes:X16}, wordStart={wordStart}, width={width}, rowBytes={rowBytes}, byteStart={byteStart}");
                }
                changes &= ~(1UL << bit);

                // Bit 63 is the first pixel of the word, so the pixel index counts down from 63.
                var position = wordStart + (63 - bit);
                if (position >= width)
                    continue; // padding bits past the plate width

                transitions[count++] = position;
                if (count == transitions.Length)
                    return count;
            }
        }

        return count;
    }
}