using System.Buffers.Binary;
using System.Text;
using SoftProof.RIPParsers.Raster;
using SoftProof.RIPParsers.Raster.Compression;

namespace SoftProof.RIPParsers.Raster.Tiff;

/// <summary>
/// Minimal, dependency-free TIFF reader for prepress plate files.
/// <para>
/// Covers what real RIP plate writers emit: multi-page files (one page per separation),
/// strips <em>and</em> tiles, 1/8/16 bits per sample, chunky or planar samples,
/// photometric WhiteIsZero / BlackIsZero / Separated (CMYK) / RGB, FillOrder 2 (LSB-first),
/// predictor 2, and none / PackBits / LZW compression. Unsupported layouts (JPEG, CCITT,
/// Deflate) are rejected with a diagnostic instead of yielding a blank plate.
/// </para>
/// </summary>
public static class TiffReader
{
    private const ushort TagImageWidth = 256;
    private const ushort TagImageLength = 257;
    private const ushort TagBitsPerSample = 258;
    private const ushort TagCompression = 259;
    private const ushort TagPhotometric = 262;
    private const ushort TagFillOrder = 266;
    private const ushort TagImageDescription = 270;
    private const ushort TagStripOffsets = 273;
    private const ushort TagSamplesPerPixel = 277;
    private const ushort TagRowsPerStrip = 278;
    private const ushort TagStripByteCounts = 279;
    private const ushort TagPlanarConfiguration = 284;
    private const ushort TagPageName = 285;
    private const ushort TagPredictor = 317;
    private const ushort TagTileWidth = 322;
    private const ushort TagTileLength = 323;
    private const ushort TagTileOffsets = 324;
    private const ushort TagTileByteCounts = 325;
    private const ushort TagSampleFormat = 339;

    /// <summary>Number of pages (IFDs) in the file.</summary>
    public static int PageCount(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var (littleEndian, firstIfd) = ReadTiffHeader(bytes);
        var count = 0;
        var offset = firstIfd;
        while (offset > 0 && offset < bytes.Length && count < 512)
        {
            var (_, next) = ReadIfd(bytes, offset, littleEndian);
            count++;
            offset = next;
        }
        return Math.Max(count, 1);
    }

    /// <summary>TIFF tag 285 (page name) for the given page, when present.</summary>
    public static string? ReadPageName(string path, int pageIndex = 0) =>
        ReadTextTag(path, pageIndex, TagPageName);

    /// <summary>TIFF tag 270 (ImageDescription) for the given page, when present.</summary>
    public static string? ReadDescription(string path, int pageIndex = 0) =>
        ReadTextTag(path, pageIndex, TagImageDescription);

    private static string? ReadTextTag(string path, int pageIndex, ushort tag)
    {
        var bytes = File.ReadAllBytes(path);
        var (littleEndian, firstIfd) = ReadTiffHeader(bytes);
        var offset = firstIfd;

        for (var page = 0; page <= pageIndex; page++)
        {
            if (offset <= 0 || offset >= bytes.Length)
                return null;

            if (page == pageIndex)
                return ReadAsciiTag(bytes, offset, tag, littleEndian);

            var (_, next) = ReadIfd(bytes, offset, littleEndian);
            offset = next;
        }
        return null;
    }

    public static TiffImage Read(string path, int pageIndex = 0)
    {
        var bytes = File.ReadAllBytes(path);
        return Read(bytes, pageIndex);
    }

    public static TiffImage Read(ReadOnlySpan<byte> bytes, int pageIndex = 0)
    {
        var (littleEndian, firstIfd) = ReadTiffHeader(bytes);
        var tags = ReadIfdAt(bytes, firstIfd, pageIndex, littleEndian)
                   ?? throw new PbfFormatException(
                       $"TIFF has no page {pageIndex}; the file has fewer pages than requested.");

        var width = (int)First(tags, TagImageWidth, 0);
        var height = (int)First(tags, TagImageLength, 0);
        if (width <= 0 || height <= 0)
            throw new PbfFormatException($"TIFF page {pageIndex} has invalid dimensions {width}x{height}.");

        var samples = (int)First(tags, TagSamplesPerPixel, 1);
        if (samples is < 1 or > 8)
            throw new PbfFormatException($"TIFF has unsupported SamplesPerPixel={samples}.");

        var bitsPerSample = new int[samples];
        var bits = tags.TryGetValue(TagBitsPerSample, out var bps) ? bps : new[] { 1u };
        for (var i = 0; i < samples; i++)
            bitsPerSample[i] = (int)bits[Math.Min(i, bits.Length - 1)];

        foreach (var b in bitsPerSample)
            if (b is not (1 or 8 or 16))
                throw new PbfFormatException($"TIFF has unsupported BitsPerSample={b}.");

        var compression = (int)First(tags, TagCompression, 1);
        if (compression is 3)
            throw new PbfFormatException(
                "TIFF compression CCITT G3 is not supported; Group 4 (T.6) is. " +
                "Re-export the plate as Group 4, PackBits or LZW.");

        if (compression is not (1 or 4 or 5 or 32773))
            throw new PbfFormatException(
                $"TIFF compression {CompressionName(compression)} is not supported " +
                "(supported: none, CCITT G4, PackBits, LZW).");

        var photometric = (int)First(tags, TagPhotometric, 1);
        var planar = (int)First(tags, TagPlanarConfiguration, 1);
        var predictor = (int)First(tags, TagPredictor, 1);
        var lsbFirst = (int)First(tags, TagFillOrder, 1) == 2;

        if (predictor == 2 && bitsPerSample.Any(b => b == 1))
            throw new PbfFormatException("TIFF predictor 2 is not valid for 1-bit samples.");

        if (compression == 4)
        {
            if (samples != 1 || bitsPerSample[0] != 1)
                throw new PbfFormatException("CCITT Group 4 requires 1-bit, single-sample data.");

            var g4Pixels = (long)width * height;
            if (g4Pixels > MaxPixelsPerPage)
                throw new PbfFormatException(
                    $"CCITT Group 4 page is {width} x {height} px ({g4Pixels:N0} pixels). " +
                    "Full-resolution decoding would need one byte per pixel; decode it with " +
                    "downsampling instead (TiffReader.ReadG4Coverage).");

            var plane = DecodeG4Plane(bytes, tags, width, height, photometric);
            return new TiffImage(width, height, bitsPerSample, samples, photometric, planar,
                                 new[] { plane });
        }

        // One byte of coverage per pixel; a multi-gigapixel plate cannot be allocated.
        var totalPixels = (long)width * height;
        if (totalPixels > MaxPixelsPerPage)
        {
            var gigabytes = totalPixels / (1024.0 * 1024.0 * 1024.0);
            throw new PbfFormatException(
                $"TIFF page is {width} x {height} px ({totalPixels:N0} pixels) and would need " +
                $"{gigabytes:0.0} GB as one byte per pixel. This build decodes up to " +
                $"{MaxPixelsPerPage:N0} pixels per page at full resolution; " +
                "use a Group 4 plate (decoded with downsampling) or export a smaller raster.");
        }

        var planes = new byte[samples][];
        for (var s = 0; s < samples; s++)
            planes[s] = new byte[(long)width * height];

        var tiled = tags.ContainsKey(TagTileWidth) && tags.ContainsKey(TagTileOffsets);
        if (tiled)
        {
            ReadTiles(bytes, tags, width, height, samples, bitsPerSample[0], planar, predictor,
                      lsbFirst, compression, planes);
        }
        else
        {
            ReadStrips(bytes, tags, width, height, samples, bitsPerSample[0], planar, predictor,
                       lsbFirst, compression, planes);
        }

        return new TiffImage(width, height, bitsPerSample, samples, photometric, planar, planes);
    }

    /// <summary>Full-resolution decode budget per page (one byte of coverage per pixel).</summary>
    public const long MaxPixelsPerPage = 250_000_000;

    /// <summary>Header facts about one page, without decoding any pixels.</summary>
    public readonly record struct TiffInfo(int Width, int Height, int BitsPerSample,
                                           int SamplesPerPixel, int Compression, int Photometric);

    public static TiffInfo GetInfo(string path, int pageIndex = 0)
    {
        var bytes = File.ReadAllBytes(path);
        var (littleEndian, firstIfd) = ReadTiffHeader(bytes);
        var tags = ReadIfdAt(bytes, firstIfd, pageIndex, littleEndian)
                   ?? throw new PbfFormatException($"TIFF has no page {pageIndex}.");

        return new TiffInfo(
            (int)First(tags, TagImageWidth, 0),
            (int)First(tags, TagImageLength, 0),
            (int)First(tags, TagBitsPerSample, 1),
            (int)First(tags, TagSamplesPerPixel, 1),
            (int)First(tags, TagCompression, 1),
            (int)First(tags, TagPhotometric, 1));
    }

    /// <summary>
    /// Decodes a CCITT G4 page straight into ink coverage, downsampling while decoding, so a
    /// multi-gigapixel plate never exists in memory at full resolution.
    /// </summary>
    public static byte[] ReadG4Coverage(string path, int pageIndex, int maxPixels,
                                        out int outWidth, out int outHeight, out int factor)
    {
        var bytes = File.ReadAllBytes(path);
        var (littleEndian, firstIfd) = ReadTiffHeader(bytes);
        var tags = ReadIfdAt(bytes, firstIfd, pageIndex, littleEndian)
                   ?? throw new PbfFormatException($"TIFF has no page {pageIndex}.");

        var width = (int)First(tags, TagImageWidth, 0);
        var height = (int)First(tags, TagImageLength, 0);
        var bits = (int)First(tags, TagBitsPerSample, 1);
        var samples = (int)First(tags, TagSamplesPerPixel, 1);
        var compression = (int)First(tags, TagCompression, 1);
        var photometric = (int)First(tags, TagPhotometric, 1);

        if (compression != 4)
            throw new PbfFormatException(
                $"TIFF compression {CompressionName(compression)} is not Group 4.");
        if (bits != 1 || samples != 1)
            throw new PbfFormatException(
                "CCITT Group 4 downsampling requires 1-bit single-sample data.");
        if (maxPixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPixels));

        factor = 1;
        while ((long)CeilDiv(width, factor) * CeilDiv(height, factor) > maxPixels)
            factor++;

        outWidth = CeilDiv(width, factor);
        outHeight = CeilDiv(height, factor);

        var scale = factor;
        var outputWidth = outWidth;
        var outputHeight = outHeight;

        var coverage = new byte[(long)outputWidth * outputHeight];
        var accumulator = new long[outputWidth];
        var inkIsBlack = photometric == 0;
        var rowInBlock = 0;

        DecodeG4Rows(bytes, tags, width, height, (sourceY, transitions, count) =>
        {
            Array.Clear(accumulator);
            AccumulateRow(accumulator, transitions, count, width, scale, inkIsBlack);
            rowInBlock++;

            var blockEnds = (sourceY + 1) % scale == 0 || sourceY == height - 1;
            if (!blockEnds)
                return;

            var outY = sourceY / scale;
            var rowsInBlock = rowInBlock;
            for (var x = 0; x < outputWidth; x++)
            {
                var cellWidth = Math.Min(scale, width - x * scale);
                var area = (long)cellWidth * rowsInBlock;
                var value = area == 0 ? 0 : accumulator[x] * 255 / area;
                coverage[(long)outY * outputWidth + x] = (byte)Math.Clamp(value, 0, 255);
            }
            rowInBlock = 0;
        });

        return coverage;
    }

    private static int CeilDiv(int value, int divisor) => (value + divisor - 1) / divisor;

    private static void AccumulateRow(long[] accumulator, int[] transitions, int count, int width,
                                      int factor, bool inkIsBlack)
    {
        var position = 0;
        var color = 0; // 0 = white; transitions alternate starting with white→black
        var inkColor = inkIsBlack ? 1 : 0;

        for (var i = 0; i <= count; i++)
        {
            var end = i < count ? transitions[i] : width;
            if (color == inkColor)
                AccumulateRun(accumulator, position, end, factor, width);

            position = end;
            color ^= 1;
        }
    }

    private static void AccumulateRun(long[] accumulator, int start, int end, int factor, int width)
    {
        if (end <= start)
            return;

        var firstCell = start / factor;
        var lastCell = (end - 1) / factor;

        for (var cell = firstCell; cell <= lastCell; cell++)
        {
            var cellStart = cell * factor;
            var cellEnd = Math.Min(cellStart + factor, width);
            var overlap = Math.Min(end, cellEnd) - Math.Max(start, cellStart);
            if (overlap > 0)
                accumulator[cell] += overlap;
        }
    }

    private static void DecodeG4Rows(ReadOnlySpan<byte> bytes, Dictionary<ushort, uint[]> tags,
                                     int width, int height, Action<int, int[], int> onRow)
    {
        var offsets = ToIntArray(tags, TagStripOffsets);
        var counts = ToIntArray(tags, TagStripByteCounts);
        if (offsets.Length == 0 || counts.Length != offsets.Length)
            throw new PbfFormatException("CCITT TIFF has no usable strip table.");

        var rowsPerStrip = (int)First(tags, TagRowsPerStrip, (uint)height);
        if (rowsPerStrip <= 0 || rowsPerStrip > height)
            rowsPerStrip = height;

        var y0 = 0;
        for (var i = 0; i < offsets.Length; i++)
        {
            var rows = Math.Min(rowsPerStrip, height - y0);
            if (rows <= 0)
                break;

            var payload = SliceBlock(bytes, offsets[i], counts[i]);
            var start = y0;
            CcittG4.DecodeRows(payload, width, rows,
                (y, transitions, count) => onRow(start + y, transitions, count));
            y0 += rows;
        }
    }

    /// <summary>
    /// Decodes any 1-bit page (none / CCITT G4 / LZW / PackBits) into downsampled ink coverage.
    /// Strips are decoded one at a time and immediately reduced into the output grid, so peak
    /// memory is one strip plus the preview — never a byte per source pixel.
    /// </summary>
    public static byte[] ReadBilevelCoverage(string path, int pageIndex, int maxPixels,
                                             out int outWidth, out int outHeight, out int factor,
                                             IProgress<double>? progress = null,
                                             CancellationToken cancellationToken = default)
    {
        if (maxPixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPixels));

        var bytes = File.ReadAllBytes(path);
        var (littleEndian, firstIfd) = ReadTiffHeader(bytes);
        var tags = ReadIfdAt(bytes, firstIfd, pageIndex, littleEndian)
                   ?? throw new PbfFormatException($"TIFF has no page {pageIndex}.");

        var width = (int)First(tags, TagImageWidth, 0);
        var height = (int)First(tags, TagImageLength, 0);
        var bits = (int)First(tags, TagBitsPerSample, 1);
        var samples = (int)First(tags, TagSamplesPerPixel, 1);
        var compression = (int)First(tags, TagCompression, 1);
        var photometric = (int)First(tags, TagPhotometric, 1);

        if (bits != 1 || samples != 1)
            throw new PbfFormatException(
                "Streaming downsample is only implemented for 1-bit single-sample plates.");
        if (compression is not (1 or 4 or 5 or 32773))
            throw new PbfFormatException(
                $"TIFF compression {CompressionName(compression)} is not supported for streaming.");

        factor = 1;
        while ((long)CeilDiv(width, factor) * CeilDiv(height, factor) > maxPixels)
            factor++;

        outWidth = CeilDiv(width, factor);
        outHeight = CeilDiv(height, factor);

        var accumulator = new BilevelAccumulator(width, height, factor, outWidth, outHeight,
                                                 photometric == 0);

        // Progress/cancel tick: decoding is row-sequential, so a cheap check every 256 rows
        // keeps the UI responsive without slowing the strip loop.
        void Tick(int y)
        {
            if ((y & 255) != 0)
                return;

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(Math.Min(100.0, (y + 1) * 100.0 / height));
        }

        if (compression == 4)
        {
            DecodeG4Rows(bytes, tags, width, height, (y, transitions, count) =>
            {
                Tick(y);
                accumulator.AddTransitions(y, transitions, count);
            });
        }
        else
        {
            DecodeStripRows(bytes, tags, width, height, compression, (y, row) =>
            {
                Tick(y);
                accumulator.AddPackedRow(y, row);
            });
        }

        accumulator.Finish();
        progress?.Report(100);
        return accumulator.Coverage;
    }

    private delegate void RowHandler(int y, ReadOnlySpan<byte> row);

    private static void DecodeStripRows(ReadOnlySpan<byte> bytes, Dictionary<ushort, uint[]> tags,
                                        int width, int height, int compression, RowHandler onRow)
    {
        var offsets = ToIntArray(tags, TagStripOffsets);
        var counts = ToIntArray(tags, TagStripByteCounts);
        if (offsets.Length == 0 || counts.Length != offsets.Length)
            throw new PbfFormatException("TIFF has no usable strip table.");

        var rowsPerStrip = (int)First(tags, TagRowsPerStrip, (uint)height);
        if (rowsPerStrip <= 0 || rowsPerStrip > height)
            rowsPerStrip = height;

        var rowBytes = (width + 7) / 8;
        var y0 = 0;

        for (var i = 0; i < offsets.Length; i++)
        {
            var rows = Math.Min(rowsPerStrip, height - y0);
            if (rows <= 0)
                break;

            var payload = SliceBlock(bytes, offsets[i], counts[i]);
            var expected = rowBytes * rows;

            var decoded = compression switch
            {
                1 => payload.ToArray(),
                5 => Lzw.Decode(payload, expected),
                32773 => PackBits.Decode(payload, expected),
                _ => throw new PbfFormatException($"Unsupported compression {compression}.")
            };

            if (decoded.Length < expected)
                Array.Resize(ref decoded, expected);

            for (var r = 0; r < rows; r++)
                onRow(y0 + r, decoded.AsSpan(r * rowBytes, rowBytes));

            y0 += rows;
        }
    }

    /// <summary>Reduces 1-bpp rows into 8-bit coverage at a fixed decimation factor.</summary>
    private sealed class BilevelAccumulator
    {
        private readonly int _width;
        private readonly int _height;
        private readonly int _factor;
        private readonly int _outWidth;
        private readonly int _outHeight;
        private readonly bool _inkIsBlack;
        private readonly long[] _rowSums;
        private readonly byte[] _coverage;
        private int _rowsInBlock;

        public BilevelAccumulator(int width, int height, int factor, int outWidth, int outHeight,
                                  bool inkIsBlack)
        {
            _width = width;
            _height = height;
            _factor = factor;
            _outWidth = outWidth;
            _outHeight = outHeight;
            _inkIsBlack = inkIsBlack;
            _rowSums = new long[outWidth];
            _coverage = new byte[(long)outWidth * outHeight];
        }

        public byte[] Coverage => _coverage;

        public void AddTransitions(int y, int[] transitions, int count)
        {
            var position = 0;
            var color = 0;
            var inkColor = _inkIsBlack ? 1 : 0;

            for (var i = 0; i <= count; i++)
            {
                var end = i < count ? transitions[i] : _width;
                if (color == inkColor)
                    AddSpan(_rowSums, position, end, _factor, _width);

                position = end;
                color ^= 1;
            }

            CompleteRow(y);
        }

        public void AddPackedRow(int y, ReadOnlySpan<byte> row)
        {
            var x = 0;
            for (var i = 0; i < row.Length && x < _width; i++, x += 8)
            {
                var count = Math.Min(8, _width - x);
                var value = row[i];

                // Whole-byte fast paths: real plates are mostly solid runs.
                if (value == 0)
                {
                    if (!_inkIsBlack)
                        AddSpan(_rowSums, x, x + count, _factor, _width);
                    continue;
                }

                if (value == 0xFF)
                {
                    if (_inkIsBlack)
                        AddSpan(_rowSums, x, x + count, _factor, _width);
                    continue;
                }

                var bits = _inkIsBlack ? value : (byte)~value;
                for (var bit = 0; bit < count; bit++)
                    if (((bits >> (7 - bit)) & 1) != 0)
                        AddSpan(_rowSums, x + bit, x + bit + 1, _factor, _width);
            }

            CompleteRow(y);
        }

        private void CompleteRow(int y)
        {
            _rowsInBlock++;
            var blockEnds = (y + 1) % _factor == 0 || y == _height - 1;
            if (!blockEnds)
                return;

            var outY = y / _factor;
            var rows = _rowsInBlock;

            for (var x = 0; x < _outWidth; x++)
            {
                var cellWidth = Math.Min(_factor, _width - x * _factor);
                var area = (long)cellWidth * rows;
                var value = area == 0 ? 0 : _rowSums[x] * 255 / area;
                _coverage[(long)outY * _outWidth + x] = (byte)Math.Clamp(value, 0, 255);
            }

            _rowsInBlock = 0;
            Array.Clear(_rowSums); // rows accumulate across the whole decimation block
        }

        public void Finish() { }
    }

    private static void AddSpan(long[] accumulator, int start, int end, int factor, int width)
    {
        if (end <= start)
            return;

        var firstCell = start / factor;
        var lastCell = (end - 1) / factor;

        for (var cell = firstCell; cell <= lastCell; cell++)
        {
            var cellStart = cell * factor;
            var cellEnd = Math.Min(cellStart + factor, width);
            var overlap = Math.Min(end, cellEnd) - Math.Max(start, cellStart);
            if (overlap > 0)
                accumulator[cell] += overlap;
        }
    }

    private static byte[] DecodeG4Plane(ReadOnlySpan<byte> bytes, Dictionary<ushort, uint[]> tags,
                                        int width, int height, int photometric)
    {
        var plane = new byte[(long)width * height];

        // G4 runs are fax white/black. Store the raw TIFF sample here: a fax-black pixel is
        // sample 1, fax-white is sample 0. InkCoverage then applies the photometric tag
        // (photometric 1 means sample 0 is black, so the plate is the inverse of the fax runs).
        const byte white = 0;
        const byte black = 255;

        Array.Fill(plane, white);

        DecodeG4Rows(bytes, tags, width, height, (y, transitions, count) =>
        {
            var row = (long)y * width;
            var position = 0;
            var color = 0;

            for (var i = 0; i <= count; i++)
            {
                var end = i < count ? transitions[i] : width;
                if (color == 1)
                    for (var x = position; x < end; x++)
                        plane[row + x] = black;

                position = end;
                color ^= 1;
            }
        });

        return plane;
    }

    private static ReadOnlySpan<byte> SliceBlock(ReadOnlySpan<byte> bytes, int offset, int byteCount)
    {
        if (offset < 0 || byteCount < 0 || (long)offset + byteCount > bytes.Length)
            throw new PbfFormatException(
                $"TIFF data block points outside the file (offset {offset}, {byteCount} bytes, " +
                $"file {bytes.Length} bytes).");

        return bytes.Slice(offset, byteCount);
    }

    // ---- strips -------------------------------------------------------------------------------

    private static void ReadStrips(ReadOnlySpan<byte> bytes, Dictionary<ushort, uint[]> tags,
                                   int width, int height, int samples, int bitsPerSample, int planar,
                                   int predictor, bool lsbFirst, int compression, byte[][] planes)
    {
        var stripOffsets = ToIntArray(tags, TagStripOffsets);
        var stripByteCounts = ToIntArray(tags, TagStripByteCounts);
        if (stripOffsets.Length == 0)
            throw new PbfFormatException("TIFF has no strip offsets (tag 273).");
        if (stripByteCounts.Length != stripOffsets.Length)
            throw new PbfFormatException(
                $"TIFF strip table mismatch: {stripOffsets.Length} offsets vs {stripByteCounts.Length} byte counts.");

        var rowsPerStrip = (int)First(tags, TagRowsPerStrip, (uint)height);
        if (rowsPerStrip <= 0 || rowsPerStrip > height)
            rowsPerStrip = height;

        var stripsPerPlane = (height + rowsPerStrip - 1) / rowsPerStrip;
        var expectedStrips = planar == 2 ? stripsPerPlane * samples : stripsPerPlane;
        if (stripOffsets.Length < expectedStrips)
            throw new PbfFormatException(
                $"TIFF declares {stripOffsets.Length} strips but {expectedStrips} are required " +
                $"(planar={planar}, rowsPerStrip={rowsPerStrip}, height={height}, samples={samples}).");

        for (var index = 0; index < stripOffsets.Length; index++)
        {
            var sample = planar == 2 ? index / stripsPerPlane : 0;
            var stripInPlane = planar == 2 ? index % stripsPerPlane : index;
            var rowStart = stripInPlane * rowsPerStrip;
            if (rowStart >= height)
                continue;

            var rows = Math.Min(rowsPerStrip, height - rowStart);
            var rowSamples = planar == 2 ? 1 : samples;
            var rowBytes = (int)(((long)width * rowSamples * bitsPerSample + 7) / 8);
            var expected = rowBytes * rows;

            var decoded = DecodeBlock(bytes, stripOffsets[index], stripByteCounts[index],
                                      compression, expected);

            if (predictor == 2)
                UndoHorizontalPredictor(decoded, rows, rowBytes, rowSamples, bitsPerSample);

            for (var r = 0; r < rows; r++)
            {
                var rowOffset = r * rowBytes;
                var target = (long)(rowStart + r) * width;
                for (var x = 0; x < width; x++)
                    for (var i = 0; i < rowSamples; i++)
                    {
                        var value = ReadSample(decoded, rowOffset, x * rowSamples + i, bitsPerSample, lsbFirst);
                        var targetSample = planar == 2 ? sample : i;
                        planes[targetSample][target + x] = (byte)value;
                    }
            }
        }
    }

    // ---- tiles --------------------------------------------------------------------------------

    private static void ReadTiles(ReadOnlySpan<byte> bytes, Dictionary<ushort, uint[]> tags,
                                  int width, int height, int samples, int bitsPerSample, int planar,
                                  int predictor, bool lsbFirst, int compression, byte[][] planes)
    {
        var tileWidth = (int)First(tags, TagTileWidth, (uint)width);
        var tileHeight = (int)First(tags, TagTileLength, (uint)height);
        if (tileWidth <= 0 || tileHeight <= 0)
            throw new PbfFormatException($"TIFF has invalid tile size {tileWidth}x{tileHeight}.");

        var offsets = ToIntArray(tags, TagTileOffsets);
        var counts = ToIntArray(tags, TagTileByteCounts);
        if (offsets.Length == 0 || counts.Length != offsets.Length)
            throw new PbfFormatException(
                $"TIFF tile table is incomplete ({offsets.Length} offsets, {counts.Length} byte counts).");

        var across = (width + tileWidth - 1) / tileWidth;
        var down = (height + tileHeight - 1) / tileHeight;
        var tilesPerPlane = across * down;
        var expectedTiles = planar == 2 ? tilesPerPlane * samples : tilesPerPlane;

        if (offsets.Length < expectedTiles)
            throw new PbfFormatException(
                $"TIFF declares {offsets.Length} tiles but {expectedTiles} are required.");

        for (var index = 0; index < offsets.Length; index++)
        {
            var sample = planar == 2 ? index / tilesPerPlane : 0;
            var tileIndex = planar == 2 ? index % tilesPerPlane : index;
            var tileX = tileIndex % across;
            var tileY = tileIndex / across;

            var rowSamples = planar == 2 ? 1 : samples;
            var rowBytes = (int)(((long)tileWidth * rowSamples * bitsPerSample + 7) / 8);
            var expected = rowBytes * tileHeight;

            var decoded = DecodeBlock(bytes, offsets[index], counts[index], compression, expected);

            if (predictor == 2)
                UndoHorizontalPredictor(decoded, tileHeight, rowBytes, rowSamples, bitsPerSample);

            var destX0 = tileX * tileWidth;
            var destY0 = tileY * tileHeight;

            for (var ty = 0; ty < tileHeight; ty++)
            {
                var destY = destY0 + ty;
                if (destY >= height) break;

                var rowOffset = ty * rowBytes;
                var target = (long)destY * width;
                for (var tx = 0; tx < tileWidth; tx++)
                {
                    var destX = destX0 + tx;
                    if (destX >= width) continue;

                    for (var i = 0; i < rowSamples; i++)
                    {
                        var value = ReadSample(decoded, rowOffset, tx * rowSamples + i, bitsPerSample, lsbFirst);
                        var targetSample = planar == 2 ? sample : i;
                        planes[targetSample][target + destX] = (byte)value;
                    }
                }
            }
        }
    }

    // ---- sample/byte helpers ------------------------------------------------------------------

    private static byte[] DecodeBlock(ReadOnlySpan<byte> bytes, int offset, int byteCount,
                                      int compression, int expected)
    {
        if (offset < 0 || byteCount < 0 || (long)offset + byteCount > bytes.Length)
            throw new PbfFormatException(
                $"TIFF data block points outside the file (offset {offset}, {byteCount} bytes, " +
                $"file {bytes.Length} bytes).");

        var payload = bytes.Slice(offset, byteCount);
        var decoded = compression switch
        {
            1 => payload.ToArray(),
            5 => Lzw.Decode(payload, expected),
            32773 => PackBits.Decode(payload, expected),
            _ => throw new PbfFormatException($"Unsupported TIFF compression {compression}.")
        };

        if (decoded.Length < expected)
            Array.Resize(ref decoded, expected); // short final block: pad, as libtiff does
        return decoded;
    }

    private static int ReadSample(byte[] data, int rowOffset, int sampleIndex, int bitsPerSample, bool lsbFirst)
    {
        if (bitsPerSample == 1)
        {
            var byteIndex = rowOffset + (sampleIndex >> 3);
            var shift = lsbFirst ? (sampleIndex & 7) : 7 - (sampleIndex & 7);
            return ((data[byteIndex] >> shift) & 1) * 255;
        }

        if (bitsPerSample == 8)
            return data[rowOffset + sampleIndex];

        // 16-bit: keep the high byte (plates are 8-bit coverage downstream).
        return data[rowOffset + sampleIndex * 2];
    }

    private static void UndoHorizontalPredictor(byte[] data, int rows, int rowBytes, int rowSamples, int bitsPerSample)
    {
        if (bitsPerSample == 1)
            return;

        var stride = bitsPerSample == 16 ? rowSamples * 2 : rowSamples;
        for (var r = 0; r < rows; r++)
        {
            var row = r * rowBytes;
            for (var i = stride; i < rowBytes; i++)
                data[row + i] = unchecked((byte)(data[row + i] + data[row + i - stride]));
        }
    }

    // ---- IFD ----------------------------------------------------------------------------------

    private static (bool LittleEndian, int FirstIfd) ReadTiffHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
            throw new PbfFormatException("TIFF file is shorter than its 8-byte header.");

        var littleEndian = bytes[0] == 'I' && bytes[1] == 'I';
        if (!littleEndian && !(bytes[0] == 'M' && bytes[1] == 'M'))
            throw new PbfFormatException("Not a TIFF file: missing II/MM byte-order mark.");

        var magic = ReadU16(bytes, 2, littleEndian);
        if (magic != 42)
            throw new PbfFormatException($"Not a classic TIFF file (magic {magic}, expected 42).");

        return (littleEndian, (int)ReadU32(bytes, 4, littleEndian));
    }

    private static Dictionary<ushort, uint[]>? ReadIfdAt(ReadOnlySpan<byte> bytes, int firstIfd,
                                                         int pageIndex, bool littleEndian)
    {
        var offset = firstIfd;
        for (var page = 0; page <= pageIndex; page++)
        {
            if (offset <= 0 || offset >= bytes.Length)
                return null;

            var (tags, next) = ReadIfd(bytes, offset, littleEndian);
            if (page == pageIndex)
                return tags;

            offset = next;
        }
        return null;
    }

    private static (Dictionary<ushort, uint[]> Tags, int NextOffset) ReadIfd(
        ReadOnlySpan<byte> bytes, int ifdOffset, bool littleEndian)
    {
        var tags = new Dictionary<ushort, uint[]>();
        if (ifdOffset <= 0 || ifdOffset + 2 > bytes.Length)
            throw new PbfFormatException($"TIFF IFD offset {ifdOffset} is outside the file.");

        var count = ReadU16(bytes, ifdOffset, littleEndian);
        var entryBase = ifdOffset + 2;

        for (var i = 0; i < count; i++)
        {
            var entry = entryBase + i * 12;
            if (entry + 12 > bytes.Length)
                throw new PbfFormatException("TIFF IFD is truncated.");

            var tag = ReadU16(bytes, entry, littleEndian);
            var type = ReadU16(bytes, entry + 2, littleEndian);
            var valueCount = ReadU32(bytes, entry + 4, littleEndian);
            var typeSize = TypeSize(type);
            if (typeSize == 0 || valueCount > 1_000_000)
                continue;

            var totalBytes = (long)typeSize * valueCount;
            int valueOffset;
            if (totalBytes <= 4)
                valueOffset = entry + 8;
            else
            {
                valueOffset = (int)ReadU32(bytes, entry + 8, littleEndian);
                if (valueOffset < 0 || valueOffset + totalBytes > bytes.Length)
                    throw new PbfFormatException($"TIFF tag {tag} value points outside the file.");
            }

            var values = new uint[valueCount];
            for (var v = 0; v < valueCount; v++)
            {
                values[v] = type switch
                {
                    1 or 6 => bytes[valueOffset + v],
                    3 or 8 => ReadU16(bytes, valueOffset + v * 2, littleEndian),
                    4 or 9 => ReadU32(bytes, valueOffset + v * 4, littleEndian),
                    16 => (uint)ReadU64(bytes, valueOffset + v * 8, littleEndian),
                    _ => 0
                };
            }
            tags[tag] = values;
        }

        var nextOffsetPosition = entryBase + count * 12;
        var next = nextOffsetPosition + 4 <= bytes.Length
            ? (int)ReadU32(bytes, nextOffsetPosition, littleEndian)
            : 0;

        return (tags, next);
    }

    private static string? ReadAsciiTag(ReadOnlySpan<byte> bytes, int ifdOffset, ushort wanted,
                                        bool littleEndian)
    {
        if (ifdOffset <= 0 || ifdOffset + 2 > bytes.Length)
            return null;

        var count = ReadU16(bytes, ifdOffset, littleEndian);
        var entryBase = ifdOffset + 2;

        for (var i = 0; i < count; i++)
        {
            var entry = entryBase + i * 12;
            if (entry + 12 > bytes.Length)
                return null;

            if (ReadU16(bytes, entry, littleEndian) != wanted)
                continue;

            if (ReadU16(bytes, entry + 2, littleEndian) != 2) // ASCII
                return null;

            var length = (int)ReadU32(bytes, entry + 4, littleEndian);
            if (length <= 0 || length > 4096)
                return null;

            var dataOffset = length <= 4
                ? entry + 8
                : (int)ReadU32(bytes, entry + 8, littleEndian);

            if (dataOffset < 0 || dataOffset + length > bytes.Length)
                return null;

            var text = Encoding.ASCII.GetString(bytes.Slice(dataOffset, length))
                                     .TrimEnd('\0').Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return null;
    }

    private static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 => 4,
        5 or 10 or 12 => 8,
        16 or 17 or 18 => 8,
        _ => 0
    };

    private static int[] ToIntArray(Dictionary<ushort, uint[]> tags, ushort tag) =>
        tags.TryGetValue(tag, out var v)
            ? v.Select(value => checked((int)value)).ToArray()
            : Array.Empty<int>();

    private static uint First(Dictionary<ushort, uint[]> tags, ushort tag, uint fallback) =>
        tags.TryGetValue(tag, out var v) && v.Length > 0 ? v[0] : fallback;

    private static ushort ReadU16(ReadOnlySpan<byte> b, int offset, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(offset, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(b.Slice(offset, 2));

    private static uint ReadU32(ReadOnlySpan<byte> b, int offset, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(offset, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(b.Slice(offset, 4));

    private static ulong ReadU64(ReadOnlySpan<byte> b, int offset, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(offset, 8))
            : BinaryPrimitives.ReadUInt64BigEndian(b.Slice(offset, 8));

    private static string CompressionName(int compression) => compression switch
    {
        2 => "CCITT G3",
        3 => "CCITT G4",
        6 => "old JPEG",
        7 => "JPEG",
        8 or 32946 => "Deflate",
        32773 => "PackBits",
        5 => "LZW",
        _ => compression.ToString()
    };
}

/// <summary>Decoded TIFF plate: dimensions, sample layout and one 8-bit plane per sample.</summary>
public sealed class TiffImage
{
    public TiffImage(int width, int height, int[] bitsPerSample, int samplesPerPixel,
                     int photometric, int planarConfiguration, byte[][] planes)
    {
        Width = width;
        Height = height;
        BitsPerSample = bitsPerSample;
        SamplesPerPixel = samplesPerPixel;
        Photometric = photometric;
        PlanarConfiguration = planarConfiguration;
        Planes = planes;
    }

    public int Width { get; }
    public int Height { get; }
    public int[] BitsPerSample { get; }
    public int SamplesPerPixel { get; }
    /// <summary>TIFF tag 262: 0 WhiteIsZero, 1 BlackIsZero, 2 RGB, 5 Separated (CMYK).</summary>
    public int Photometric { get; }
    public int PlanarConfiguration { get; }
    public byte[][] Planes { get; }

    public bool IsBilevel => BitsPerSample.All(b => b == 1);
    public bool IsSeparated => Photometric == 5;
    public bool IsWhiteIsZero => Photometric == 0;

    /// <summary>
    /// Ink coverage for one sample, 0..255 (0 = paper, 255 = solid).
    /// <para>
    /// Photometric matters: with WhiteIsZero a stored 0 is paper and 0xFF is solid ink, so the
    /// raw sample <em>is</em> the coverage; with BlackIsZero (and plain gray) the sample must be
    /// inverted. Separated (CMYK) samples are already ink amounts.
    /// </para>
    /// </summary>
    public byte[] InkCoverage(int sample = 0)
    {
        if (IsSeparated)
            return Planes[sample].ToArray();

        if (Photometric == 2 && Planes.Length >= 3) // RGB: luminance, then invert
        {
            var r = Planes[0];
            var g = Planes[1];
            var b = Planes[2];
            var lumaCoverage = new byte[r.Length];
            for (var i = 0; i < r.Length; i++)
            {
                var luma = 0.299 * r[i] + 0.587 * g[i] + 0.114 * b[i];
                lumaCoverage[i] = (byte)Math.Clamp(255 - luma, 0, 255);
            }
            return lumaCoverage;
        }

        var plane = Planes[sample];
        if (IsWhiteIsZero)
            return plane.ToArray();

        var coverage = new byte[plane.Length];
        for (var i = 0; i < plane.Length; i++)
            coverage[i] = (byte)(255 - plane[i]);
        return coverage;
    }
}
