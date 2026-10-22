namespace SoftProof.Core.Services;

/// <summary>
/// Watches RIP output folders for new/renamed/changed files and raises
/// <see cref="FileDetected"/> once a file has stopped growing (a RIP writes in bursts).
/// </summary>
public sealed class FolderWatchService : IDisposable
{
    private readonly TimeSpan _debounce;
    private readonly string[] _extensions;
    private readonly object _gate = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);

    public FolderWatchService(TimeSpan? debounce = null, IEnumerable<string>? extensions = null)
    {
        _debounce = debounce ?? TimeSpan.FromMilliseconds(750);
        _extensions = (extensions ?? RipQueueService.DefaultExtensions)
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .Select(e => e.ToLowerInvariant())
            .Distinct()
            .ToArray();
    }

    /// <summary>Raised on a worker thread after a watched file has settled.</summary>
    public event EventHandler<string>? FileDetected;

    /// <summary>Replace the watched folder set (the queue's folders may change on open).</summary>
    public void Watch(IEnumerable<string> folders)
    {
        lock (_gate)
        {
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= OnChanged;
                watcher.Changed -= OnChanged;
                watcher.Renamed -= OnChanged;
                watcher.Dispose();
            }

            _watchers.Clear();

            foreach (var folder in folders
                         .Where(f => !string.IsNullOrWhiteSpace(f))
                         .Select(f => f.Trim())
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(folder))
                    continue;

                var watcher = new FileSystemWatcher(folder)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };

                watcher.Created += OnChanged;
                watcher.Changed += OnChanged;
                watcher.Renamed += OnChanged;
                _watchers.Add(watcher);
            }
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        var path = e.FullPath;
        if (!_extensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            return;

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_pending.TryGetValue(path, out var previous))
                previous.Cancel();

            cts = new CancellationTokenSource();
            _pending[path] = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounce, cts.Token).ConfigureAwait(false);
                if (!IsSettled(path))
                    return;

                FileDetected?.Invoke(this, path);
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer write to the same file
            }
            finally
            {
                lock (_gate)
                {
                    if (_pending.TryGetValue(path, out var current) && ReferenceEquals(current, cts))
                        _pending.Remove(path);
                }

                cts.Dispose();
            }
        });
    }

    private static bool IsSettled(string path)
    {
        try
        {
            // Must be non-empty and readable while the RIP holds it open for writing.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= 0)
                return false;

            // A RIP often truncates then rewrites: require a short period of stable size.
            var length = stream.Length;
            Thread.Sleep(150);
            return new FileInfo(path).Length == length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }

            _watchers.Clear();

            foreach (var cts in _pending.Values)
                cts.Cancel();
            _pending.Clear();
        }
    }
}
