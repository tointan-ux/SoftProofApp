using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SoftProof.Core.Models;

namespace SoftProof.RIPParsers.Raster.Pyramid;

/// <summary>
/// Disk cache for preview pyramids. Decoding a 7 Gpx plate takes seconds; the pyramid is stored
/// next to a hash of (path, size, mtime) so reopening the same job is a file read.
/// <para>
/// Layout: magic "SPPF", version, source identity, level table, then per-level Deflate payload.
/// Writes go to a temp file and are moved into place, so a cancelled or crashed build never
/// leaves a half-written cache that a later run would trust.
/// </para>
/// </summary>
public sealed class PyramidCache
{
    private static readonly byte[] Magic = { (byte)'S', (byte)'P', (byte)'P', (byte)'F' };
    private const ushort Version = 1;

    private readonly string _root;
    private readonly long _maxBytes;

    public PyramidCache(string? rootDirectory = null, long maxBytes = 512L * 1024 * 1024)
    {
        _root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoftProof", "pyramid");
        _maxBytes = maxBytes;
    }

    public string RootDirectory => _root;

    /// <summary>Cache key for a source plate: path, size, mtime, page and preview budget.</summary>
    public static string ComputeKey(string filePath, int pageIndex, int maxPixels)
    {
        var info = new FileInfo(filePath);
        var identity = string.Join('|',
            Path.GetFullPath(filePath).ToLowerInvariant(),
            info.Length,
            info.LastWriteTimeUtc.Ticks,
            pageIndex,
            maxPixels,
            Version);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    public string PathForKey(string key) => Path.Combine(_root, key[..2], key + ".sppf");

    public bool TryGet(string filePath, int pageIndex, int maxPixels, out PlatePyramid? pyramid)
    {
        pyramid = null;
        var path = PathForKey(ComputeKey(filePath, pageIndex, maxPixels));
        if (!File.Exists(path))
            return false;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            var magic = reader.ReadBytes(4);
            if (magic.Length != 4 || !magic.AsSpan().SequenceEqual(Magic))
                return false;

            if (reader.ReadUInt16() != Version)
                return false;

            var levelCount = reader.ReadUInt16();
            if (levelCount == 0 || levelCount > 32)
                return false;

            var levels = new List<PyramidLevel>(levelCount);
            for (var i = 0; i < levelCount; i++)
            {
                var width = reader.ReadInt32();
                var height = reader.ReadInt32();
                var factor = reader.ReadInt32();
                var length = reader.ReadInt32();

                if (width <= 0 || height <= 0 || factor <= 0 || length <= 0)
                    return false;

                var expected = (long)width * height;
                if (length != expected || length > int.MaxValue)
                    return false;

                var compressed = reader.ReadBytes(reader.ReadInt32());
                var coverage = Inflate(compressed, (int)expected);
                levels.Add(new PyramidLevel(width, height, factor, coverage));
            }

            pyramid = PlatePyramid.FromLevels(levels);
            return true;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException
                                       or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Put(string filePath, int pageIndex, int maxPixels, PlatePyramid pyramid)
    {
        var path = PathForKey(ComputeKey(filePath, pageIndex, maxPixels));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write((ushort)pyramid.Levels.Count);

                foreach (var level in pyramid.Levels)
                {
                    var compressed = Deflate(level.Coverage);
                    writer.Write(level.Width);
                    writer.Write(level.Height);
                    writer.Write(level.Factor);
                    writer.Write(level.Coverage.Length);
                    writer.Write(compressed.Length);
                    writer.Write(compressed);
                }
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (IOException) { /* best effort */ }
            }
        }

        TrimToBudget();
    }

    private void TrimToBudget()
    {
        try
        {
            if (!Directory.Exists(_root))
                return;

            var files = new DirectoryInfo(_root)
                .EnumerateFiles("*.sppf", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            long total = 0;
            foreach (var file in files)
            {
                total += file.Length;
                if (total <= _maxBytes)
                    continue;

                try { file.Delete(); } catch (IOException) { /* in use */ }
            }
        }
        catch (IOException)
        {
            // cache maintenance must never fail a load
        }
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(data);
        return output.ToArray();
    }

    private static byte[] Inflate(byte[] compressed, int expected)
    {
        using var input = new MemoryStream(compressed);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        var output = new MemoryStream(expected);
        deflate.CopyTo(output);
        var data = output.ToArray();
        if (data.Length != expected)
            throw new InvalidDataException("Pyramid level length does not match its header.");
        return data;
    }
}
