namespace SoftProof.Core.Services;

/// <summary>
/// Watches a single job file and raises <see cref="FileChanged"/> once the RIP has finished
/// rewriting it. Changes are debounced, because a RIP typically writes the file in several
/// bursts and truncates it before writing.
/// </summary>
public sealed class FileWatchService : IDisposable
{
    private readonly TimeSpan _debounce;
    private FileSystemWatcher? _watcher;
    private string? _path;
    private CancellationTokenSource? _pending;
    private readonly object _gate = new();

    public FileWatchService(TimeSpan? debounce = null) =>
        _debounce = debounce ?? TimeSpan.FromMilliseconds(750);

    /// <summary>Raised on a worker thread once the file has settled.</summary>
    public event EventHandler<string>? FileChanged;

    public string? WatchedPath => _path;

    /// <summary>Watch one file (its directory is watched; other files are ignored).</summary>
    public void Watch(string filePath)
    {
        Stop();

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;

        _path = Path.GetFullPath(filePath);

        _watcher = new FileSystemWatcher(directory)
        {
            Filter = Path.GetFileName(_path),
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Renamed += OnChanged;
    }

    public void Stop()
    {
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;

            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnChanged;
                _watcher.Created -= OnChanged;
                _watcher.Renamed -= OnChanged;
                _watcher.Dispose();
                _watcher = null;
            }

            _path = null;
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        var path = _path;
        if (path is null || !string.Equals(Path.GetFullPath(e.FullPath), path, StringComparison.OrdinalIgnoreCase))
            return;

        CancellationTokenSource cts;
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            cts = _pending = new CancellationTokenSource();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounce, cts.Token).ConfigureAwait(false);

                // The file must be readable and complete: a RIP often truncates then rewrites.
                if (!IsSettled(path))
                    return;

                FileChanged?.Invoke(this, path);
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer write
            }
        });
    }

    private static bool IsSettled(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose() => Stop();
}
