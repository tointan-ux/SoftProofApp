using System.Buffers;

using SoftProof.RIPParsers.Raster;

namespace SoftProof.RIPParsers.Raster.Compression;

/// <summary>
/// PackBits (TIFF compression 32773 / Apple RLE) codec.
/// Used by Harlequin page buffers and by TIFF strips written by prepress workflows.
/// </summary>
public static class PackBits
{
    /// <summary>
    /// Decodes PackBits data. When <paramref name="expectedLength"/> is greater than zero the
    /// output is padded with 0x00 (TIFF decoders must tolerate short strips) or truncated.
    /// </summary>
    /// <exception cref="PbfFormatException">A run header points past the end of the input.</exception>
    public static byte[] Decode(ReadOnlySpan<byte> source, int expectedLength = 0)
    {
        var output = new List<byte>(expectedLength > 0 ? expectedLength : source.Length * 2);
        var i = 0;

        while (i < source.Length)
        {
            var header = (sbyte)source[i++];

            if (header >= 0)
            {
                // Literal run: header + 1 bytes follow.
                var count = header + 1;
                if (i + count > source.Length)
                    throw new PbfFormatException(
                        $"PackBits literal run of {count} bytes at offset {i - 1} overruns the stream " +
                        $"({source.Length - i} bytes left).");

                for (var n = 0; n < count; n++)
                    output.Add(source[i + n]);
                i += count;
            }
            else if (header != -128)
            {
                // Repeated run: 1 - header copies of the next byte.
                var count = 1 - header;
                if (i >= source.Length)
                    throw new PbfFormatException(
                        $"PackBits repeat run at offset {i - 1} is missing its data byte.");

                var value = source[i++];
                for (var n = 0; n < count; n++)
                    output.Add(value);
            }
            // -128 is a no-op.
        }

        var result = output.ToArray();
        return Fit(result, expectedLength);
    }

    /// <summary>Encodes PackBits. Runs of 3+ identical bytes are collapsed; everything else is literal.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> source)
    {
        var output = new List<byte>(source.Length + source.Length / 64 + 2);
        var i = 0;

        while (i < source.Length)
        {
            // Measure the run at i.
            var runLength = 1;
            while (i + runLength < source.Length && runLength < 128 && source[i + runLength] == source[i])
                runLength++;

            if (runLength >= 3)
            {
                output.Add(unchecked((byte)(1 - runLength))); // 257 - runLength as signed
                output.Add(source[i]);
                i += runLength;
            }
            else
            {
                // Gather literals until a run of 3+ starts (or 128 bytes are collected).
                var start = i;
                var length = 0;
                while (i < source.Length && length < 128)
                {
                    if (i + 2 < source.Length && source[i] == source[i + 1] && source[i] == source[i + 2])
                        break;
                    i++;
                    length++;
                }

                if (length == 0) // defensive: cannot happen because runLength < 3
                {
                    output.Add(0);
                    output.Add(source[start]);
                    i = start + 1;
                    continue;
                }

                output.Add((byte)(length - 1));
                for (var n = 0; n < length; n++)
                    output.Add(source[start + n]);
            }
        }

        return output.ToArray();
    }

    internal static byte[] Fit(byte[] data, int expectedLength)
    {
        if (expectedLength <= 0 || data.Length == expectedLength)
            return data;

        if (data.Length > expectedLength)
            return data.AsSpan(0, expectedLength).ToArray();

        var padded = new byte[expectedLength];
        data.CopyTo(padded, 0);
        return padded;
    }
}
