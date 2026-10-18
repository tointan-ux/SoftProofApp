using System.Text.RegularExpressions;

namespace SoftProof.Core.Services;

/// <summary>One job file found in a watched RIP output folder.</summary>
public sealed record RipQueueEntry(
    string Path,
    string Name,
    string Folder,
    long SizeBytes,
    DateTime ModifiedUtc)
{
    private static readonly Regex SeparationSuffix = new(
        @"[ _\-.]*(\([^)]*\)|\[[^\]]*\])\s*\d*\s*$|[ _\-.]*(cyan|magenta|yellow|black|white|varnish|c|m|y|k)\s*\d*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string SizeText => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / (1024.0 * 1024.0):0.0} MB"
        : $"{Math.Max(1, SizeBytes / 1024)} KB";

    public string ModifiedText => ModifiedUtc.ToLocalTime().ToString("HH:mm:ss");

    /// <summary>True when this file is the job currently open in the viewer.</summary>
    public bool IsLoaded { get; set; }

    /// <summary>Job name with the trailing separation token removed, used to group the queue.</summary>
    public string JobName
    {
        get
        {
            var stripped = SeparationSuffix.Replace(Name, string.Empty).Trim();
            return string.IsNullOrWhiteSpace(stripped) ? Name : stripped;
        }
    }

    /// <summary>Single-letter separation chip for the list (C, M, Y, K, or the ink's initial).</summary>
    public string Chip
    {
        get
        {
            var match = Regex.Match(Name,
                @"(cyan|magenta|yellow|black|white|varnish)|[._\-\s]([cmyk])\d{0,3}\s*$",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                return "?";

            var token = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
                        .ToLowerInvariant();

            return token switch
            {
                "cyan" => "C",
                "magenta" => "M",
                "yellow" => "Y",
                "black" => "K",
                "white" => "W",
                "varnish" => "V",
                _ => token.ToUpperInvariant()
            };
        }
    }

    /// <summary>Middle-truncated name: RIP job prefixes are identical, the tail is what differs.</summary>
    public string DisplayName
    {
        get
        {
            const int max = 34;
            if (Name.Length <= max)
                return Name;

            const int head = 15;
            const int tail = 16;
            return Name[..head] + "…" + Name[^tail..];
        }
    }
}

/// <summary>
/// Scans one or more RIP output folders ("network RIP queue"). Read-only and deliberately
/// shallow: RIP drop folders are flat, and recursing into a press archive is never what you want.
/// </summary>
public sealed class RipQueueService
{
    public static readonly string[] DefaultExtensions = { ".tif", ".tiff", ".pbuf", ".hqn", ".hpb", ".len", ".lenx" };

    private readonly string[] _extensions;

    public RipQueueService(IEnumerable<string> folders, IEnumerable<string>? extensions = null)
    {
        Folders = folders
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _extensions = (extensions ?? DefaultExtensions)
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .Select(e => e.ToLowerInvariant())
            .Distinct()
            .ToArray();
    }

    public IReadOnlyList<string> Folders { get; }

    /// <summary>
    /// Newest first, at most <paramref name="maxPerFolder"/> per folder. Missing folders and
    /// unreadable files are skipped, never thrown: a queue view must survive an offline RIP.
    /// </summary>
    public IReadOnlyList<RipQueueEntry> Scan(int maxPerFolder = 250)
    {
        var entries = new List<RipQueueEntry>();

        foreach (var folder in Folders)
        {
            if (!Directory.Exists(folder))
                continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var matched = new List<RipQueueEntry>();
            foreach (var file in files)
            {
                if (!_extensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                    continue;

                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                    if (!info.Exists)
                        continue;
                }
                catch (IOException)
                {
                    continue;
                }

                matched.Add(new RipQueueEntry(file, Path.GetFileNameWithoutExtension(file),
                                              folder, info.Length, info.LastWriteTimeUtc));
            }

            entries.AddRange(matched
                .OrderByDescending(e => e.ModifiedUtc)
                .Take(Math.Max(1, maxPerFolder)));
        }

        return entries;
    }

    /// <summary>
    /// Folders for the queue view: <c>SOFTPROOF_QUEUE_FOLDERS</c> (semicolon separated), plus the
    /// folder of the job currently open, so a drop folder appears as soon as it is used once.
    /// </summary>
    public static IReadOnlyList<string> FoldersFromEnvironment(string? currentJobPath = null)
    {
        var folders = new List<string>();

        var configured = Environment.GetEnvironmentVariable("SOFTPROOF_QUEUE_FOLDERS");
        if (!string.IsNullOrWhiteSpace(configured))
            folders.AddRange(configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (!string.IsNullOrWhiteSpace(currentJobPath))
        {
            var directory = Path.GetDirectoryName(currentJobPath);
            if (!string.IsNullOrWhiteSpace(directory))
                folders.Add(directory);
        }

        if (folders.Count == 0)
        {
            var drop = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SoftProofQueue");
            folders.Add(drop);
        }

        return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
