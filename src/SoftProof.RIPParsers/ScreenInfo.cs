using System.Globalization;
using System.Text.RegularExpressions;

namespace SoftProof.RIPParsers;

/// <summary>
/// Reads screening data out of the free text RIPs actually emit, such as a TIFF
/// ImageDescription or page name: <c>"Ace Kid 175lpi 15deg"</c>, <c>"AM 175 LPI, 15°"</c>.
/// <para>
/// Plates that carry no such text keep zero screening values, which makes the screen-angle
/// preflight report "not run" instead of inventing an answer.
/// </para>
/// </summary>
public static partial class ScreenInfo
{
    [GeneratedRegex(@"(\d{2,3}(?:[.,]\d+)?)\s*(?:lpi|lines?\s*per\s*inch)", RegexOptions.IgnoreCase)]
    private static partial Regex LpiPattern();

    [GeneratedRegex(@"(\d{1,3}(?:[.,]\d+)?)\s*(?:°|deg(?:rees)?\b)", RegexOptions.IgnoreCase)]
    private static partial Regex AnglePattern();

    /// <summary>
    /// Extracts screen frequency (lpi) and angle (degrees) from <paramref name="text"/>.
    /// Returns false when neither is present; either out value is 0 when only the other was found.
    /// </summary>
    public static bool TryParse(string? text, out float lpi, out float angle)
    {
        lpi = 0;
        angle = 0;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lpiMatch = LpiPattern().Match(text);
        if (lpiMatch.Success && TryNumber(lpiMatch.Groups[1].Value, out var parsedLpi)
            && parsedLpi is > 0 and <= 600)
        {
            lpi = parsedLpi;
        }

        var angleMatch = AnglePattern().Match(text);
        if (angleMatch.Success && TryNumber(angleMatch.Groups[1].Value, out var parsedAngle)
            && parsedAngle is >= 0 and < 360)
        {
            angle = parsedAngle;
        }

        return lpi > 0 || angle > 0;
    }

    private static bool TryNumber(string text, out float value) =>
        float.TryParse(text.Replace(',', '.'), NumberStyles.Float,
                       CultureInfo.InvariantCulture, out value);
}
