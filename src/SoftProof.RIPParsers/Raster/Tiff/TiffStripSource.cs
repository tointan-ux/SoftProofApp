using System.Buffers.Binary;
using SoftProof.RIPParsers.Raster.Compression;

namespace SoftProof.RIPParsers.Raster.Tiff;

/// <summary>
/// Strip-based TIFF plate source (classic TIFF and BigTIFF). Opening parses the IFD and the
/// strip table only — no pixels are touched until rows are requested, so a 7 Gpx plate opens
/// in milliseconds.
///
/// Guarantees to consumers: rows are 1 bpp, MSB first, 1 = ink, regardless of file polarity,
/// fill order or compression. The file is opened with FileShare.ReadWrite | Delete so reading
/// works while the RIP is still writing it.
/// </summary>
public sealed class TiffStripSource : IPlateSource
{
    private const long MaxPixels = 20_000_000_000L;      // 20 Gpx hard guard
    private const int MaxDimension = 1_000_000;
    private const long StripCacheBudget = 64L * 1024 * 1024;
    private const long SingleStripBudget = 512L * 1024 * 1024;

    private readonly FileStream _stream;
    private readonly long _fileLength;
    private readonly ulong[] _offsets;
    private readonly int[] _counts;
    private readonly int _rowsPerStrip;
    private readonly bool _invert;
    private readonly bool _reverseBits;
    private readonly G4CheckpointStore? _checkpointStore;
    private readonly object _gate = new();
    private int _activeReads;
    private bool _disposeRequested;

    /// <summary>Rows between saved G4 checkpoints (0 disables checkpointing).</summary>
    public int CheckpointInterval { get; set; } = 512;

    /// <summary>Sidecar file for this plate's checkpoints; null when the plate is not single-strip G4.</summary>
    public string? CheckpointPath => _checkpointStore?.FilePath;

    /// <summary>Row the last single-strip G4 read actually started decoding from (diagnostics/tests).</summary>
    public int LastDecodeStartRow { get; private set; }

    // Multi-strip cache: decoded, already normalised strips with LRU eviction.
    private readonly Dictionary<int, byte[]> _strips = new();
    private readonly LinkedList<int> _lru = new();
    private long _cachedBytes;

    // Single-strip non-G4 files: decoded once into a full-page buffer (budget guarded).
    private byte[]? _singleStripBuffer;

    public PlateInfo Info { get; }
    public bool IsComplete { get; }

    private TiffStripSource(FileStream stream, long fileLength, PlateInfo info, ulong[] offsets,
                            int[] counts, int rowsPerStrip, bool invert, bool reverseBits,
                            G4CheckpointStore? checkpointStore, bool isComplete)
    {
        _stream = stream;
        _fileLength = fileLength;
        Info = info;
        _offsets = offsets;
        _counts = counts;
        _rowsPerStrip = rowsPerStrip;
        _invert = invert;
        _reverseBits = reverseBits;
        _checkpointStore = checkpointStore;
        IsComplete = isComplete;
    }

    public static TiffStripSource Open(string path, int pageIndex = 0)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                    FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PlateSourceException($"Cannot open '{path}': {ex.Message}", ex);
        }

        try
        {
            var fileLength = stream.Length;
            var directory = ReadDirectory(stream, fileLength, pageIndex);

            var width = (int)directory.Scalar(256, 0);
            var height = (int)directory.Scalar(257, 0);
            var bits = (int)directory.Scalar(258, 1);
            var compression = (int)directory.Scalar(259, 1);
            var photometric = (int)directory.Scalar(262, 1);
            var samples = (int)directory.Scalar(277, 1);
            var fillOrder = (int)directory.Scalar(266, 1);
            var unit = (int)directory.Scalar(296, 2);

            if (width <= 0 || height <= 0)
                throw new PlateSourceException("TIFF has no usable image dimensions.");
            if (width > MaxDimension || height > MaxDimension || (long)width * height > MaxPixels)
                throw new PlateSourceException(
                    $"Refusing implausible plate size {width}x{height}; header looks corrupt.");

            if (bits != 1 || samples != 1)
                throw new PlateSourceException(
                    $"Plate is {bits} bit(s) with {samples} sample(s); this source loads 1-bit plates only.");

            if (compression is not (1 or 4 or 5 or 32773))
                throw new PlateSourceException($"Unsupported TIFF compression {compression}.");

            if (directory.Has(322) || directory.Has(323) || directory.Has(324))
                throw new PlateSourceException("Tiled TIFF is not supported yet; use a strip-based plate.");

            var offsets = directory.Integers(273);
            var counts = directory.Integers(279);
            if (offsets.Length == 0 || counts.Length != offsets.Length)
                throw new PlateSourceException("CCITT TIFF has no usable strip table.");

            var rowsPerStrip = (int)directory.Scalar(278, height);
            if (rowsPerStrip <= 0 || rowsPerStrip > height)
                rowsPerStrip = height;

            var expectedStrips = (height + rowsPerStrip - 1) / rowsPerStrip;
            var complete = offsets.Length >= expectedStrips;
            for (var i = 0; i < offsets.Length; i++)
            {
                if (counts[i] == 0 || offsets[i] + counts[i] > (ulong)fileLength)
                {
                    complete = false;
                    break;
                }
            }

            var scale = unit == 3 ? 2.54 : 1.0;
            var dpiX = directory.Scalar(282, 0) * scale;
            var dpiY = directory.Scalar(283, 0) * scale;

            var info = new PlateInfo
            {
                Width = width,
                Height = height,
                DpiX = dpiX,
                DpiY = dpiY,
                WhiteIsZero = photometric == 0,
                SeparationHint = SeparationNaming.Parse(path),
                PageIndex = pageIndex,
                Compression = compression,
                RowsPerStrip = rowsPerStrip,
                StripCount = offsets.Length,
                IsBigTiff = directory.IsBigTiff,
                FilePath = path
            };

            var counts32 = counts.Select(c => (int)Math.Min(int.MaxValue, c)).ToArray();

            // Only one long G4 stream benefits from checkpoints: with multiple strips the strip
            // table already gives random access.
            var checkpointStore = compression == 4 && offsets.Length == 1
                ? new G4CheckpointStore(path, pageIndex)
                : null;

            return new TiffStripSource(stream, fileLength, info, offsets, counts32, rowsPerStrip,
                                       invert: photometric != 0, reverseBits: fillOrder == 2,
                                       checkpointStore, complete);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public async ValueTask ReadRowsAsync(int y0, int count, Memory<byte> dest,
                                         CancellationToken ct = default)
    {
        EnterRead();
        try
        {
            var rowBytes = Info.RowBytes;

            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (y0 < 0 || (long)y0 + count > Info.Height)
                throw new ArgumentOutOfRangeException(nameof(y0), "Row range is outside the plate.");
            if (count == 0)
                return;

            var required = (long)count * rowBytes;
            if (dest.Length < required)
                throw new ArgumentException($"Destination needs {required} bytes.", nameof(dest));

            if (_offsets.Length == 1)
            {
                // One strip: no random access inside a G4 stream, so decode forward and stop at y1.
                if (Info.Compression == 4)
                    await Task.Run(() => ReadSingleStripG4(y0, count, dest, ct), ct).ConfigureAwait(false);
                else
                    await Task.Run(() => ReadSingleStripRaw(y0, count, dest, ct), ct).ConfigureAwait(false);
            }
            else
            {
                await Task.Run(() => ReadFromStrips(y0, count, dest, ct), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ExitRead();
        }
    }

    /// <summary>
    /// Register an in-flight read so Dispose cannot close the stream under it. A pan that
    /// supersedes a decode used to dispose the shared FileStream while RandomAccess.Read was
    /// still using its handle.
    /// </summary>
    private void EnterRead()
    {
        lock (_gate)
        {
            if (_disposeRequested)
                throw new ObjectDisposedException(nameof(TiffStripSource));
            _activeReads++;
        }
    }

    private void ExitRead()
    {
        var close = false;
        lock (_gate)
        {
            _activeReads--;
            close = _disposeRequested && _activeReads == 0;
        }

        if (close)
            CloseResources();
    }

    private void ReadFromStrips(int y0, int count, Memory<byte> dest, CancellationToken ct)
    {
        var rowBytes = Info.RowBytes;
        var end = y0 + count;
        var firstStrip = y0 / _rowsPerStrip;
        var lastStrip = (end - 1) / _rowsPerStrip;

        EnsureStrips(firstStrip, lastStrip, ct);

        lock (_gate)
        {
            for (var s = firstStrip; s <= lastStrip; s++)
            {
                ct.ThrowIfCancellationRequested();

                var strip = GetStrip(s, ct);
                var stripFirstRow = s * _rowsPerStrip;
                var rowsInStrip = Math.Min(_rowsPerStrip, Info.Height - stripFirstRow);

                var from = Math.Max(y0, stripFirstRow);
                var to = Math.Min(end, stripFirstRow + rowsInStrip);
                var bytes = (long)(to - from) * rowBytes;
                if (bytes > int.MaxValue)
                    throw new PlateSourceException("Requested row range is too large for one buffer.");

                var sourceOffset = (long)(from - stripFirstRow) * rowBytes;
                var targetOffset = (long)(from - y0) * rowBytes;
                if (sourceOffset + bytes > strip.LongLength || targetOffset + bytes > dest.Length)
                    throw new PlateSourceException("Strip row range lies outside its buffer.");

                strip.AsSpan((int)sourceOffset, (int)bytes)
                     .CopyTo(dest.Span.Slice((int)targetOffset, (int)bytes));
            }
        }
    }

    /// <summary>
    /// Decode the strips a view needs, in parallel. Strips are independent by definition in G4
    /// (each resets the reference line), and reads are positional, so this scales with cores.
    /// </summary>
    private void EnsureStrips(int first, int last, CancellationToken ct)
    {
        var missing = new List<int>();
        lock (_gate)
        {
            for (var s = first; s <= last; s++)
                if (!_strips.ContainsKey(s))
                    missing.Add(s);
        }

        if (missing.Count == 0)
            return;

        if (missing.Count == 1 || Info.RowsPerStrip > 8192)
        {
            foreach (var s in missing)
            {
                var buffer = DecodeStrip(s, RowsInStrip(s), ct);
                lock (_gate) InsertStrip(s, buffer);
            }

            return;
        }

        var decoded = new byte[missing.Count][];
        try
        {
            Parallel.For(0, missing.Count,
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
                i => decoded[i] = DecodeStrip(missing[i], RowsInStrip(missing[i]), ct));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
        {
            // Surface the real cause (truncated strip, corrupt stream) rather than Parallel's wrapper.
            throw ex.InnerException!;
        }

        lock (_gate)
        {
            for (var i = 0; i < missing.Count; i++)
                InsertStrip(missing[i], decoded[i]);
        }
    }

    private int RowsInStrip(int index) =>
        Math.Min(_rowsPerStrip, Info.Height - index * _rowsPerStrip);

    private void InsertStrip(int index, byte[] buffer)
    {
        if (_strips.ContainsKey(index))
            return;

        _strips[index] = buffer;
        _lru.AddFirst(index);
        _cachedBytes += buffer.Length;

        while (_cachedBytes > StripCacheBudget && _lru.Count > 1)
        {
            var evict = _lru.Last!.Value;
            _lru.RemoveLast();
            if (_strips.Remove(evict, out var old))
                _cachedBytes -= old.Length;
        }
    }

    private byte[] GetStrip(int index, CancellationToken ct)
    {
        if (_strips.TryGetValue(index, out var cached))
        {
            _lru.Remove(index);
            _lru.AddFirst(index);
            return cached;
        }

        var buffer = DecodeStrip(index, RowsInStrip(index), ct);
        InsertStrip(index, buffer);
        return buffer;
    }

    private byte[] DecodeStrip(int index, int rows, CancellationToken ct)
    {
        var rowBytes = Info.RowBytes;
        var length = (long)rows * rowBytes;
        if (length > int.MaxValue)
            throw new PlateSourceException("Decoded strip is too large for one buffer; reduce RowsPerStrip.");

        var target = new byte[length];
        var payload = ReadAt(_offsets[index], _counts[index]);

        switch (Info.Compression)
        {
            case 4:
                CcittG4.DecodeRows(payload, Info.Width, rows,
                    (y, transitions, count) => FillRow(target, y, transitions, count));
                break;

            case 5:
            case 32773:
            {
                var expected = (int)Math.Min(int.MaxValue, (long)rows * rowBytes);
                var raw = Info.Compression == 5
                    ? Lzw.Decode(payload, expected)
                    : PackBits.Decode(payload, expected);
                Array.Copy(raw, target, Math.Min(raw.Length, target.Length));
                NormalisePacked(target, rows);
                break;
            }

            default:
                Array.Copy(payload, target, Math.Min(payload.Length, target.Length));
                NormalisePacked(target, rows);
                break;
        }

        ct.ThrowIfCancellationRequested();
        return target;
    }

    private void ReadSingleStripG4(int y0, int count, Memory<byte> dest, CancellationToken ct)
    {
        var rowBytes = Info.RowBytes;
        var targetBytes = (long)count * rowBytes;
        if (targetBytes > int.MaxValue)
            throw new PlateSourceException("Requested row range is too large for one buffer.");

        var target = dest.Slice(0, (int)targetBytes);
        target.Span.Clear();

        lock (_gate)
        {
            var payload = ReadAt(_offsets[0], _counts[0]);

            // Resume from the nearest checkpoint at or before y0; without one, start at row 0.
            var existing = _checkpointStore?.Load(Info.Width, Info.Height) ?? new List<G4Checkpoint>();
            var start = BestCheckpoint(existing, y0);
            var reference = start is null ? ReadOnlySpan<int>.Empty : start.Reference;

            LastDecodeStartRow = start?.Row ?? 0;
            var cursor = new CcittG4.Cursor(payload, Info.Width, start?.BitOffset ?? 0,
                                            reference, start?.Row ?? 0);

            var added = new List<G4Checkpoint>();
            var end = y0 + count;

            while (cursor.Row < end)
            {
                var row = cursor.Row;
                ct.ThrowIfCancellationRequested();

                if (!cursor.TryDecodeRow(out var transitions, out var transitionCount))
                    break;

                if (row >= y0 && row < end)
                    FillRow(target.Span.Slice((row - y0) * rowBytes, rowBytes), transitions, transitionCount);

                if (CheckpointInterval > 0 && cursor.Row % CheckpointInterval == 0 && cursor.Row < Info.Height)
                    added.Add(new G4Checkpoint(cursor.Row, cursor.BitOffset, cursor.ReferenceLine.ToArray()));
            }

            if (_checkpointStore is not null && added.Count > 0)
            {
                var merged = existing
                    .Concat(added)
                    .GroupBy(c => c.Row)
                    .Select(g => g.OrderBy(c => c.BitOffset).First())
                    .OrderBy(c => c.Row)
                    .ToList();

                _checkpointStore.Save(merged, Info.Width, Info.Height, CheckpointInterval);
            }
        }
    }

    private static G4Checkpoint? BestCheckpoint(List<G4Checkpoint> checkpoints, int row)
    {
        G4Checkpoint? best = null;
        foreach (var checkpoint in checkpoints)
            if (checkpoint.Row <= row && (best is null || checkpoint.Row > best.Row))
                best = checkpoint;

        return best;
    }

    private void ReadSingleStripRaw(int y0, int count, Memory<byte> dest, CancellationToken ct)
    {
        lock (_gate)
        {
            _singleStripBuffer ??= DecodeWholeSingleStrip(ct);

            var rowBytes = Info.RowBytes;
            var start = (long)y0 * rowBytes;
            var bytes = (long)count * rowBytes;

            if (start + bytes > _singleStripBuffer.LongLength)
                throw new PlateSourceException("Requested rows lie outside the decoded plate buffer.");
            if (bytes > int.MaxValue)
                throw new PlateSourceException("Requested row range is too large for one buffer.");

            _singleStripBuffer.AsSpan((int)start, (int)bytes)
                              .CopyTo(dest.Span.Slice(0, (int)bytes));
        }
    }

    private byte[] DecodeWholeSingleStrip(CancellationToken ct)
    {
        var rowBytes = Info.RowBytes;
        var total = (long)Info.Height * rowBytes;
        if (total > SingleStripBudget)
            throw new PlateSourceException(
                "Single-strip plate is too large to decode at once; re-RIP with strips (RowsPerStrip).");

        var target = new byte[total];
        var payload = ReadAt(_offsets[0], _counts[0]);

        if (Info.Compression is 5 or 32773)
        {
            var raw = Info.Compression == 5
                ? Lzw.Decode(payload, (int)Math.Min(int.MaxValue, total))
                : PackBits.Decode(payload, (int)Math.Min(int.MaxValue, total));
            Array.Copy(raw, target, Math.Min(raw.Length, target.Length));
        }
        else
        {
            Array.Copy(payload, target, Math.Min(payload.Length, target.Length));
        }

        NormalisePacked(target, Info.Height);
        ct.ThrowIfCancellationRequested();
        return target;
    }

    /// <summary>Write one row of G4 transitions as packed ink, honouring polarity.</summary>
    private void FillRow(byte[] target, int y, int[] transitions, int transitionCount)
    {
        var offset = (long)y * Info.RowBytes;
        if (offset < 0 || offset + Info.RowBytes > target.LongLength)
            throw new PlateSourceException($"Row {y} lies outside the decoded strip.");

        FillRow(target.AsSpan((int)offset, Info.RowBytes), transitions, transitionCount);
    }

    private void FillRow(Span<byte> row, int[] transitions, int transitionCount)
    {
        row.Clear();
        var width = Info.Width;

        if (Info.WhiteIsZero)
        {
            // Transitions mark black runs: ink = [t0,t1), [t2,t3), ...
            for (var i = 0; i < transitionCount; i += 2)
                SetBits(row, transitions[i], i + 1 < transitionCount ? transitions[i + 1] : width);
        }
        else
        {
            // BlackIsZero inverts the plate: ink is the white runs, [0,t0), [t1,t2), ...
            var position = 0;
            for (var i = 0; i < transitionCount; i += 2)
            {
                SetBits(row, position, transitions[i]);                       // white run
                position = i + 1 < transitionCount ? transitions[i + 1] : width; // skip black run
            }

            if (position < width)
                SetBits(row, position, width);                                // trailing white run
        }
    }

    private static void SetBits(Span<byte> row, int from, int to)
    {
        var limit = row.Length * 8;
        from = Math.Clamp(from, 0, limit);
        to = Math.Clamp(to, 0, limit);

        while (from < to && (from & 7) != 0)
        {
            row[from >> 3] |= (byte)(0x80 >> (from & 7));
            from++;
        }

        var fullBytes = (to - from) / 8;
        if (fullBytes > 0)
        {
            row.Slice(from >> 3, fullBytes).Fill(0xFF);
            from += fullBytes * 8;
        }

        while (from < to)
        {
            row[from >> 3] |= (byte)(0x80 >> (from & 7));
            from++;
        }
    }

    /// <summary>Apply fill-order reversal, polarity and trailing-bit masking to packed rows.</summary>
    private void NormalisePacked(byte[] buffer, int rows)
    {
        var rowBytes = Info.RowBytes;
        var usedBits = Info.Width & 7;

        for (var r = 0; r < rows; r++)
        {
            var offset = (long)r * rowBytes;
            if (offset + rowBytes > buffer.LongLength)
                throw new PlateSourceException($"Row {r} lies outside the decoded buffer.");

            var row = buffer.AsSpan((int)offset, rowBytes);

            if (_invert)
                for (var i = 0; i < row.Length; i++)
                    row[i] ^= 0xFF;

            if (_reverseBits)
                for (var i = 0; i < row.Length; i++)
                    row[i] = ReverseBits(row[i]);

            if (usedBits != 0)
                row[^1] &= (byte)((0xFF << (8 - usedBits)) & 0xFF);
        }
    }

    private static byte ReverseBits(byte value)
    {
        value = (byte)((value & 0xF0) >> 4 | (value & 0x0F) << 4);
        value = (byte)((value & 0xCC) >> 2 | (value & 0x33) << 2);
        return (byte)((value & 0xAA) >> 1 | (value & 0x55) << 1);
    }

    private byte[] ReadAt(ulong offset, int count)
    {
        if (count <= 0)
            return Array.Empty<byte>();
        if (offset + (ulong)count > (ulong)_fileLength)
            throw new PlateSourceException(
                $"Strip at {offset} (+{count}) lies beyond end of file; the RIP may still be writing.");

        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = RandomAccess.Read(_stream.SafeFileHandle, buffer.AsSpan(read), (long)offset + read);
            if (n <= 0)
                throw new PlateSourceException("Unexpected end of file while reading a strip.");
            read += n;
        }

        return buffer;
    }

    public void Dispose()
    {
        var close = false;
        lock (_gate)
        {
            if (_disposeRequested)
                return;

            _disposeRequested = true;
            close = _activeReads == 0;
        }

        if (close)
            CloseResources();
    }

    private void CloseResources()
    {
        lock (_gate)
        {
            _stream.Dispose();
            _strips.Clear();
            _lru.Clear();
            _cachedBytes = 0;
            _singleStripBuffer = null;
        }
    }

    private sealed class StopDecoding : Exception
    {
        public static readonly StopDecoding Instance = new();
        private StopDecoding() { }
    }

    // ---------------------------------------------------------------- IFD parsing

    private sealed class Directory
    {
        private readonly Dictionary<ushort, double[]> _numbers = new();
        private readonly Dictionary<ushort, ulong[]> _integers = new();

        public bool IsBigTiff { get; init; }

        public bool Has(ushort tag) => _numbers.ContainsKey(tag) || _integers.ContainsKey(tag);

        public double Scalar(ushort tag, double fallback)
        {
            if (_integers.TryGetValue(tag, out var ints) && ints.Length > 0)
                return ints[0];
            if (_numbers.TryGetValue(tag, out var values) && values.Length > 0)
                return values[0];
            return fallback;
        }

        public ulong[] Integers(ushort tag) =>
            _integers.TryGetValue(tag, out var values) ? values : Array.Empty<ulong>();

        public void Add(ushort tag, ulong[] values) => _integers[tag] = values;

        public void Add(ushort tag, double[] values) => _numbers[tag] = values;
    }

    private static Directory ReadDirectory(FileStream stream, long fileLength, int pageIndex)
    {
        Span<byte> header = stackalloc byte[16];
        stream.Seek(0, SeekOrigin.Begin);
        stream.ReadExactly(header);

        var little = header[0] switch
        {
            (byte)'I' => true,
            (byte)'M' => false,
            _ => throw new PlateSourceException("Not a TIFF file (bad byte-order mark).")
        };

        var version = ReadU16(header[2..], little);
        if (version is not (42 or 43))
            throw new PlateSourceException($"Unsupported TIFF version {version}.");

        var big = version == 43;
        var directory = new Directory { IsBigTiff = big };

        ulong ifdOffset = big ? ReadU64(header[8..], little) : ReadU32(header[4..], little);
        for (var page = 0; page < pageIndex; page++)
        {
            if (ifdOffset == 0)
                throw new PlateSourceException($"TIFF has no page {pageIndex}.");
            ifdOffset = ReadNextIfd(stream, ifdOffset, big, little, fileLength);
        }

        if (ifdOffset == 0 || (long)ifdOffset >= fileLength)
            throw new PlateSourceException($"TIFF has no page {pageIndex}.");

        var countSize = big ? 8 : 2;
        var entrySize = big ? 20 : 12;
        var inlineSize = big ? 8 : 4;

        Span<byte> countBytes = stackalloc byte[8];
        stream.Seek((long)ifdOffset, SeekOrigin.Begin);
        stream.ReadExactly(countBytes[..countSize]);
        var entryCount = big ? ReadU64(countBytes, little) : ReadU16(countBytes, little);

        var maxEntries = (fileLength - (long)ifdOffset - countSize) / entrySize;
        if ((long)entryCount > maxEntries || entryCount > 4096)
            throw new PlateSourceException("IFD entry count is implausible; header looks corrupt.");

        var entries = new byte[(int)entryCount * entrySize];
        stream.ReadExactly(entries);

        for (var i = 0; i < (int)entryCount; i++)
        {
            var entry = entries.AsSpan(i * entrySize, entrySize);
            var tag = ReadU16(entry, little);
            var type = ReadU16(entry[2..], little);
            var count = big ? ReadU64(entry[4..], little) : ReadU32(entry[4..], little);

            var typeSize = TypeSize(type);
            if (typeSize == 0)
                continue;

            var total = count * (ulong)typeSize;
            if (total == 0 || total > 64UL * 1024 * 1024)
                continue;

            byte[] raw;
            if (total <= (ulong)inlineSize)
            {
                raw = entry.Slice(entrySize - inlineSize, (int)total).ToArray();
            }
            else
            {
                var valueOffset = big ? ReadU64(entry.Slice(entrySize - inlineSize), little)
                                      : ReadU32(entry.Slice(entrySize - inlineSize), little);

                if (valueOffset + total > (ulong)fileLength)
                    continue; // value beyond EOF: treat as missing (truncated file)

                raw = new byte[total];
                var position = stream.Position;
                stream.Seek((long)valueOffset, SeekOrigin.Begin);
                stream.ReadExactly(raw);
                stream.Seek(position, SeekOrigin.Begin);
            }

            if (!TryDecode(type, raw, (int)count, little, out var numbers, out var integers))
                continue;

            if (integers is not null)
                directory.Add(tag, integers);
            else if (numbers is not null)
                directory.Add(tag, numbers);
        }

        return directory;
    }

    private static ulong ReadNextIfd(FileStream stream, ulong ifdOffset, bool big, bool little, long fileLength)
    {
        var countSize = big ? 8 : 2;
        var entrySize = big ? 20 : 12;

        Span<byte> countBytes = stackalloc byte[8];
        stream.Seek((long)ifdOffset, SeekOrigin.Begin);
        stream.ReadExactly(countBytes[..countSize]);
        var entryCount = big ? ReadU64(countBytes, little) : ReadU16(countBytes, little);

        var pointerOffset = (long)ifdOffset + countSize + (long)entryCount * entrySize;
        if (pointerOffset + (big ? 8 : 4) > fileLength)
            return 0;

        Span<byte> pointer = stackalloc byte[8];
        stream.Seek(pointerOffset, SeekOrigin.Begin);
        stream.ReadExactly(pointer[..(big ? 8 : 4)]);
        return big ? ReadU64(pointer, little) : ReadU32(pointer, little);
    }

    private static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 => 4,
        5 or 10 or 12 or 16 or 17 or 18 => 8,
        _ => 0
    };

    private static bool TryDecode(ushort type, byte[] raw, int count, bool little,
                                  out double[]? numbers, out ulong[]? integers)
    {
        numbers = null;
        integers = null;

        switch (type)
        {
            case 3:
            case 4:
            case 16:
            {
                var values = new ulong[count];
                for (var i = 0; i < count; i++)
                {
                    values[i] = type switch
                    {
                        3 => ReadU16(raw.AsSpan(i * 2), little),
                        4 => ReadU32(raw.AsSpan(i * 4), little),
                        _ => ReadU64(raw.AsSpan(i * 8), little)
                    };
                }

                integers = values;
                return true;
            }

            case 5:
            {
                var values = new double[count];
                for (var i = 0; i < count; i++)
                {
                    var numerator = ReadU32(raw.AsSpan(i * 8), little);
                    var denominator = ReadU32(raw.AsSpan(i * 8 + 4), little);
                    values[i] = denominator == 0 ? 0 : (double)numerator / denominator;
                }

                numbers = values;
                return true;
            }

            case 1:
            case 7:
            {
                var values = new ulong[count];
                for (var i = 0; i < count; i++)
                    values[i] = raw[i];
                integers = values;
                return true;
            }

            default:
                return false;
        }
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(data) : BinaryPrimitives.ReadUInt16BigEndian(data);

    private static uint ReadU32(ReadOnlySpan<byte> data, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(data) : BinaryPrimitives.ReadUInt32BigEndian(data);

    private static ulong ReadU64(ReadOnlySpan<byte> data, bool little) =>
        little ? BinaryPrimitives.ReadUInt64LittleEndian(data) : BinaryPrimitives.ReadUInt64BigEndian(data);
}
