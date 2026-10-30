using System;
using System.IO;
using System.Text;

namespace SoftProof.Core.Utilities;

/// <summary>
/// Turns arbitrary text (a job name, a separation name) into something Windows accepts as a file
/// name. Replacing invalid characters is not enough: reserved device names, control characters,
/// trailing dots and over-long names all fail at write time, after the user has picked a folder.
/// </summary>
public static class SafeFileName
{
    public const int DefaultMaxLength = 80;

    private static readonly string[] ReservedStems =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>Sanitises <paramref name="name"/>, falling back to <paramref name="fallback"/>.</summary>
    public static string For(string? name, string fallback = "job", int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrWhiteSpace(name))
            return fallback;

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);

        foreach (var c in name)
            builder.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);

        var clean = builder.ToString().Trim().TrimEnd('.', ' ');

        if (maxLength > 0 && clean.Length > maxLength)
            clean = clean[..maxLength].TrimEnd('.', ' ');

        // Nothing but replacement characters ("///" -> "___") is not a usable name either.
        if (clean.Length == 0 || clean.Trim('_').Trim().Length == 0)
            return fallback;

        // CON, PRN, NUL, COM1… are device names with or without an extension: "CON-report.html"
        // still opens the console device, so prefix rather than reject.
        var stem = clean.Split('.')[0].TrimEnd();

        foreach (var reserved in ReservedStems)
        {
            if (stem.Equals(reserved, StringComparison.OrdinalIgnoreCase))
                return "_" + clean;
        }

        return clean;
    }
}
