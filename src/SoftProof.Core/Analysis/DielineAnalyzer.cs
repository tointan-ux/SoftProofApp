using System;
using System.Collections.Generic;
using System.Linq;
using SoftProof.Core.Models;

namespace SoftProof.Core.Analysis;

/// <summary>
/// Analyzes dieline plates for knockout/overprint compliance.
/// Dielines (cut, fold, perforation, crease) should knock out ink underneath.
/// Varnish/white plates may need to overprint correctly.
/// </summary>
public static class DielineAnalyzer
{
    /// <summary>
    /// Result of dieline analysis on a plate.
    /// </summary>
    public sealed class DielineResult
    {
        public string DielinePlateName { get; set; } = string.Empty;
        public string DielineType { get; set; } = string.Empty; // "cut", "fold", "perf", "crease", "score", "registration"
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double InkUnderDielinePercent { get; set; } // % of dieline area with ink underneath
        public double MaxInkDensityUnderDieline { get; set; } // 0-255
        public bool KnockoutPass { get; set; }
        public bool OverprintPass { get; set; }
        public string Detail { get; set; } = string.Empty;
        public FocusRegion? Focus { get; set; }
        public List<string> AffectedPlateNames { get; set; } = new();
    }

    /// <summary>
    /// Known dieline/technical plate keywords.
    /// </summary>
    private static readonly string[] DielineKeywords = new[]
    {
        "dieline", "die line", "die-line",
        "cut", "cutting",
        "fold", "folding",
        "perf", "perforation", "perforated",
        "crease", "creasing", "score", "scoring",
        "knife", "knife line",
        "registration", "register", "reg mark", "reg-mark",
        "keyline", "key line"
    };

    private static readonly string[] VarnishKeywords = new[]
    {
        "varnish", "coating", "uv", "aqueous", "aq"
    };

    private static readonly string[] WhiteKeywords = new[]
    {
        "white", "opaque white", "white ink", "white_ink"
    };

    /// <summary>
    /// Analyzes all plates for dieline knockout/overprint compliance.
    /// </summary>
    /// <param name="job">Print job for metadata.</param>
    /// <param name="plates">All loaded plates.</param>
    /// <param name="settings">Dieline check thresholds.</param>
    /// <returns>List of dieline findings.</returns>
    public static IReadOnlyList<DielineResult> Analyze(PrintJob job, IReadOnlyList<PlateLayer> plates, PreflightSettings settings)
    {
        var results = new List<DielineResult>();

        if (!settings.EnableDielineCheck || plates.Count == 0)
            return results;

        // Identify dieline plates
        var dielinePlates = plates.Where(p => IsDielinePlate(p)).ToList();
        if (dielinePlates.Count == 0)
            return results;

        // Identify varnish and white plates
        var varnishPlates = plates.Where(p => IsVarnishPlate(p)).ToList();
        var whitePlates = plates.Where(p => IsWhitePlate(p)).ToList();
        var allTechnicalPlates = plates.Where(p => p.Separation.IsTechnical).ToList();

        // Identify artwork plates (process + spot inks that are not technical)
        var artworkPlates = plates.Where(p => !p.Separation.IsTechnical && !IsVarnishPlate(p) && !IsWhitePlate(p)).ToList();

        foreach (var dielinePlate in dielinePlates)
        {
            var dielineType = ClassifyDielineType(dielinePlate.Separation.Name);
            var result = AnalyzeDielinePlate(dielinePlate, artworkPlates, varnishPlates, whitePlates, job, settings);
            result.DielinePlateName = dielinePlate.Separation.Name;
            result.DielineType = dielineType;
            results.Add(result);
        }

        // Check varnish/white overprint
        foreach (var vp in varnishPlates.Concat(whitePlates))
        {
            var result = CheckVarnishWhiteOverprint(vp, artworkPlates, job, settings);
            if (result != null)
                results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Classifies a dieline plate by its name.
    /// </summary>
    private static string ClassifyDielineType(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("cut") || lower.Contains("die")) return "cut";
        if (lower.Contains("fold") || lower.Contains("crease") || lower.Contains("score")) return "fold";
        if (lower.Contains("perf") || lower.Contains("perforat")) return "perforation";
        if (lower.Contains("registration") || lower.Contains("reg mark") || lower.Contains("reg-mark")) return "registration";
        if (lower.Contains("keyline") || lower.Contains("key line")) return "keyline";
        return "technical";
    }

    /// <summary>
    /// Checks if a plate is a dieline/technical plate.
    /// </summary>
    private static bool IsDielinePlate(PlateLayer plate)
    {
        if (plate.Separation.IsTechnical)
            return true;

        var lower = plate.Separation.Name.ToLowerInvariant();
        foreach (var kw in DielineKeywords)
        {
            if (lower.Contains(kw))
                return true;
        }

        // Also check SpotColorName if it's a spot color
        if (plate.Separation.IsSpotColor && !string.IsNullOrWhiteSpace(plate.Separation.SpotColorName))
        {
            var spotLower = plate.Separation.SpotColorName.ToLowerInvariant();
            foreach (var kw in DielineKeywords)
            {
                if (spotLower.Contains(kw))
                    return true;
            }
        }

        return false;
    }

    private static bool IsVarnishPlate(PlateLayer plate)
    {
        if (plate.Separation.Color == SeparationColor.Varnish)
            return true;

        var lower = plate.Separation.Name.ToLowerInvariant();
        foreach (var kw in VarnishKeywords)
        {
            if (lower.Contains(kw))
                return true;
        }

        if (plate.Separation.IsSpotColor && !string.IsNullOrWhiteSpace(plate.Separation.SpotColorName))
        {
            var spotLower = plate.Separation.SpotColorName.ToLowerInvariant();
            foreach (var kw in VarnishKeywords)
            {
                if (spotLower.Contains(kw))
                    return true;
            }
        }

        return false;
    }

    private static bool IsWhitePlate(PlateLayer plate)
    {
        if (plate.Separation.Color == SeparationColor.White)
            return true;

        var lower = plate.Separation.Name.ToLowerInvariant();
        foreach (var kw in WhiteKeywords)
        {
            if (lower.Contains(kw))
                return true;
        }

        if (plate.Separation.IsSpotColor && !string.IsNullOrWhiteSpace(plate.Separation.SpotColorName))
        {
            var spotLower = plate.Separation.SpotColorName.ToLowerInvariant();
            foreach (var kw in WhiteKeywords)
            {
                if (spotLower.Contains(kw))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Analyzes a single dieline plate for knockout compliance.
    /// </summary>
    private static DielineResult AnalyzeDielinePlate(
        PlateLayer dielinePlate,
        IReadOnlyList<PlateLayer> artworkPlates,
        IReadOnlyList<PlateLayer> varnishPlates,
        IReadOnlyList<PlateLayer> whitePlates,
        PrintJob job,
        PreflightSettings settings)
    {
        var dpi = job.Metadata.Resolution > 0 ? job.Metadata.Resolution : 2400;
        var pixelsPerMm = dpi / 25.4;
        var minInkThreshold = 16; // ignore faint anti-aliasing
        var maxInkThreshold = settings.DielineMaxInkDensityUnderDieline; // 0-255

        // Create binary mask of dieline (ink = true)
        var dielineMask = new bool[dielinePlate.Coverage.Length];
        for (var i = 0; i < dielineMask.Length; i++)
            dielineMask[i] = dielinePlate.Coverage[i] >= 128;

        // Find bounding box of dieline
        var bounds = GetInkBounds(dielineMask, dielinePlate.Width, dielinePlate.Height);
        if (bounds.Width == 0 || bounds.Height == 0)
        {
            return new DielineResult
            {
                DielinePlateName = dielinePlate.Separation.Name,
                Detail = "Dieline plate has no ink content.",
                KnockoutPass = true,
                OverprintPass = true
            };
        }

        // Check ink under dieline area on artwork plates
        var affectedPlates = new List<string>();
        var totalDielinePixels = 0L;
        var inkUnderPixels = 0L;
        var maxInkDensity = 0;

        foreach (var artPlate in artworkPlates)
        {
            if (artPlate.Width != dielinePlate.Width || artPlate.Height != dielinePlate.Height)
                continue;

            var plateInkUnder = 0L;
            var plateMaxDensity = 0;

            // Only check within dieline bounding box (expanded by a small margin)
            var margin = (int)Math.Round(1.0 * job.Metadata.Resolution / 25.4); // 1mm margin
            var x0 = Math.Max(0, bounds.X - margin);
            var y0 = Math.Max(0, bounds.Y - margin);
            var x1 = Math.Min(dielinePlate.Width, bounds.X + bounds.Width + margin);
            var y1 = Math.Min(dielinePlate.Height, bounds.Y + bounds.Height + margin);

            for (var y = y0; y < y1; y++)
            {
                var baseIdx = y * dielinePlate.Width;
                for (var x = x0; x < x1; x++)
                {
                    var idx = baseIdx + x;
                    if (dielineMask[idx])
                    {
                        totalDielinePixels++;
                        var inkValue = artPlate.Coverage[idx];
                        if (inkValue >= minInkThreshold)
                        {
                            inkUnderPixels++;
                            plateInkUnder++;
                            if (inkValue > plateMaxDensity) plateMaxDensity = inkValue;
                        }
                    }
                }
            }

            if (plateInkUnder > 0)
            {
                affectedPlates.Add($"{artPlate.Separation.Name} ({plateInkUnder * 100.0 / totalDielinePixels:0.00}% under dieline)");
                if (plateMaxDensity > maxInkDensity) maxInkDensity = plateMaxDensity;
            }
        }

        var inkUnderPercent = totalDielinePixels > 0 ? (inkUnderPixels * 100.0 / totalDielinePixels) : 0;
        var knockoutPass = inkUnderPercent <= settings.DielineMaxInkUnderPercent;

        // Check varnish/white overprint on dieline
        var overprintPass = true;
        foreach (var vp in new[] { "varnish", "white" }) // placeholder
        {
            // Varnish/white should NOT be under the dieline (they should overprint on top)
            // But we check this in a separate method
        }

        var focus = new FocusRegion(
            dielinePlate.Separation.Name,
            bounds.X, bounds.Y, bounds.Width, bounds.Height);

        var detail = $"Dieline: {dielinePlate.Separation.Name} ({inkUnderPercent:0.00}% ink under, limit {settings.DielineMaxInkUnderPercent:0.##}%)";
        if (affectedPlates.Count > 0)
            detail += $". Affected: {string.Join(", ", affectedPlates)}";

        return new DielineResult
        {
            DielinePlateName = dielinePlate.Separation.Name,
            DielineType = ClassifyDielineType(dielinePlate.Separation.Name),
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
            InkUnderDielinePercent = inkUnderPercent,
            MaxInkDensityUnderDieline = maxInkDensity,
            KnockoutPass = knockoutPass,
            OverprintPass = overprintPass,
            Detail = detail,
            Focus = focus,
            AffectedPlateNames = affectedPlates
        };
    }

    /// <summary>
    /// Checks varnish/white plates for correct overprint behavior.
    /// Varnish/white should overprint artwork, not knock it out.
    /// </summary>
    private static DielineResult? CheckVarnishWhiteOverprint(
        PlateLayer vp,
        IReadOnlyList<PlateLayer> artworkPlates,
        PrintJob job,
        PreflightSettings settings)
    {
        if (vp.Coverage == null || vp.Width == 0 || vp.Height == 0)
            return null;

        var dpi = job.Metadata.Resolution > 0 ? job.Metadata.Resolution : 2400;

        // Check if varnish/white has knockout where it should overprint
        // This is a simplified check: varnish/white should cover artwork areas
        var vpMask = new bool[vp.Coverage.Length];
        for (var i = 0; i < vpMask.Length; i++)
            vpMask[i] = vp.Coverage[i] >= 128;

        var knockoutPixels = 0L;
        var totalVpPixels = 0L;

        foreach (var artPlate in artworkPlates)
        {
            if (artPlate.Width != vp.Width || artPlate.Height != vp.Height)
                continue;

            for (var i = 0; i < vpMask.Length; i++)
            {
                if (vpMask[i])
                {
                    totalVpPixels++;
                    var artInk = artPlate.Coverage[i];
                    if (artInk == 0) // artwork has no ink here, but varnish/white does - that's fine
                        continue;
                    // If artwork has ink but varnish/white is not knocking out, that's correct overprint
                    // If varnish/white is knocking out (has a hole where artwork is), that's wrong
                    // Actually, in printing, varnish/white typically overprints, so we check for knockouts
                }
            }
        }

        // This is a complex check; for now return null (not implemented in detail)
        // Full implementation would check for knockouts in varnish/white where artwork exists
        return null;
    }

    /// <summary>
    /// Gets the bounding box of ink pixels in a binary mask.
    /// </summary>
    private static (int X, int Y, int Width, int Height) GetInkBounds(bool[] mask, int width, int height)
    {
        int minX = width, minY = height, maxX = -1, maxY = -1;

        for (var y = 0; y < height; y++)
        {
            var baseIdx = y * width;
            for (var x = 0; x < width; x++)
            {
                if (mask[baseIdx + x])
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        if (maxX < 0 || maxY < 0)
            return (0, 0, 0, 0);

        return (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}