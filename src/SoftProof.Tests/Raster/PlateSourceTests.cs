using SoftProof.RIPParsers.Raster;
using SoftProof.RIPParsers.Raster.Tiff;
using Xunit;

namespace SoftProof.Tests.Raster;

/// <summary>
/// IPlateSource contract: rows are packed 1 bpp, MSB first, 1 = ink; opening reads only the IFD;
/// strip files support random access; truncated files (RIP still writing) report IsComplete = false.
/// </summary>
public class PlateSourceTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static async Task AssertMatchesOracle(string fixture)
    {
        var oracle = TiffReader.Read(Fixture(fixture));
        var coverage = oracle.InkCoverage();

        using var source = TiffStripSource.Open(Fixture(fixture));
        Assert.Equal(oracle.Width, source.Info.Width);
        Assert.Equal(oracle.Height, source.Info.Height);

        var rowBytes = source.Info.RowBytes;
        var packed = new byte[rowBytes * source.Info.Height];

        // Deliberately odd chunk sizes to exercise strip boundaries.
        var y = 0;
        while (y < source.Info.Height)
        {
            var n = Math.Min(7, source.Info.Height - y);
            await source.ReadRowsAsync(y, n, packed.AsMemory(y * rowBytes, n * rowBytes));
            y += n;
        }

        for (var py = 0; py < source.Info.Height; py++)
            for (var px = 0; px < source.Info.Width; px++)
            {
                var bit = (packed[py * rowBytes + (px >> 3)] >> (7 - (px & 7))) & 1;
                var expectedInk = coverage[py * oracle.Width + px] > 0;
                Assert.True(bit == 1 == expectedInk, $"{fixture}: pixel ({px},{py})");
            }
    }

    [Fact]
    public Task G4_SingleStrip_MatchesFullDecode() => AssertMatchesOracle("bilevel_g4.tif");

    [Fact]
    public Task Lzw_MatchesFullDecode() => AssertMatchesOracle("bilevel_lzw.tif");

    [Fact]
    public Task Uncompressed_BlackIsZero_MatchesFullDecode() => AssertMatchesOracle("bilevel_blackiszero.tif");

    [Fact]
    public Task Uncompressed_WhiteIsZero_MatchesFullDecode() => AssertMatchesOracle("bilevel_miniswhite.tif");

    [Fact]
    public void Open_ReadsMetadataWithoutDecoding()
    {
        using var source = TiffStripSource.Open(Fixture("bilevel_g4.tif"));

        Assert.Equal(128, source.Info.Width);
        Assert.Equal(64, source.Info.Height);
        Assert.Equal(4, source.Info.Compression);
        Assert.True(source.IsComplete);
        Assert.False(string.IsNullOrEmpty(source.Info.FilePath));
    }

    [Fact]
    public async Task MultiStrip_SupportsReverseRandomAccess()
    {
        // 13x10, 3 strips of 4 rows, uncompressed. Pattern depends on y so order bugs show up.
        var path = Path.Combine(Path.GetTempPath(), $"sp_multi_{Guid.NewGuid():N}.tif");
        try
        {
            TiffBuilder.WriteClassic(path, 13, 10, photometric: 0, rowsPerStrip: 4,
                                    ink: (x, y) => ((x + y) % 5) == 0 || y == 9);

            using var source = TiffStripSource.Open(path);
            Assert.Equal(3, source.Info.StripCount);
            Assert.True(source.IsComplete);

            var rowBytes = source.Info.RowBytes;
            var expected = TiffBuilder.Render(13, 10, (x, y) => ((x + y) % 5) == 0 || y == 9);
            var packed = new byte[rowBytes * 10];

            // Read the last strip first, then the middle, then the first: no sequential assumption.
            foreach (var (start, count) in new[] { (8, 2), (4, 4), (0, 4) })
            {
                await source.ReadRowsAsync(start, count, packed.AsMemory(start * rowBytes, count * rowBytes));
            }

            Assert.Equal(expected, packed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BigTiff_Uncompressed_IsParsedAndRead()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sp_big_{Guid.NewGuid():N}.tif");
        try
        {
            TiffBuilder.WriteBigTiff(path, 17, 5, photometric: 0, ink: (x, y) => (x % 3) == (y % 3));

            using var source = TiffStripSource.Open(path);
            Assert.True(source.Info.IsBigTiff);
            Assert.Equal(17, source.Info.Width);
            Assert.Equal(5, source.Info.Height);

            var rowBytes = source.Info.RowBytes;
            var packed = new byte[rowBytes * 5];
            await source.ReadRowsAsync(0, 5, packed);
            Assert.Equal(TiffBuilder.Render(17, 5, (x, y) => (x % 3) == (y % 3)), packed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TruncatedFile_ReportsIncomplete_AndFailsLoudlyOnRead()
    {
        var copy = Path.Combine(Path.GetTempPath(), $"sp_trunc_{Guid.NewGuid():N}.tif");
        try
        {
            // Write a synthetic plate, then chop bytes off the end so the IFD survives but the
            // strip data does not — exactly what a RIP caught mid-write looks like.
            TiffBuilder.WriteClassic(copy, 64, 32, photometric: 0, rowsPerStrip: 16,
                                    ink: (x, y) => ((x / 4 + y / 4) % 2) == 0);
            var length = new FileInfo(copy).Length;
            using (var fs = new FileStream(copy, FileMode.Open, FileAccess.Write))
                fs.SetLength(length - 40);

            using var source = TiffStripSource.Open(copy);
            Assert.False(source.IsComplete);

            await Assert.ThrowsAsync<PlateSourceException>((Func<Task>)(async () =>
            {
                var buffer = new byte[source.Info.RowBytes * source.Info.Height];
                await source.ReadRowsAsync(0, source.Info.Height, buffer);
            }));
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void ImplausibleDimensions_AreRejectedBeforeAllocating()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sp_huge_{Guid.NewGuid():N}.tif");
        try
        {
            // 900k x 900k = 810 Gpx: a corrupt header, not a plate.
            TiffBuilder.WriteClassic(path, 1_000_001, 1, photometric: 0, rowsPerStrip: 1,
                                    ink: (_, _) => false, writePixels: false);
            Assert.Throws<PlateSourceException>(() => TiffStripSource.Open(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ShortDestination_IsRejected()
    {
        using var source = TiffStripSource.Open(Fixture("bilevel_lzw.tif"));
        await Assert.ThrowsAsync<ArgumentException>((Func<Task>)(async () =>
            await source.ReadRowsAsync(0, 4, new byte[4])));
    }

    [Theory]
    [InlineData("Mayles Glowskin (Cutting)00.tif", "Cutting")]
    [InlineData("job_background_C.tif", "Cyan")]
    [InlineData("job.background.Magenta.tif", "Magenta")]
    [InlineData("plate-(K)01.tif", "Black")]
    [InlineData("plain-job-name.tif", null)]
    public void SeparationHint_ParsesCommonRipConventions(string file, string? expected)
    {
        var parsed = SeparationNaming.Parse(file);
        if (expected is null)
            Assert.True(parsed is null || parsed.Length > 2);
        else
            Assert.Equal(expected, parsed);
    }
}

/// <summary>Minimal TIFF writer used only to synthesise test plates.</summary>
internal static class TiffBuilder
{
    public static byte[] Render(int width, int height, Func<int, int, bool> ink)
    {
        var rowBytes = (width + 7) / 8;
        var buffer = new byte[rowBytes * height];

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (ink(x, y))
                    buffer[y * rowBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));

        return buffer;
    }

    public static void WriteClassic(string path, int width, int height, int photometric,
                                    int rowsPerStrip, Func<int, int, bool> ink,
                                    bool writePixels = true)
    {
        var rowBytes = (width + 7) / 8;
        var strips = (height + rowsPerStrip - 1) / rowsPerStrip;
        var stripBytes = rowBytes * rowsPerStrip;

        var entries = new List<(ushort Tag, ushort Type, uint Count, uint Value)>
        {
            (256, 4, 1, (uint)width),
            (257, 4, 1, (uint)height),
            (258, 3, 1, 1),
            (259, 3, 1, 1),
            (262, 3, 1, (uint)photometric),
            (266, 3, 1, 1),
            (277, 3, 1, 1),
            (278, 4, 1, (uint)rowsPerStrip),
            (284, 3, 1, 1)
        };

        var ifdStart = 8;
        var entryCount = entries.Count + 2; // + StripOffsets, StripByteCounts
        var ifdEnd = ifdStart + 2 + entryCount * 12 + 4;
        var offsetsArrayAt = ifdEnd;
        var countsArrayAt = offsetsArrayAt + strips * 4;
        var dataStart = countsArrayAt + strips * 4;

        var offsets = new uint[strips];
        var counts = new uint[strips];
        for (var i = 0; i < strips; i++)
        {
            offsets[i] = (uint)(dataStart + i * stripBytes);
            counts[i] = (uint)(stripBytes);
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        writer.Write((byte)'I'); writer.Write((byte)'I');
        writer.Write((ushort)42);
        writer.Write((uint)ifdStart);

        writer.Write((ushort)entryCount);

        foreach (var (tag, type, count, value) in entries)
            WriteEntry(writer, tag, type, count, value);

        WriteEntry(writer, 273, 4, (uint)strips, strips == 1 ? offsets[0] : (uint)offsetsArrayAt);
        WriteEntry(writer, 279, 4, (uint)strips, strips == 1 ? counts[0] : (uint)countsArrayAt);

        writer.Write(0u); // next IFD

        if (strips > 1)
        {
            foreach (var value in offsets) writer.Write(value);
            foreach (var value in counts) writer.Write(value);
        }

        if (writePixels)
        {
            for (var s = 0; s < strips; s++)
            {
                var rows = Math.Min(rowsPerStrip, height - s * rowsPerStrip);
                var block = new byte[stripBytes];
                for (var r = 0; r < rows; r++)
                {
                    var y = s * rowsPerStrip + r;
                    for (var x = 0; x < width; x++)
                        if (ink(x, y))
                            block[r * rowBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }

                writer.Write(block);
            }
        }
    }

    private static void WriteEntry(BinaryWriter writer, ushort tag, ushort type, uint count, uint value)
    {
        writer.Write(tag);
        writer.Write(type);
        writer.Write(count);
        writer.Write(value);
    }

    public static void WriteBigTiff(string path, int width, int height, int photometric,
                                    Func<int, int, bool> ink)
    {
        var rowBytes = (width + 7) / 8;
        var pixels = Render(width, height, ink);

        // 16-byte header + uint64 count + 9 entries * 20 + uint64 next pointer.
        const long ifdStart = 16;
        var entryCount = 9;
        var ifdEnd = ifdStart + 8 + entryCount * 20 + 8;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        writer.Write((byte)'I'); writer.Write((byte)'I');
        writer.Write((ushort)43);   // BigTIFF
        writer.Write((ushort)8);    // offset size
        writer.Write((ushort)0);    // reserved
        writer.Write((ulong)ifdStart);

        writer.Write((ulong)entryCount);

        void Entry(ushort tag, ushort type, ulong count, ulong value)
        {
            writer.Write(tag);
            writer.Write(type);
            writer.Write(count);
            writer.Write(value);
        }

        Entry(256, 4, 1, (ulong)width);
        Entry(257, 4, 1, (ulong)height);
        Entry(258, 3, 1, 1);
        Entry(259, 3, 1, 1);
        Entry(262, 3, 1, (ulong)photometric);
        Entry(273, 16, 1, (ulong)ifdEnd);          // LONG8 strip offset
        Entry(277, 3, 1, 1);
        Entry(278, 4, 1, (ulong)height);
        Entry(279, 16, 1, (ulong)pixels.Length);   // LONG8 byte count

        writer.Write(0ul); // next IFD

        if (stream.Position != ifdEnd)
            stream.Position = ifdEnd;

        writer.Write(pixels);
    }
}
