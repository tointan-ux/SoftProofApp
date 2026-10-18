using SoftProof.RIPParsers.Raster;
using SoftProof.RIPParsers.Raster.Pbf;
using Xunit;

namespace SoftProof.Tests.Raster;

public class PbfReaderTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"softproof-pbf-{Guid.NewGuid():N}.pbuf");

    private static byte[] MakeCoverage(int width, int height, Func<int, int, byte> paint)
    {
        var data = new byte[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                data[y * width + x] = paint(x, y);
        return data;
    }

    [Theory]
    [InlineData(PbfCompression.None)]
    [InlineData(PbfCompression.PackBits)]
    [InlineData(PbfCompression.Lzw)]
    public void WriteThenRead_OneBitPlate_RoundTripsThroughCompression(PbfCompression compression)
    {
        const int width = 64, height = 24;
        // Bilevel artwork: threshold at 128 means only 0/255 survive the packing.
        var coverage = MakeCoverage(width, height,
            (x, y) => (x / 4 + y / 3) % 2 == 0 ? (byte)255 : (byte)0);

        var path = TempFile();
        try
        {
            PbfWriter.Write(path, width, height, 2400, 2, new[]
            {
                new PbfPlateInput { Name = "Cyan", Coverage = coverage, Compression = compression }
            });

            var (header, separations) = PbfReader.ReadDocument(path);
            Assert.Equal(width, header.Width);
            Assert.Equal(height, header.Height);
            Assert.Equal(2400, header.Resolution);
            Assert.Single(separations);
            Assert.Equal("Cyan", separations[0].Name);
            Assert.Equal(compression, separations[0].Compression);
            Assert.True(separations[0].DataOffset > 0);

            var decoded = PbfReader.ReadPlate(path, header, separations[0]);
            Assert.Equal(coverage, decoded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WriteThenRead_EightBitPlate_PreservesExactValues()
    {
        const int width = 37, height = 11;
        var coverage = MakeCoverage(width, height, (x, y) => (byte)((x * 5 + y * 13) % 256));

        var path = TempFile();
        try
        {
            PbfWriter.Write(path, width, height, 1200, 3, new[]
            {
                new PbfPlateInput
                {
                    Name = "Spot UV",
                    Coverage = coverage,
                    Compression = PbfCompression.PackBits,
                    BitDepth = 8
                }
            });

            var (header, separations) = PbfReader.ReadDocument(path);
            var decoded = PbfReader.ReadPlate(path, header, separations[0]);

            Assert.Equal(8, separations[0].BitDepth);
            Assert.Equal(coverage, decoded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WriteThenRead_MultiplePlates_UsesPerSeparationDirectoryOffsets()
    {
        const int width = 32, height = 8;
        var cyan = MakeCoverage(width, height, (x, y) => x < 16 ? (byte)255 : (byte)0);
        var magenta = MakeCoverage(width, height, (x, y) => y < 4 ? (byte)255 : (byte)0);
        var yellow = MakeCoverage(width, height, (x, y) => (x + y) % 2 == 0 ? (byte)255 : (byte)0);

        var path = TempFile();
        try
        {
            PbfWriter.Write(path, width, height, 2400, 2, new[]
            {
                new PbfPlateInput { Name = "Cyan", Coverage = cyan, Compression = PbfCompression.None },
                new PbfPlateInput { Name = "Magenta", Coverage = magenta, Compression = PbfCompression.PackBits },
                new PbfPlateInput { Name = "Yellow", Coverage = yellow, Compression = PbfCompression.Lzw }
            });

            var (header, separations) = PbfReader.ReadDocument(path);
            Assert.Equal(3, separations.Count);
            Assert.Equal(new[] { "Cyan", "Magenta", "Yellow" }, separations.Select(s => s.Name));

            // Directory offsets must be distinct and increasing.
            Assert.Equal(separations.Select(s => s.DataOffset).OrderBy(o => o).ToArray(),
                         separations.Select(s => s.DataOffset).ToArray());

            Assert.Equal(cyan, PbfReader.ReadPlate(path, header, separations[0]));
            Assert.Equal(magenta, PbfReader.ReadPlate(path, header, separations[1]));
            Assert.Equal(yellow, PbfReader.ReadPlate(path, header, separations[2]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadDocument_WrongMagic_ThrowsWithHexDiagnostic()
    {
        var path = TempFile();
        try
        {
            File.WriteAllBytes(path, Enumerable.Repeat((byte)0x5A, 64).ToArray());

            var ex = Assert.Throws<PbfFormatException>(() => PbfReader.ReadDocument(path));
            Assert.Contains("HQN", ex.Message);
            Assert.Contains("5A", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadPlate_PayloadPastEndOfFile_ThrowsInsteadOfBlanking()
    {
        const int width = 16, height = 8;
        var coverage = MakeCoverage(width, height, (_, _) => (byte)255);

        var path = TempFile();
        try
        {
            PbfWriter.Write(path, width, height, 2400, 2, new[]
            {
                new PbfPlateInput { Name = "Cyan", Coverage = coverage }
            });

            var (header, separations) = PbfReader.ReadDocument(path);
            var truncated = new PbfSeparation
            {
                Name = separations[0].Name,
                Index = 0,
                DataOffset = separations[0].DataOffset,
                CompressedSize = separations[0].CompressedSize + 10_000,
                UncompressedSize = separations[0].UncompressedSize,
                Compression = separations[0].Compression,
                RowBytes = separations[0].RowBytes,
                BitDepth = separations[0].BitDepth,
                OneIsInk = separations[0].OneIsInk
            };

            var ex = Assert.Throws<PbfFormatException>(() => PbfReader.ReadPlate(path, header, truncated));
            Assert.Contains("past the end", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Unpack_ZeroIsInk_InvertsPolarity()
    {
        // 0b1010_0000 with MSB-first bit order: pixels 0 and 2 are ink when 1 = ink.
        var raw = new byte[] { 0b1010_0000 };
        var oneIsInk = PbfReader.Unpack(raw, width: 4, height: 1, rowBytes: 1, bitDepth: 1,
                                        oneIsInk: true, lsbFirst: false);
        var zeroIsInk = PbfReader.Unpack(raw, width: 4, height: 1, rowBytes: 1, bitDepth: 1,
                                         oneIsInk: false, lsbFirst: false);

        Assert.Equal(new byte[] { 255, 0, 255, 0 }, oneIsInk);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, zeroIsInk);
    }
}
