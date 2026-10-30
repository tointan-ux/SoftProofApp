using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using SoftProof.RIPParsers.Raster;
using SoftProof.RIPParsers.Raster.Pbf;
using Xunit;

namespace SoftProof.Tests.Raster;

/// <summary>
/// Hostile page-buffer headers. The reader validates geometry, but these cases slipped through:
/// a wrapping offset+length sum, a name table that stops mid-entry, and a sequential layout that
/// claims more payload than the file contains.
/// </summary>
public class PbfMalformedStreamTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"softproof-pbf-bad-{Guid.NewGuid():N}.pbuf");

    private static byte[] Header(int version, int width, int height, int separations,
                                uint directoryOffset = 0, uint directoryLength = 0)
    {
        var head = new byte[PbfReader.HeaderV2Size];
        head[0] = (byte)'H';
        head[1] = (byte)'Q';
        head[2] = (byte)'N';
        head[3] = 0x1A;

        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4), (uint)version);
        BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(8), width);
        BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(12), height);
        BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(16), 2400);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(22), (ushort)separations);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(24), 0);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(26), 0);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(28), directoryOffset);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(32), directoryLength);

        return head;
    }

    [Fact]
    public void A_directory_offset_that_wraps_past_the_end_of_the_file_is_rejected()
    {
        // 0xFFFF_FF00 + 0x200 == 0x0000_0100 in 32-bit arithmetic: the sum lands *inside* a small
        // file and the bounds check used to pass.
        var path = TempFile();
        try
        {
            var bytes = new byte[PbfReader.HeaderV2Size + 8];
            Header(2, 64, 32, 1, directoryOffset: 0xFFFF_FF00, directoryLength: 0x200).CopyTo(bytes, 0);
            File.WriteAllBytes(path, bytes);

            var act = () => PbfReader.ReadDocument(path);

            act.Should().Throw<PbfFormatException>().WithMessage("*runs past the end of the file*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_name_table_that_ends_mid_entry_is_a_format_error()
    {
        var path = TempFile();
        try
        {
            // Claims two separations but carries no name payload at all.
            File.WriteAllBytes(path, Header(2, 64, 32, 2));

            var act = () => PbfReader.ReadDocument(path);

            act.Should().Throw<PbfFormatException>().WithMessage("*name table is truncated*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_name_that_runs_past_the_end_of_the_file_is_a_format_error()
    {
        var path = TempFile();
        try
        {
            var bytes = new byte[PbfReader.HeaderV2Size + 4];
            Header(2, 64, 32, 1).CopyTo(bytes, 0);

            // One name: length 4096, but only two bytes follow.
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(PbfReader.HeaderV2Size), 4096);
            File.WriteAllBytes(path, bytes);

            var act = () => PbfReader.ReadDocument(path);

            act.Should().Throw<PbfFormatException>().WithMessage("*file ends first*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_sequential_layout_larger_than_the_file_is_rejected_before_allocating()
    {
        var path = TempFile();
        try
        {
            // 20 000 x 20 000 px = 400 Mpx, well under the 4 Gpx cap. The name table is valid, so
            // the sequential plane size is what has to be rejected: 4 x 50 MB in a 60-byte file.
            var names = new List<byte>();
            for (var i = 0; i < 4; i++)
            {
                names.Add(0);
                names.Add(4);
                names.AddRange("Cyan"u8.ToArray());
            }

            var bytes = Header(2, 20_000, 20_000, 4).Concat(names).ToArray();
            File.WriteAllBytes(path, bytes);

            var act = () => PbfReader.ReadDocument(path);

            act.Should().Throw<PbfFormatException>().WithMessage("*the file is only*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0, 32, 1)]          // zero width
    [InlineData(-5, 32, 1)]         // negative width
    [InlineData(64, 32, 0)]         // no separations
    [InlineData(64, 32, 65)]        // more separations than any press has
    [InlineData(70_000, 70_000, 1)] // 4.9 Gpx, over the plausibility cap
    public void Implausible_headers_are_rejected(int width, int height, int separations)
    {
        var path = TempFile();
        try
        {
            File.WriteAllBytes(path, Header(2, width, height, separations));

            var act = () => PbfReader.ReadDocument(path);

            act.Should().Throw<PbfFormatException>();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
