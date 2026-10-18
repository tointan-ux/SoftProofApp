using SoftProof.RIPParsers.Raster;
using SoftProof.RIPParsers.Raster.Compression;
using Xunit;

namespace SoftProof.Tests.Raster;

public class PackBitsTests
{
    [Fact]
    public void Decode_RepeatedRun_ExpandsThreeCopies()
    {
        // 0xFE = -2 => 1 - (-2) = 3 copies of the following byte.
        var decoded = PackBits.Decode(new byte[] { 0xFE, 0xAA }, expectedLength: 3);
        Assert.Equal(new byte[] { 0xAA, 0xAA, 0xAA }, decoded);
    }

    [Fact]
    public void Decode_LiteralRun_CopiesHeaderPlusOneBytes()
    {
        var decoded = PackBits.Decode(new byte[] { 0x02, 0x10, 0x20, 0x30 }, expectedLength: 3);
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, decoded);
    }

    [Fact]
    public void Decode_NoOpByte_IsSkipped()
    {
        var decoded = PackBits.Decode(new byte[] { 0x80, 0x00, 0x07 }, expectedLength: 1);
        Assert.Equal(new byte[] { 0x07 }, decoded);
    }

    [Fact]
    public void Decode_TruncatedRepeatRun_Throws()
    {
        var ex = Assert.Throws<PbfFormatException>(() => PackBits.Decode(new byte[] { 0xFF }));
        Assert.Contains("repeat run", ex.Message);
    }

    [Fact]
    public void EncodeThenDecode_RoundTripsRunsAndLiterals()
    {
        var data = new byte[700];
        for (var i = 0; i < data.Length; i++)
        {
            // Long runs, short runs and isolated literals.
            data[i] = (byte)(i < 300 ? 0x40 : i % 7 == 0 ? i & 0xFF : i % 13);
        }

        var encoded = PackBits.Encode(data);
        var decoded = PackBits.Decode(encoded, expectedLength: data.Length);

        Assert.Equal(data, decoded);
        Assert.True(encoded.Length < data.Length, "PackBits should compress a mostly-run payload.");
    }

    [Fact]
    public void Decode_ShortInput_IsPaddedToExpectedLength()
    {
        var decoded = PackBits.Decode(new byte[] { 0x00, 0xAB }, expectedLength: 4);
        Assert.Equal(new byte[] { 0xAB, 0x00, 0x00, 0x00 }, decoded);
    }
}
