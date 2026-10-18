using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SoftProof.RIPParsers.Raster.Tiff;

/// <summary>
/// A resumable position inside a single-strip G4 stream: the row it starts at, the bit offset,
/// and the reference line (row - 1 transitions) needed to decode the next row correctly.
/// </summary>
public sealed record G4Checkpoint(int Row, long BitOffset, int[] Reference);

/// <summary>
/// Sidecar file holding G4 checkpoints for one plate, stored next to the pyramid cache and
/// keyed by (path, length, mtime, page). A rewritten plate invalidates the file by identity, so
/// a RIP overwrite can never make us resume in the wrong stream.
/// </summary>
public sealed class G4CheckpointStore
{
    private static readonly byte[] Magic = { (byte)'S', (byte)'P', (byte)'G', (byte)'4' };
    private const ushort Version = 1;

    private readonly string _path;
    private readonly long _fileLength;
    private readonly long _modifiedTicks;

    public G4CheckpointStore(string platePath, int pageIndex, string? rootDirectory = null)
    {
        var info = new FileInfo(platePath);
        _fileLength = info.Length;
        _modifiedTicks = info.LastWriteTimeUtc.Ticks;

        var root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoftProof", "g4checkpoints");

        var identity = string.Join('|',
            Path.GetFullPath(platePath).ToLowerInvariant(),
            _fileLength,
            _modifiedTicks,
            pageIndex,
            Version);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var key = Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
        _path = Path.Combine(root, key[..2], key + ".g4ck");
    }

    public string FilePath => _path;

    public List<G4Checkpoint> Load(int width, int height)
    {
        var empty = new List<G4Checkpoint>();
        if (!File.Exists(_path))
            return empty;

        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            if (!reader.ReadBytes(4).AsSpan().SequenceEqual(Magic))
                return empty;
            if (reader.ReadUInt16() != Version)
                return empty;
            if (reader.ReadInt64() != _fileLength || reader.ReadInt64() != _modifiedTicks)
                return empty;
            if (reader.ReadInt32() != width || reader.ReadInt32() != height)
                return empty;

            reader.ReadInt32(); // interval: informational
            var count = reader.ReadInt32();
            if (count < 0 || count > 200_000)
                return empty;

            var checkpoints = new List<G4Checkpoint>(count);
            for (var i = 0; i < count; i++)
            {
                var row = reader.ReadInt32();
                var bitOffset = reader.ReadInt64();
                var referenceCount = reader.ReadInt32();

                if (referenceCount < 0 || referenceCount > width + 2)
                    return empty;

                var reference = new int[referenceCount];
                for (var j = 0; j < referenceCount; j++)
                    reference[j] = reader.ReadInt32();

                if (row > 0 && row < height && bitOffset >= 0)
                    checkpoints.Add(new G4Checkpoint(row, bitOffset, reference));
            }

            return checkpoints;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return empty;
        }
    }

    public void Save(IReadOnlyList<G4Checkpoint> checkpoints, int width, int height, int interval)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(directory))
                return;

            Directory.CreateDirectory(directory);
            var temp = _path + ".tmp";

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(_fileLength);
                writer.Write(_modifiedTicks);
                writer.Write(width);
                writer.Write(height);
                writer.Write(interval);
                writer.Write(checkpoints.Count);

                foreach (var checkpoint in checkpoints)
                {
                    writer.Write(checkpoint.Row);
                    writer.Write(checkpoint.BitOffset);
                    writer.Write(checkpoint.Reference.Length);
                    foreach (var transition in checkpoint.Reference)
                        writer.Write(transition);
                }
            }

            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The sidecar is an optimisation: failing to write it must never fail a read.
        }
    }
}
