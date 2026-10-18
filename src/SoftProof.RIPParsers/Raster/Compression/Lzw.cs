using System.Buffers;

using SoftProof.RIPParsers.Raster;

namespace SoftProof.RIPParsers.Raster.Compression;

/// <summary>
/// TIFF flavour LZW (compression 5): MSB-first bit packing, 9..12 bit codes,
/// Clear = 256, EOI = 257, "early change" table growth.
/// </summary>
public static class Lzw
{
    public const int ClearCode = 256;
    public const int EoiCode = 257;
    public const int FirstFreeCode = 258;
    public const int MaxCode = 4096;
    public const int MaxCodeWidth = 12;

    /// <summary>Decodes a TIFF LZW strip.</summary>
    /// <param name="expectedLength">Uncompressed strip length; output is padded/truncated to it (0 = as decoded).</param>
    public static byte[] Decode(ReadOnlySpan<byte> source, int expectedLength = 0)
    {
        var bits = new MsbBitReader(source);
        var output = new List<byte>(expectedLength > 0 ? expectedLength : source.Length * 3);

        // Dictionary of byte strings; index = code.
        var prefix = new int[MaxCode];
        var suffix = new byte[MaxCode];
        var firstByte = new byte[MaxCode];
        var length = new int[MaxCode];

        void ResetTable()
        {
            for (var i = 0; i < 256; i++)
            {
                prefix[i] = -1;
                suffix[i] = (byte)i;
                firstByte[i] = (byte)i;
                length[i] = 1;
            }
        }

        var codeWidth = 9;
        var nextCode = FirstFreeCode;
        var previous = -1;
        ResetTable();

        void Emit(int code)
        {
            var bytes = new byte[length[code]];
            var c = code;
            for (var i = bytes.Length - 1; i >= 0; i--)
            {
                bytes[i] = suffix[c];
                c = prefix[c];
            }
            output.AddRange(bytes);
        }

        while (true)
        {
            int code;
            try
            {
                code = bits.Read(codeWidth);
            }
            catch (EndOfStreamException)
            {
                break; // Some writers omit EOI at the end of a strip.
            }

            if (code == EoiCode)
                break;

            if (code == ClearCode)
            {
                codeWidth = 9;
                nextCode = FirstFreeCode;
                previous = -1;
                continue;
            }

            if (previous == -1)
            {
                if (code >= 256)
                    throw new PbfFormatException($"LZW stream starts with non-literal code {code}.");
                Emit(code);
                previous = code;
                continue;
            }

            int entryCode;
            var addedInKwKwK = false;

            if (code < nextCode)
            {
                entryCode = code;
            }
            else if (code == nextCode)
            {
                // KwKwK case: entry is previous string + its own first byte.
                entryCode = nextCode;
                if (nextCode >= MaxCode)
                    throw new PbfFormatException("LZW dictionary overflow before table reset.");

                prefix[entryCode] = previous;
                suffix[entryCode] = firstByte[previous];
                firstByte[entryCode] = firstByte[previous];
                length[entryCode] = length[previous] + 1;
                nextCode++;
                addedInKwKwK = true;
            }
            else
            {
                // A truncated strip can leave a partial code that decodes to a code ahead of
                // the table. libtiff stops and pads in that case rather than failing the page.
                break;
            }

            Emit(entryCode);

            // Add previous + first byte of entry. The KwKwK branch already inserted it above;
            // inserting again would desynchronise every following code (and the code width).
            if (!addedInKwKwK && nextCode < MaxCode)
            {
                prefix[nextCode] = previous;
                suffix[nextCode] = firstByte[entryCode];
                firstByte[nextCode] = firstByte[previous];
                length[nextCode] = length[previous] + 1;
                nextCode++;
            }

            previous = code;

            // Early change: grow the code width one code early, exactly like libtiff.
            if (nextCode == (1 << codeWidth) - 1 && codeWidth < MaxCodeWidth)
                codeWidth++;
        }

        return PackBits.Fit(output.ToArray(), expectedLength);
    }

    /// <summary>
    /// Encodes a TIFF LZW strip. Kept in the product assembly so the decoder can be
    /// round-trip tested and so tools can author fixture page buffers.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> source)
    {
        var writer = new MsbBitWriter(source.Length + 16);
        var table = new Dictionary<(int Prefix, byte Suffix), int>();

        var width = 9;
        var nextCode = FirstFreeCode;

        void ResetTable()
        {
            table.Clear();
            nextCode = FirstFreeCode;
            width = 9;
        }

        writer.Write(ClearCode, width);
        if (source.Length == 0)
        {
            writer.Write(EoiCode, width);
            return writer.ToArray();
        }

        var current = (int)source[0];
        for (var i = 1; i < source.Length; i++)
        {
            var next = source[i];
            if (table.TryGetValue((current, next), out var code))
            {
                current = code; // current now tracks a dictionary code
                continue;
            }

            writer.Write(current, width);
            table[(current, next)] = nextCode++;

            // The encoder's table is one entry ahead of the decoder's, so it widens at
            // 1 << width (512/1024/2048) while the decoder widens at (1 << width) - 1.
            // That is exactly what TIFF's "early change" means; using the decoder's rule
            // here shifts every subsequent code by one bit and desynchronises the stream.
            if (nextCode == (1 << width) && width < MaxCodeWidth)
                width++;

            if (nextCode >= MaxCode - 1)
            {
                writer.Write(ClearCode, width);
                ResetTable();
            }

            current = next;
        }

        writer.Write(current, width);
        writer.Write(EoiCode, width);
        return writer.ToArray();
    }

    private sealed class MsbBitReader
    {
        private readonly byte[] _data;
        private int _bitPosition;

        public MsbBitReader(ReadOnlySpan<byte> data) => _data = data.ToArray();

        public int Read(int width)
        {
            if (_bitPosition + width > _data.Length * 8)
                throw new EndOfStreamException();

            var value = 0;
            for (var i = 0; i < width; i++)
            {
                var bit = (_data[_bitPosition >> 3] >> (7 - (_bitPosition & 7))) & 1;
                value = (value << 1) | bit;
                _bitPosition++;
            }
            return value;
        }
    }

    private sealed class MsbBitWriter
    {
        private readonly List<byte> _bytes;
        private int _bitPosition;

        public MsbBitWriter(int capacity) => _bytes = new List<byte>(capacity);

        public void Write(int value, int width)
        {
            for (var i = width - 1; i >= 0; i--)
            {
                if ((_bitPosition & 7) == 0)
                    _bytes.Add(0);

                if (((value >> i) & 1) != 0)
                    _bytes[_bytes.Count - 1] |= (byte)(1 << (7 - (_bitPosition & 7)));

                _bitPosition++;
            }
        }

        public byte[] ToArray() => _bytes.ToArray();
    }
}
