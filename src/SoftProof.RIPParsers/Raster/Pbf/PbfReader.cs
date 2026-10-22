using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SoftProof.RIPParsers.Raster.Compression;

namespace SoftProof.RIPParsers.Raster.Pbf;

/// <summary>
/// Reads SoftProof page-buffer profiles (see <see cref="PbfHeader"/>): header, separation
/// directory with per-plate offsets, compressed payloads and 1-bit/8-bit unpacking.
/// </summary>
public static class PbfReader
{
    public const int HeaderV2Size = 36;
    public const int DirectoryEntrySize = 24;
    private static readonly byte[] MagicV2 = { (byte)'H', (byte)'Q', (byte)'N', 0x1A };

    /// <summary>True when the first bytes look like a page buffer this reader understands.</summary>
    public static bool HasPbfSignature(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 4 && head[..4].SequenceEqual(MagicV2))
            return true;

        return head.Length >= 3
               && head[0] == (byte)'H' && head[1] == (byte)'Q' && head[2] == (byte)'N';
    }

    public static PbfHeader ReadHeader(string path)
    {
        using var fs = OpenRead(path);
        return ReadHeader(fs);
    }

    public static PbfHeader ReadHeader(Stream stream)
    {
        var head = new byte[Math.Min(HeaderV2Size, checked((int)Math.Max(0, stream.Length)))];
        stream.Position = 0;
        stream.ReadExactly(head);
        return ParseHeader(stream, head);
    }

    /// <summary>Header plus one directory entry per separation, in file order.</summary>
    public static (PbfHeader Header, IReadOnlyList<PbfSeparation> Separations) ReadDocument(string path)
    {
        using var fs = OpenRead(path);
        var head = new byte[Math.Min(HeaderV2Size, checked((int)Math.Max(0, fs.Length)))];
        fs.Position = 0;
        fs.ReadExactly(head);
        var header = ParseHeader(fs, head);
        var separations = ReadDirectory(fs, header);
        return (header, separations);
    }

    /// <summary>Reads and decodes one separation payload into 8-bit ink coverage (width * height).</summary>
    public static byte[] ReadPlate(string path, PbfHeader header, PbfSeparation separation)
    {
        using var fs = OpenRead(path);
        return ReadPlate(fs, header, separation);
    }

    public static byte[] ReadPlate(Stream stream, PbfHeader header, PbfSeparation separation)
    {
        if (separation.CompressedSize < 0 || separation.DataOffset < 0)
            throw new PbfFormatException($"Separation '{separation.Name}' has a negative payload range.");

        if (separation.DataOffset + separation.CompressedSize > stream.Length)
            throw new PbfFormatException(
                $"Separation '{separation.Name}' payload (offset {separation.DataOffset}, " +
                $"{separation.CompressedSize} bytes) runs past the end of the {stream.Length}-byte file.");

        var compressed = new byte[separation.CompressedSize];
        stream.Position = separation.DataOffset;
        stream.ReadExactly(compressed);

        var rowBytes = separation.RowBytes > 0
            ? separation.RowBytes
            : PbfSeparation.ComputeRowBytes(header.Width, separation.BitDepth);
        var expected = (int)((long)rowBytes * header.Height);

        var raw = separation.Compression switch
        {
            PbfCompression.None => compressed,
            PbfCompression.PackBits => PackBits.Decode(compressed, expected),
            PbfCompression.Lzw => Lzw.Decode(compressed, expected),
            PbfCompression.Deflate => Inflate(compressed, expected),
            _ => throw new PbfFormatException(
                $"Separation '{separation.Name}' uses unknown compression " +
                $"{(ushort)separation.Compression}.")
        };

        if (raw.Length < expected)
            Array.Resize(ref raw, expected); // zero-padded short strip, as TIFF decoders do

        return Unpack(raw, header.Width, header.Height, rowBytes, separation.BitDepth,
                      separation.OneIsInk, header.Flags.HasFlag(PbfFlags.LsbFirst));
    }

    /// <summary>Expands packed samples into one coverage byte per pixel.</summary>
    public static byte[] Unpack(ReadOnlySpan<byte> raw, int width, int height, int rowBytes,
                                int bitDepth, bool oneIsInk, bool lsbFirst)
    {
        var coverage = new byte[(long)width * height];
        var bytesPerSample = bitDepth / 8;

        if (bitDepth == 1)
        {
            for (var y = 0; y < height; y++)
            {
                var row = (long)y * rowBytes;
                var target = (long)y * width;
                for (var x = 0; x < width; x++)
                {
                    var index = row + (x >> 3);
                    if (index >= raw.Length) break;

                    var bit = lsbFirst
                        ? (raw[(int)index] >> (x & 7)) & 1
                        : (raw[(int)index] >> (7 - (x & 7))) & 1;

                    var ink = oneIsInk ? bit == 1 : bit == 0;
                    coverage[target + x] = ink ? (byte)255 : (byte)0;
                }
            }
            return coverage;
        }

        if (bytesPerSample is < 1 or > 2)
            throw new PbfFormatException($"Unsupported page-buffer bit depth {bitDepth}.");

        for (var y = 0; y < height; y++)
        {
            var row = (long)y * rowBytes;
            var target = (long)y * width;
            for (var x = 0; x < width; x++)
            {
                var index = row + (long)x * bytesPerSample;
                if (index + bytesPerSample > raw.Length) break;

                var value = bytesPerSample == 2
                    ? BinaryPrimitives.ReadUInt16BigEndian(raw.Slice((int)index, 2))
                    : raw[(int)index];

                if (bytesPerSample == 2)
                    value >>= 8; // 16-bit plate → high byte

                coverage[target + x] = oneIsInk ? (byte)value : (byte)(255 - value);
            }
        }

        return coverage;
    }

    private static PbfHeader ParseHeader(Stream stream, byte[] head)
    {
        if (!HasPbfSignature(head))
            throw new PbfFormatException(
                "Not a Harlequin page buffer: expected magic 'HQN'. " +
                $"First bytes: {Hex(head, 16)}");

        var isV2 = head.Length >= 4 && head[..4].SequenceEqual(MagicV2);

        return isV2 ? ParseV2(stream, head) : ParseV1(stream, head);
    }

    private static PbfHeader ParseV2(Stream stream, byte[] head)
    {
        var version = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
        var width = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(8));
        var height = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(12));
        var resolution = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(16));
        var bitDepth = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(20));
        var separationCount = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(22));
        var colorSpace = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(24));
        var flags = (PbfFlags)BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(26));
        var directoryOffset = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(28));
        var directoryLength = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(32));

        ValidateGeometry(width, height, separationCount, stream.Length);

        var names = ReadNames(stream, HeaderV2Size, separationCount);

        if (directoryOffset > 0)
        {
            if (directoryOffset + directoryLength > stream.Length)
                throw new PbfFormatException(
                    $"Page-buffer directory (offset {directoryOffset}, {directoryLength} bytes) " +
                    "runs past the end of the file.");
            stream.Position = directoryOffset;
        }

        return new PbfHeader(version, width, height, resolution <= 0 ? 2400 : resolution,
                             bitDepth <= 0 ? 1 : bitDepth, separationCount, colorSpace, flags,
                             directoryOffset, directoryLength, names);
    }

    private static PbfHeader ParseV1(Stream stream, byte[] head)
    {
        // Legacy layout: little-endian, exactly what the original parser assumed.
        var head16 = new byte[16];
        stream.Position = 0;
        var read = stream.Read(head16, 0, 16);
        if (read < 16)
            throw new PbfFormatException("Page-buffer file is shorter than its 16-byte header.");

        var version = (int)BinaryPrimitives.ReadUInt32LittleEndian(head16.AsSpan(4));
        var width = BinaryPrimitives.ReadInt32LittleEndian(head16.AsSpan(8));
        var height = BinaryPrimitives.ReadInt32LittleEndian(head16.AsSpan(12));

        var tail = new byte[6];
        stream.ReadExactly(tail);
        var bitDepth = BinaryPrimitives.ReadInt16LittleEndian(tail.AsSpan(0));
        var separationCount = BinaryPrimitives.ReadInt16LittleEndian(tail.AsSpan(2));
        var colorSpace = BinaryPrimitives.ReadInt16LittleEndian(tail.AsSpan(4));

        if (bitDepth is not (1 or 8 or 16))
            bitDepth = 1;

        ValidateGeometry(width, height, separationCount, stream.Length);

        var names = ReadNames(stream, 22, separationCount, littleEndian: true);

        return new PbfHeader(version <= 0 ? 1 : version, width, height, 2400, bitDepth,
                             separationCount, colorSpace, PbfFlags.None, 0, 0, names);
    }

    private static void ValidateGeometry(int width, int height, int separationCount, long fileLength)
    {
        if (width <= 0 || height <= 0)
            throw new PbfFormatException($"Page buffer has invalid page size {width}x{height}.");

        if ((long)width * height > 4_000_000_000L)
            throw new PbfFormatException(
                $"Page buffer page size {width}x{height} is implausible for a RIP plate.");

        if (separationCount <= 0 || separationCount > 64)
            throw new PbfFormatException(
                $"Page buffer declares {separationCount} separations (expected 1..64).");

        if (fileLength < HeaderV2Size)
            throw new PbfFormatException("Page-buffer file is truncated inside its header.");
    }

    private static List<string> ReadNames(Stream stream, long offset, int count, bool littleEndian = false)
    {
        var names = new List<string>(count);
        stream.Position = offset;
        var lengthBuffer = new byte[2];

        for (var i = 0; i < count; i++)
        {
            stream.ReadExactly(lengthBuffer);
            var length = littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(lengthBuffer)
                : BinaryPrimitives.ReadUInt16BigEndian(lengthBuffer);
            if (length > 4096)
                throw new PbfFormatException($"Separation name {i} is {length} bytes long - corrupt header.");

            var nameBytes = new byte[length];
            stream.ReadExactly(nameBytes);
            names.Add(Encoding.UTF8.GetString(nameBytes));
        }

        return names;
    }

    private static IReadOnlyList<PbfSeparation> ReadDirectory(Stream stream, PbfHeader header)
    {
        var result = new List<PbfSeparation>(header.SeparationCount);
        var entry = new byte[DirectoryEntrySize];

        if (header.Version >= 2 && header.DirectoryOffset > 0)
        {
            stream.Position = header.DirectoryOffset;
            for (var i = 0; i < header.SeparationCount; i++)
            {
                stream.ReadExactly(entry);
                result.Add(new PbfSeparation
                {
                    Name = i < header.Names.Count ? header.Names[i] : $"Separation_{i + 1}",
                    Index = i,
                    DataOffset = BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(0)),
                    CompressedSize = (int)BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(4)),
                    UncompressedSize = (int)BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(8)),
                    Compression = (PbfCompression)BinaryPrimitives.ReadUInt16BigEndian(entry.AsSpan(12)),
                    RowBytes = BinaryPrimitives.ReadUInt16BigEndian(entry.AsSpan(14)),
                    BitDepth = entry[16] == 0 ? header.BitDepth : entry[16],
                    OneIsInk = entry[17] != 0
                });
            }
            return result;
        }

        // Legacy sequential layout: raw planes packed back to back after the name table.
        var nameTableBytes = header.Names.Sum(n => 2 + Encoding.UTF8.GetByteCount(n));
        long offset = (header.Version >= 2 ? HeaderV2Size : 22) + nameTableBytes;
        var rowBytes = PbfSeparation.ComputeRowBytes(header.Width, header.BitDepth);
        var size = (int)((long)rowBytes * header.Height);
        var oneIsInk = !header.Flags.HasFlag(PbfFlags.Inverted);

        for (var i = 0; i < header.SeparationCount; i++)
        {
            result.Add(new PbfSeparation
            {
                Name = i < header.Names.Count ? header.Names[i] : $"Separation_{i + 1}",
                Index = i,
                DataOffset = offset,
                CompressedSize = size,
                UncompressedSize = size,
                Compression = PbfCompression.None,
                RowBytes = rowBytes,
                BitDepth = header.BitDepth,
                OneIsInk = oneIsInk
            });
            offset += size;
        }

        return result;
    }

    private static byte[] Inflate(byte[] compressed, int expected)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var output = new MemoryStream(expected > 0 ? expected : compressed.Length * 4);
        zlib.CopyTo(output);
        var data = output.ToArray();
        if (expected > 0 && data.Length < expected)
            Array.Resize(ref data, expected);
        return data;
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    public static string Hex(ReadOnlySpan<byte> bytes, int max)
    {
        var n = Math.Min(bytes.Length, max);
        var text = new StringBuilder(n * 3);
        for (var i = 0; i < n; i++)
            text.Append(bytes[i].ToString("X2")).Append(' ');
        return text.ToString().TrimEnd();
    }
}
