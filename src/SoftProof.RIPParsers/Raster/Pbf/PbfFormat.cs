namespace SoftProof.RIPParsers.Raster.Pbf;

/// <summary>Payload compression used by a page-buffer separation.</summary>
public enum PbfCompression : ushort
{
    None = 0,
    PackBits = 1,
    Lzw = 2,
    Deflate = 3
}

[Flags]
public enum PbfFlags : ushort
{
    None = 0,
    HasDirectory = 1 << 0,
    /// <summary>Bits inside each byte are stored least-significant-bit first (TIFF FillOrder 2).</summary>
    LsbFirst = 1 << 1,
    /// <summary>Payload bytes are already inverted (0 = ink) for every separation.</summary>
    Inverted = 1 << 2
}

/// <summary>
/// Page-buffer header.
/// <para>
/// Harlequin's on-disk page buffer is proprietary and undocumented, so SoftProof defines a
/// versioned profile it can validate strictly and fail loudly on, rather than silently showing
/// an empty page. Version 2 is big-endian with an explicit separation directory (offsets,
/// compressed sizes, per-plate compression); version 1 is the legacy little-endian, header +
/// name table + sequential raw planes layout this application previously assumed.
/// </para>
/// </summary>
public sealed record PbfHeader(
    int Version,
    int Width,
    int Height,
    int Resolution,
    int BitDepth,
    int SeparationCount,
    int ColorSpace,
    PbfFlags Flags,
    long DirectoryOffset,
    long DirectoryLength,
    IReadOnlyList<string> Names)
{
    public long ExpectedPlateBytes(int rowBytes) => (long)rowBytes * Height;
}

/// <summary>One separation's payload location and encoding inside a page buffer.</summary>
public sealed class PbfSeparation
{
    public string Name { get; init; } = string.Empty;
    public int Index { get; init; }
    public long DataOffset { get; init; }
    public int CompressedSize { get; init; }
    public int UncompressedSize { get; init; }
    public PbfCompression Compression { get; init; }
    public int RowBytes { get; init; }
    public int BitDepth { get; init; }
    /// <summary>True when a set bit means ink (TIFF WhiteIsZero style). False for 0 = ink.</summary>
    public bool OneIsInk { get; init; } = true;

    public static int ComputeRowBytes(int width, int bitDepth) =>
        bitDepth == 1 ? (width + 7) / 8 : width * (bitDepth / 8);
}
