using System.Text.RegularExpressions;

namespace SoftProof.RIPParsers.Raster;

/// <summary>
/// Metadata known from the file header alone, before any pixel is decoded.
/// </summary>
public sealed record PlateInfo
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Native resolution from the TIFF resolution tags; 0 when absent or zero.</summary>
    public double DpiX { get; init; }
    public double DpiY { get; init; }

    /// <summary>TIFF photometric 0: sample 0 is white, so a set bit is ink.</summary>
    public bool WhiteIsZero { get; init; }

    /// <summary>Best guess from the file name; null when nothing matched.</summary>
    public string? SeparationHint { get; init; }

    public int PageIndex { get; init; }
    public int Compression { get; init; }
    public int RowsPerStrip { get; init; }
    public int StripCount { get; init; }
    public bool IsBigTiff { get; init; }
    public string FilePath { get; init; } = string.Empty;

    public int RowBytes => (Width + 7) / 8;
}

/// <summary>
/// A plate's pixels, addressable by row. Implementations normalise storage so that rows are
/// 1 bit per pixel, MSB first, and <b>1 always means ink</b>. Polarity, compression and byte
/// order are the source's problem, never the consumer's.
/// </summary>
public interface IPlateSource : IDisposable
{
    PlateInfo Info { get; }

    /// <summary>False while a RIP is still writing the file (strips beyond end of file).</summary>
    bool IsComplete { get; }

    /// <summary>
    /// Fill <paramref name="dest"/> with <paramref name="count"/> packed rows starting at
    /// <paramref name="y0"/>. <c>dest.Length</c> must be at least <c>count * Info.RowBytes</c>.
    /// </summary>
    ValueTask ReadRowsAsync(int y0, int count, Memory<byte> dest, CancellationToken ct = default);
}

/// <summary>Raised when a plate cannot be decoded at all (bad header, unsupported layout).</summary>
public sealed class PlateSourceException : Exception
{
    public PlateSourceException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Separation names from file names. Real RIP output is depressingly creative; this covers the
/// conventions seen in practice without pretending to be exhaustive (manual override stays).
/// </summary>
public static class SeparationNaming
{
    private static readonly Dictionary<string, string> Letters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["C"] = "Cyan", ["M"] = "Magenta", ["Y"] = "Yellow", ["K"] = "Black",
        ["W"] = "White", ["V"] = "Varnish", ["O"] = "Orange", ["G"] = "Green"
    };

    private static readonly Regex[] Patterns =
    {
        // (C)01, (Cutting)00, (Spot Blue)12
        new(@"\(([A-Za-z][A-Za-z0-9 _\-]{0,18})\)\s*\d{0,3}$", RegexOptions.Compiled),
        // .Cyan, _Magenta, -K, .Cutting001
        new(@"[._\-]([A-Za-z]{1,18})\s*\d{0,3}$", RegexOptions.Compiled)
    };

    public static string? Parse(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(name))
            return null;

        foreach (var pattern in Patterns)
        {
            var match = pattern.Match(name);
            if (!match.Success)
                continue;

            var token = match.Groups[1].Value.Trim();
            if (token.Length == 0)
                continue;

            if (Letters.TryGetValue(token, out var canonical))
                return canonical;

            // Ignore tokens that are clearly part of a job code rather than an ink name.
            if (token.Length <= 2)
                continue;

            return char.ToUpperInvariant(token[0]) + token[1..];
        }

        return null;
    }
}
