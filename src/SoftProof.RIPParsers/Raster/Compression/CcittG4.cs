using System.Buffers.Binary;

namespace SoftProof.RIPParsers.Raster.Compression;

/// <summary>
/// ITU-T T.6 (MMR / CCITT Group 4) decoder for bitonal plates.
/// <para>
/// Decodes row by row and reports each row as a sorted list of colour transitions
/// (0 = first transition white→black), so callers can accumulate ink without ever
/// materialising a full-page bitmap. That matters for real plates: a 60000 x 73229
/// background would need 4.4 GB as one byte per pixel.
/// </para>
/// </summary>
public static class CcittG4
{
    private const int PassMode = 100;
    private const int HorizontalMode = 101;
    private const int EndOfData = 102;

    private static readonly Dictionary<int, int> WhiteCodes = BuildTable(CcittTables.White);
    private static readonly Dictionary<int, int> BlackCodes = BuildTable(CcittTables.Black);

    private static Dictionary<int, int> BuildTable(CcittTables.Code[] codes)
    {
        var table = new Dictionary<int, int>(codes.Length * 2);
        foreach (var code in codes)
            table[(code.Bits << 16) | (int)code.Value] = code.Run;
        return table;
    }

    /// <summary>
    /// Decodes the image, invoking <paramref name="onRow"/> with (y, transitions, count).
    /// Transitions are pixel positions where the colour changes; even indices start a black run.
    /// Decoding stops cleanly at end-of-data, filling the remaining rows with white.
    /// </summary>
    public static void DecodeRows(ReadOnlySpan<byte> data, int width, int height,
                                  Action<int, int[], int> onRow)
    {
        if (width <= 0 || height <= 0)
            throw new PbfFormatException($"CCITT image has invalid size {width}x{height}.");

        var cursor = new Cursor(data, width);
        for (var y = 0; y < height; y++)
        {
            var ok = cursor.TryDecodeRow(out var transitions, out var count);
            onRow(y, transitions, ok ? count : 0);
        }
    }

    /// <summary>
    /// Resumable row-at-a-time decoder. After each row, <see cref="BitOffset"/> and
    /// <see cref="ReferenceLine"/> describe exactly the state needed to restart at
    /// <see cref="Row"/> — store those as a checkpoint and a later seek costs at most one
    /// checkpoint interval of decoding.
    /// </summary>
    public sealed class Cursor
    {
        private readonly MsbBitReader _bits;
        private readonly int _width;
        private int[] _reference;
        private int[] _current;
        private int _referenceCount;
        private bool _finished;

        public Cursor(ReadOnlySpan<byte> data, int width, long bitOffset = 0,
                      ReadOnlySpan<int> reference = default, int startRow = 0)
            : this(data.ToArray(), width, bitOffset, reference, startRow)
        {
        }

        /// <summary>Overload that does not copy the payload (use for large strips).</summary>
        public Cursor(byte[] data, int width, long bitOffset = 0,
                      ReadOnlySpan<int> reference = default, int startRow = 0)
        {
            if (width <= 0)
                throw new PbfFormatException($"CCITT image has invalid width {width}.");
            if (bitOffset < 0 || bitOffset > (long)data.Length * 8)
                throw new ArgumentOutOfRangeException(nameof(bitOffset));

            _width = width;
            _bits = new MsbBitReader(data, bitOffset);
            _reference = new int[width + 2];
            _current = new int[width + 2];

            if (!reference.IsEmpty)
            {
                var count = Math.Min(reference.Length, _reference.Length);
                reference[..count].CopyTo(_reference);
                _referenceCount = count;
            }

            Row = startRow;
        }

        /// <summary>Row that <see cref="TryDecodeRow"/> will decode next.</summary>
        public int Row { get; set; }

        /// <summary>Bit position in the strip at the start of <see cref="Row"/>.</summary>
        public long BitOffset => _bits.Position;

        public int ReferenceCount => _referenceCount;

        /// <summary>Transitions of row <see cref="Row"/> - 1 (the reference line).</summary>
        public ReadOnlySpan<int> ReferenceLine => _reference.AsSpan(0, _referenceCount);

        public bool TryDecodeRow(out int[] transitions, out int count)
        {
            count = 0;

            if (_finished)
            {
                transitions = _current;
                return false;
            }

            try
            {
                DecodeRow(_bits, _width, _reference, _referenceCount, _current, ref count);
            }
            catch (EndOfStreamException)
            {
                _finished = true;
                transitions = _current;
                return false;
            }

            transitions = _current;
            (_reference, _current) = (_current, _reference);
            _referenceCount = count;
            Row++;
            return true;
        }
    }

    private static void DecodeRow(MsbBitReader bits, int width, int[] reference, int referenceCount,
                                  int[] current, ref int currentCount)
    {
        var a0 = -1;
        var color = 0; // 0 = white run, 1 = black run
        var guard = 0;

        while (a0 < width)
        {
            if (++guard > width * 4 + 1024)
                throw new PbfFormatException("CCITT row does not terminate (corrupt stream).");

            var mode = ReadMode(bits);
            if (mode == EndOfData)
                return;

            if (mode == PassMode)
            {
                var index = FindB1(reference, referenceCount, a0, color);
                a0 = index + 1 < referenceCount ? reference[index + 1] : width;
                continue;
            }

            if (mode == HorizontalMode)
            {
                var run1 = ReadRun(bits, color);
                var run2 = ReadRun(bits, 1 - color);

                var start = Math.Max(a0, 0);
                var a1 = (int)Math.Min((long)start + run1, width);
                var a2 = (int)Math.Min((long)a1 + run2, width);

                Append(current, ref currentCount, a1);
                Append(current, ref currentCount, a2);
                a0 = a2;
                continue;
            }

            // Vertical mode: V0 = 0, VR1..3 = +1..+3, VL1..3 = -1..-3.
            var b1Index = FindB1(reference, referenceCount, a0, color);
            var b1 = b1Index < referenceCount ? reference[b1Index] : width;
            var a1v = Math.Clamp(b1 + mode, 0, width);

            Append(current, ref currentCount, a1v);
            a0 = a1v;
            color = 1 - color;
        }
    }

    private static void Append(int[] transitions, ref int count, int position)
    {
        if (count > 0 && position <= transitions[count - 1])
            return;

        // Position 0 is legitimate: it means the row starts in ink.
        if (position < 0)
            return;

        transitions[count++] = position;
    }

    /// <summary>
    /// Index of b1: the first changing element right of a0 whose colour is the opposite of the
    /// current run colour. Transitions alternate starting with white→black at index 0, so the
    /// required parity equals the current colour.
    /// </summary>
    private static int FindB1(int[] reference, int count, int a0, int color)
    {
        var index = 0;
        while (index < count && reference[index] <= a0)
            index++;

        if ((index & 1) != color)
            index++;

        return index;
    }

    private static int ReadRun(MsbBitReader bits, int color)
    {
        var table = color == 0 ? WhiteCodes : BlackCodes;
        var total = 0;

        while (true)
        {
            var run = ReadCode(bits, table);
            total += run;

            // Make-up codes are multiples of 64 (and are followed by a terminating code);
            // 2560 is the largest make-up.
            if (run < 64)
                return total;

            if (total > 1_000_000)
                throw new PbfFormatException("CCITT run length is implausible (corrupt stream).");
        }
    }

    private static int ReadCode(MsbBitReader bits, Dictionary<int, int> table)
    {
        var value = 0;
        for (var length = 1; length <= 13; length++)
        {
            value = (value << 1) | bits.ReadBit();
            if (table.TryGetValue((length << 16) | value, out var run))
                return run;
        }

        throw new PbfFormatException("CCITT run-length code not found (corrupt stream).");
    }

    private static int ReadMode(MsbBitReader bits)
    {
        var value = 0;
        for (var length = 1; length <= 13; length++)
        {
            if (!bits.TryReadBit(out var bit))
                throw new EndOfStreamException();

            value = (value << 1) | bit;

            switch (length, value)
            {
                case (1, 0b1): return 0;                       // V0
                case (3, 0b011): return 1;                     // VR1
                case (3, 0b010): return -1;                    // VL1
                case (3, 0b001): return HorizontalMode;        // H
                case (4, 0b0001): return PassMode;             // P
                case (6, 0b000011): return 2;                  // VR2
                case (6, 0b000010): return -2;                 // VL2
                case (7, 0b0000011): return 3;                 // VR3
                case (7, 0b0000010): return -3;                // VL3
            }

            if (length == 12 && value == 0)
                return EndOfData; // twelve zeros: EOL / EOFB
        }

        return EndOfData;
    }

    private sealed class MsbBitReader
    {
        private readonly byte[] _data;
        private long _bitPosition;

        public MsbBitReader(byte[] data, long bitPosition = 0)
        {
            _data = data;
            _bitPosition = bitPosition;
        }

        public long Position => _bitPosition;

        private long TotalBits => (long)_data.Length * 8;

        public bool TryReadBit(out int bit)
        {
            if (_bitPosition >= TotalBits)
            {
                bit = 0;
                return false;
            }

            bit = (_data[(int)(_bitPosition >> 3)] >> (int)(7 - (_bitPosition & 7))) & 1;
            _bitPosition++;
            return true;
        }

        public int ReadBit() => TryReadBit(out var bit)
            ? bit
            : throw new EndOfStreamException();
    }
}
