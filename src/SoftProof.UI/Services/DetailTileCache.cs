using System.Windows.Media.Imaging;

namespace SoftProof.UI.Services;

/// <summary>
/// LRU cache of composited native-resolution tiles. Tiles are aligned to a fixed grid so
/// panning reuses everything that was already decoded; the cache is cleared whenever the
/// plate state (visibility, colour, opacity, view mode, substrate) changes.
/// </summary>
public sealed class DetailTileCache
{
    public const int TileSize = 512;

    private sealed record Entry(long Key, BitmapSource Bitmap, int Bytes);

    private readonly object _gate = new();
    private readonly long _budgetBytes;
    private readonly Dictionary<long, LinkedListNode<Entry>> _map = new();
    private readonly LinkedList<Entry> _lru = new();
    private long _bytes;

    public DetailTileCache(long budgetBytes = 128L * 1024 * 1024)
    {
        _budgetBytes = budgetBytes;
    }

    private static long Key(int source, int level, int x, int y) =>
        ((long)source * 2654435761L) ^ ((long)level << 56) ^ ((long)x << 28) ^ (uint)y;

    public bool TryGet(int source, int level, int x, int y, out BitmapSource bitmap)
    {
        // Decoding runs on worker threads while the UI thread clears the cache.
        lock (_gate)
        {
            if (_map.TryGetValue(Key(source, level, x, y), out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                bitmap = node.Value.Bitmap;
                return true;
            }
        }

        bitmap = null!;
        return false;
    }

    public void Put(int source, int level, int x, int y, BitmapSource bitmap)
    {
        var bytes = bitmap.PixelWidth * bitmap.PixelHeight * 4;
        if (bytes > _budgetBytes)
            return;

        lock (_gate)
        {
            PutLocked(source, level, x, y, bitmap, bytes);
        }
    }

    private void PutLocked(int source, int level, int x, int y, BitmapSource bitmap, int bytes)
    {
        var key = Key(source, level, x, y);
        if (_map.Remove(key, out var old))
        {
            _lru.Remove(old);
            _bytes -= old.Value.Bytes;
        }

        var node = _lru.AddFirst(new Entry(key, bitmap, bytes));
        _map[key] = node;
        _bytes += bytes;

        while (_bytes > _budgetBytes && _lru.Last is { } last)
        {
            _lru.RemoveLast();
            _map.Remove(last.Value.Key);
            _bytes -= last.Value.Bytes;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _lru.Clear();
            _bytes = 0;
        }
    }
}
