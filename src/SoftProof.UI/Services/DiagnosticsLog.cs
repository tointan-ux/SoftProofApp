using System.Collections.Concurrent;
using System.IO;

namespace SoftProof.UI.Services;

/// <summary>
/// Small ring buffer + append-only file for crash forensics. Native crashes never reach the
/// managed handlers, so the last few hundred view/tile events must already be on disk.
/// </summary>
public static class DiagnosticsLog
{
    private const int Capacity = 200;
    private const long MaxFileBytes = 2 * 1024 * 1024;

    private static readonly ConcurrentQueue<string> Ring = new();
    private static readonly object Gate = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoftProof", "diagnostics.log");

    public static void Info(string message) => Add("INFO", message);

    public static void Error(string message, Exception? exception = null)
    {
        var detail = exception is null
            ? message
            : $"{message} | {exception.GetType().Name}: {exception.Message}";
        Add("ERROR", detail);
    }

    private static void Add(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level} {message} " +
                   $"| ws={Environment.WorkingSet / (1024 * 1024)}MB";

        Ring.Enqueue(line);
        while (Ring.Count > Capacity && Ring.TryDequeue(out _))
        {
        }

        lock (Gate)
        {
            try
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxFileBytes)
                    File.Move(path, path + ".1", overwrite: true);

                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // Diagnostics must never break the app.
            }
        }
    }

    public static string Tail() => string.Join(Environment.NewLine, Ring);
}
