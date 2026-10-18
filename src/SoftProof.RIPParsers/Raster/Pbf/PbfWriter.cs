using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SoftProof.RIPParsers.Raster.Compression;

namespace SoftProof.RIPParsers.Raster.Pbf;

/// <summary>One plate to encode. Coverage is 8-bit ink (0 = paper, 255 = solid).</summary>
public sealed class PbfPlateInput
{
    public string Name { get; init; } = "Plate";
    public required byte[] Coverage { get; init; }
    public PbfCompression Compression { get; init; } = PbfCompression.None;
    /// <summary>1 packs to one bit per pixel (threshold 128); 8 stores one byte per pixel.</summary>
    public int BitDepth { get; init; } = 1;
    /// <summary>True encodes a set bit / non-zero byte as ink.</summary>
    public bool OneIsInk { get; init; } = true;
}

/// <summary>
/// Writes the SoftProof page-buffer profile v2 (big-endian header + explicit separation
/// directory). Used by the test suite and available to author sample/fixture jobs; it is not
/// a Harlequin RIP writer.
/// </summary>
public static class PbfWriter
{
    public static void Write(string path, int width, int height, int resolution, int colorSpace,
                             IReadOnlyList<PbfPlateInput> plates, PbfFlags flags = PbfFlags.HasDirectory)
    {
        if (plates.Count == 0)
            throw new ArgumentException("At least one plate is required.", nameof(plates));
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Page size must be positive.");

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Write(fs, width, height, resolution, colorSpace, plates, flags);
    }

    public static void Write(Stream stream, int width, int height, int resolution, int colorSpace,
                             IReadOnlyList<PbfPlateInput> plates, PbfFlags flags = PbfFlags.HasDirectory)
    {
        var names = plates.Select(p => p.Name).ToList();
        var nameBytes = names.Select(n => Encoding.UTF8.GetBytes(n)).ToList();
        var nameTableLength = nameBytes.Sum(b => 2 + b.Length);

        var directoryOffset = PbfReader.HeaderV2Size + nameTableLength;
        var directoryLength = plates.Count * PbfReader.DirectoryEntrySize;
        var dataStart = directoryOffset + directoryLength;

        // Encode payloads first so sizes are known before the header is written.
        var payloads = new List<(byte[] Bytes, int RowBytes, int BitDepth, bool OneIsInk)>(plates.Count);
        foreach (var plate in plates)
        {
            if (plate.Coverage.Length != (long)width * height)
                throw new ArgumentException(
                    $"Plate '{plate.Name}' has {plate.Coverage.Length} samples, expected {width}x{height}.");

            var bitDepth = plate.BitDepth == 1 ? 1 : 8;
            var rowBytes = PbfSeparation.ComputeRowBytes(width, bitDepth);
            var raw = bitDepth == 1
                ? PackOneBit(plate.Coverage, width, height, rowBytes, plate.OneIsInk, flags.HasFlag(PbfFlags.LsbFirst))
                : PackEightBit(plate.Coverage, width, height, plate.OneIsInk);

            var encoded = plate.Compression switch
            {
                PbfCompression.None => raw,
                PbfCompression.PackBits => PackBits.Encode(raw),
                PbfCompression.Lzw => Lzw.Encode(raw),
                PbfCompression.Deflate => Deflate(raw),
                _ => throw new ArgumentOutOfRangeException(nameof(plate),
                        $"Unknown compression {plate.Compression}.")
            };

            payloads.Add((encoded, rowBytes, bitDepth, plate.OneIsInk));
        }

        var header = new byte[PbfReader.HeaderV2Size];
        header[0] = (byte)'H';
        header[1] = (byte)'Q';
        header[2] = (byte)'N';
        header[3] = 0x1A;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12), height);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16), resolution);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(20), (ushort)payloads.Max(p => p.BitDepth));
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(22), (ushort)plates.Count);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(24), (ushort)colorSpace);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(26), (ushort)(flags | PbfFlags.HasDirectory));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28), (uint)directoryOffset);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(32), (uint)directoryLength);

        stream.Write(header);

        for (var i = 0; i < plates.Count; i++)
        {
            var name = nameBytes[i];
            var length = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)name.Length);
            stream.Write(length);
            stream.Write(name);
        }

        var offset = dataStart;
        var directory = new byte[directoryLength];
        for (var i = 0; i < plates.Count; i++)
        {
            var payload = payloads[i];
            var entry = directory.AsSpan(i * PbfReader.DirectoryEntrySize, PbfReader.DirectoryEntrySize);
            BinaryPrimitives.WriteUInt32BigEndian(entry[..4], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(4, 4), (uint)payload.Bytes.Length);
            BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(8, 4), (uint)((long)payload.RowBytes * height));
            BinaryPrimitives.WriteUInt16BigEndian(entry.Slice(12, 2), (ushort)plates[i].Compression);
            BinaryPrimitives.WriteUInt16BigEndian(entry.Slice(14, 2), (ushort)payload.RowBytes);
            entry[16] = (byte)payload.BitDepth;
            entry[17] = payload.OneIsInk ? (byte)1 : (byte)0;

            offset += payload.Bytes.Length;
        }

        stream.Write(directory);

        foreach (var payload in payloads)
            stream.Write(payload.Bytes);
    }

    private static byte[] PackOneBit(byte[] coverage, int width, int height, int rowBytes,
                                     bool oneIsInk, bool lsbFirst)
    {
        var packed = new byte[(long)rowBytes * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * rowBytes;
            var source = (long)y * width;
            for (var x = 0; x < width; x++)
            {
                var ink = coverage[source + x] >= 128;
                var bit = oneIsInk ? ink : !ink;
                if (!bit) continue;

                var index = row + (x >> 3);
                packed[index] |= lsbFirst
                    ? (byte)(1 << (x & 7))
                    : (byte)(1 << (7 - (x & 7)));
            }
        }
        return packed;
    }

    private static byte[] PackEightBit(byte[] coverage, int width, int height, bool oneIsInk)
    {
        var packed = new byte[(long)width * height];
        for (var i = 0; i < coverage.Length; i++)
            packed[i] = oneIsInk ? coverage[i] : (byte)(255 - coverage[i]);
        return packed;
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw);
        return output.ToArray();
    }
}
