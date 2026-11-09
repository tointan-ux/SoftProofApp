using SoftProof.RIPParsers.Raster;
using SoftProof.RIPParsers.Raster.Compression;
using SoftProof.RIPParsers.Raster.Tiff;
using Xunit;

namespace SoftProof.Tests.Raster;

/// <summary>
/// Hostile or corrupt headers must be rejected with a diagnostic instead of driving an
/// allocation. Each case here is a sub-kilobyte file that previously asked for gigabytes.
/// </summary>
public class ParserHardeningTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"softproof-hard-{Guid.NewGuid():N}.tif");

    // --- LZW -------------------------------------------------------------------------------

    /// <summary>
    /// Builds a valid LZW stream that expands quadratically: a literal followed by a chain of
    /// KwKwK codes, each emitting a string one byte longer than the previous one.
    /// </summary>
    private static byte[] ExpandingLzwStream(int codes)
    {
        var bits = new List<bool>();
        var codeWidth = 9;
        var nextCode = Lzw.FirstFreeCode;

        void Write(int code)
        {
            for (var b = codeWidth - 1; b >= 0; b--)
                bits.Add(((code >> b) & 1) != 0);
        }

        Write('A');
        var code = Lzw.FirstFreeCode;
        for (var i = 0; i < codes; i++)
        {
            Write(code);
            nextCode++;
            code++;
            // Mirrors the decoder's early-change rule so both stay in step.
            if (nextCode == (1 << codeWidth) - 1 && codeWidth < Lzw.MaxCodeWidth)
                codeWidth++;
        }

        while (bits.Count % 8 != 0)
            bits.Add(false);

        var bytes = new byte[bits.Count / 8];
        for (var i = 0; i < bits.Count; i++)
            if (bits[i])
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));

        return bytes;
    }

    [Fact]
    public void Lzw_ExpandingStream_IsBoundedByTheExpectedLength()
    {
        var bomb = ExpandingLzwStream(3800);

        // Far smaller than the ~7 MB this stream really decodes to.
        var decoded = Lzw.Decode(bomb, expectedLength: 4096);

        Assert.Equal(4096, decoded.Length);
    }

    [Fact]
    public void Lzw_WithoutExpectedLength_StaysUnderTheBlockCeiling()
    {
        var bomb = ExpandingLzwStream(3800);

        var decoded = Lzw.Decode(bomb);

        Assert.True(decoded.Length <= Lzw.MaxOutputBytes,
                    $"decoded {decoded.Length} bytes, over the {Lzw.MaxOutputBytes} ceiling");
        Assert.NotEmpty(decoded);
    }

    [Fact]
    public void Lzw_NormalStrip_StillRoundTrips()
    {
        // Guards against the cap breaking ordinary decoding.
        var payload = new byte[8192];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i * 7);

        var encoded = Lzw.Encode(payload);
        var decoded = Lzw.Decode(encoded, payload.Length);

        Assert.Equal(payload, decoded);
    }

    // --- tiles -----------------------------------------------------------------------------

    [Fact]
    public void Read_TileLargerThanTheBlockBudget_ThrowsInsteadOfAllocating()
    {
        // 1 x 1 image, one tile declared as 1,000,000 x 17,000: 2.1 GB if it were honoured.
        var path = TempFile();
        TiffBuilder.WriteTiled(path, width: 1, height: 1, tileWidth: 1_000_000, tileLength: 17_000);

        try
        {
            var ex = Assert.Throws<PbfFormatException>(() => TiffReader.Read(path));
            Assert.Contains("limit", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_OverfullIfd_ThrowsInsteadOfRetainingTags()
    {
        var path = TempFile();
        TiffBuilder.WriteOverfullIfd(path, entryCount: 5000);

        try
        {
            var ex = Assert.Throws<PbfFormatException>(() => TiffReader.GetInfo(path));
            Assert.Contains("IFD", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- dimensions ------------------------------------------------------------------------

    [Fact]
    public void Read_OversizedWidth_ThrowsBeforeAnyAllocation()
    {
        var path = TempFile();
        TiffBuilder.WriteClassic(path, width: 2_000_000, height: 1, photometric: 1,
                                 rowsPerStrip: 1, ink: (_, _) => false, writePixels: false);

        try
        {
            var ex = Assert.Throws<PbfFormatException>(() => TiffReader.Read(path));
            Assert.Contains("limit", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void StreamingCoverage_OversizedWidth_ThrowsBeforeAnyAllocation()
    {
        // The production path for multi-gigapixel plates (PlateLoader uses this overload).
        var path = TempFile();
        TiffBuilder.WriteClassic(path, width: 2_000_000, height: 1, photometric: 1,
                                 rowsPerStrip: 1, ink: (_, _) => false, writePixels: false);

        try
        {
            Assert.Throws<PbfFormatException>(() =>
                TiffReader.ReadBilevelCoverage(path, 0, 1_000_000,
                                               out _, out _, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
