using System.Text.RegularExpressions;
using SoftProof.Core.Models;

namespace SoftProof.RIPParsers;

/// <summary>
/// Recovers plate identity from the places prepress files actually carry it: filename
/// suffixes (<c>job_C.tif</c>, <c>job.Cyan.tif</c>, <c>job(c)01.tif</c>), TIFF ImageDescription
/// or page names, and a description like "… Magenta …".
/// </summary>
public static partial class SeparationNaming
{
    private static readonly (string Token, string Name, SeparationColor Color)[] Known =
    {
        ("cyan", "Cyan", SeparationColor.ProcessCyan),
        ("magenta", "Magenta", SeparationColor.ProcessMagenta),
        ("yellow", "Yellow", SeparationColor.ProcessYellow),
        ("black", "Black", SeparationColor.ProcessBlack),
        ("key", "Black", SeparationColor.ProcessBlack),
        ("c", "Cyan", SeparationColor.ProcessCyan),
        ("m", "Magenta", SeparationColor.ProcessMagenta),
        ("y", "Yellow", SeparationColor.ProcessYellow),
        ("k", "Black", SeparationColor.ProcessBlack),
        ("orange", "Orange", SeparationColor.Orange),
        ("green", "Green", SeparationColor.Green),
        ("violet", "Violet", SeparationColor.Violet),
        ("purple", "Violet", SeparationColor.Violet),
        ("red", "Red", SeparationColor.Red),
        ("blue", "Blue", SeparationColor.Blue),
        ("white", "White", SeparationColor.White),
        ("varnish", "Varnish", SeparationColor.Varnish),
        ("coating", "Varnish", SeparationColor.Varnish),
        ("primer", "Primer", SeparationColor.Primer)
    };

    [GeneratedRegex(@"[._\-\s()\[\]{},;]+")]
    private static partial Regex TokenSeparator();

    /// <summary>
    /// Canonical separation name for a plate file. Prefers an explicit colour token in the
    /// description/page name, then in the filename; otherwise returns the file's base name.
    /// </summary>
    public static string ResolveName(string filePath, params string?[] extraHints)
    {
        foreach (var hint in extraHints)
        {
            var fromHint = FindColor(hint);
            if (fromHint is not null)
                return fromHint.Value.Name;
        }

        var baseName = Path.GetFileNameWithoutExtension(filePath);
        var fromFile = FindColor(baseName);
        return fromFile?.Name ?? baseName;
    }

    /// <summary>Colour implied by a file name (used for spot/process classification).</summary>
    public static SeparationColor? ResolveColor(string filePath, params string?[] extraHints)
    {
        foreach (var hint in extraHints)
        {
            var fromHint = FindColor(hint);
            if (fromHint is not null)
                return fromHint.Value.Color;
        }

        return FindColor(Path.GetFileNameWithoutExtension(filePath))?.Color;
    }

    private static (string Name, SeparationColor Color)? FindColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var lower = text.ToLowerInvariant();

        // Whole-word tokens first ("job_C01", "job (M).tif", "job.Cyan.tif").
        foreach (var token in TokenSeparator().Split(lower))
        {
            var match = MatchToken(token);
            if (match is not null)
                return match;
        }

        // Fall back to a substring scan ("Ace Kid … Magenta …").
        foreach (var entry in Known)
        {
            if (entry.Token.Length > 1 && lower.Contains(entry.Token, StringComparison.Ordinal))
                return (entry.Name, entry.Color);
        }

        return null;
    }

    private static (string Name, SeparationColor Color)? MatchToken(string token)
    {
        if (token.Length == 0)
            return null;

        // Strip a trailing/leading ordinal: "c01" -> "c", "1c" -> "c".
        var trimmed = token.Trim('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (trimmed.Length == 0)
            return null;

        foreach (var entry in Known)
        {
            if (trimmed == entry.Token)
                return (entry.Name, entry.Color);
        }

        return null;
    }
}
