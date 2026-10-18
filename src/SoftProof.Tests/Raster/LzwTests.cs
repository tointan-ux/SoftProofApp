using SoftProof.RIPParsers.Raster.Compression;
using Xunit;

namespace SoftProof.Tests.Raster;

public class LzwTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(255)]
    [InlineData(4096)]
    [InlineData(20000)]
    public void EncodeThenDecode_RoundTrips(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
            data[i] = (byte)((i * 31 + (i >> 3)) & 0xFF);

        var encoded = Lzw.Encode(data);
        var decoded = Lzw.Decode(encoded, expectedLength: length);

        Assert.Equal(data, decoded);
    }

    [Fact]
    public void EncodeThenDecode_RoundTripsHighlyRepetitiveData()
    {
        // Long runs force dictionary growth and the 9->10->11 bit width changes.
        var data = new byte[60000];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)(i < 20000 ? 0x00 : i < 40000 ? 0xFF : 0x5A);

        var decoded = Lzw.Decode(Lzw.Encode(data), expectedLength: data.Length);

        Assert.Equal(data, decoded);
    }

    [Fact]
    public void Decode_TruncatedStream_ReturnsDecodedPrefixWithoutThrowing()
    {
        var data = new byte[5000];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)(i % 251);

        var encoded = Lzw.Encode(data);
        var truncated = encoded.AsSpan(0, encoded.Length / 2).ToArray();

        // A truncated strip must not throw: TIFF decoders pad short strips.
        var decoded = Lzw.Decode(truncated, expectedLength: data.Length);
        Assert.Equal(data.Length, decoded.Length);
        Assert.Equal(data.AsSpan(0, 16).ToArray(), decoded.AsSpan(0, 16).ToArray());
    }
}
