using SoftProof.Core.Models;

namespace SoftProof.Core.Analysis;

public enum PreflightStatus
{
    /// <summary>The check could not be performed (missing data, or analysis not implemented).</summary>
    NotRun,
    Pass,
    Warning,
    Fail
}

/// <summary>Image-space region (base-level pixels) a warning wants the viewer to zoom to.</summary>
public sealed record FocusRegion(string PlateName, double X, double Y, double Width, double Height);

public sealed record PreflightResult(string Title, string Detail, PreflightStatus Status)
{
    /// <summary>Where to jump when the user clicks the warning; null when not locatable.</summary>
    public FocusRegion? Focus { get; init; }
}

/// <summary>
/// Preflight checks computed from the loaded plate rasters and screening metadata.
/// Only checks that can genuinely be derived from that data report Pass/Warning/Fail;
/// the rest report <see cref="PreflightStatus.NotRun"/> rather than pretending.
/// </summary>
public static class PreflightAnalyzer
{
    private const double HighlightThresholdPercent = 2.0;
    private const double HighlightMaxAreaFraction = 0.0005; // 0.05 % of a plate's pixels


    
    public static IReadOnlyList<PreflightResult> Run(PrintJob job, IReadOnlyList<PlateLayer> plates)
    {
        // Per-press thresholds, clamped so a bad profile cannot fire every warning at once.
        var settings = job.ProofSettings.Preflight?.Normalized() ?? new PreflightSettings();

        return new List<PreflightResult>
        {
            CheckScreenAngles(job),
            CheckHighlightDots(job, plates),
            CheckTotalInk(job, plates),
            CheckContentVsMarks(job, plates, settings),
            CheckPlatePolarity(plates, settings),
            CheckMissingSeparation(plates, settings),
            CheckTrapGaps(job, plates, settings),
            CheckBarcodeQuietZoneAndBwr(job, plates, settings),
            CheckDielineKnockoutOverprint(job, plates, settings),
        };
    }
    
    /// <summary>
    /// Tell the reader when a result is computed from an incomplete plate set or a reduced
    /// preview. A green tick on two of four plates is worse than an honest amber estimate.
    /// </summary>
    private static string CompletenessNote(PrintJob job, IReadOnlyList<PlateLayer> plates,
                                           int loadedProcess, out bool partial)
    {
        var expectedProcess = job.Separations.Count(separation => IsProcess(separation.Color));
        partial = expectedProcess > 0 && loadedProcess < expectedProcess;

        var estimated = plates.Any(plate => (plate.Pyramid?.Base.Factor ?? 1) > 1);
        if (!partial && !estimated)
            return string.Empty;

        var parts = new List<string>();
        if (partial)
            parts.Add($"Partial: {loadedProcess} of {expectedProcess} separations");
        if (estimated)
            parts.Add("estimated");

        return " · " + string.Join(" · ", parts);
    }

    private static bool IsProcess(SeparationColor c) =>
        c is SeparationColor.ProcessCyan or SeparationColor.ProcessMagenta
          or SeparationColor.ProcessYellow or SeparationColor.ProcessBlack;
    
    // ---- Screen angle separation (moire avoidance) -------------------------------------------
    private static PreflightResult CheckScreenAngles(PrintJob job)
    {
        const string title = "Screen Angle Separation";
        
        var c = job.Separations.FirstOrDefault(s => s.Color == SeparationColor.ProcessCyan);
        var m = job.Separations.FirstOrDefault(s => s.Color == SeparationColor.ProcessMagenta);
        var y = job.Separations.FirstOrDefault(s => s.Color == SeparationColor.ProcessYellow);
        var k = job.Separations.FirstOrDefault(s => s.Color == SeparationColor.ProcessBlack);
        
        if (c is null || m is null || y is null || k is null)
            return new(title, "Needs cyan, magenta, yellow and black plates.", PreflightStatus.NotRun);
        if (c.ScreenLpi <= 0 || m.ScreenLpi <= 0 || y.ScreenLpi <= 0 || k.ScreenLpi <= 0)
            return new(title, "The RIP did not report screening for the process plates.", PreflightStatus.NotRun);
        
        // Dark inks (C, M, K) should be >= 30 deg apart; yellow >= 15 deg from the others.
        var darkMin = Math.Min(Dist(c.ScreenAngle, m.ScreenAngle),
                      Math.Min(Dist(c.ScreenAngle, k.ScreenAngle), Dist(m.ScreenAngle, k.ScreenAngle)));
        var yellowMin = Math.Min(Dist(y.ScreenAngle, c.ScreenAngle),
                        Math.Min(Dist(y.ScreenAngle, m.ScreenAngle), Dist(y.ScreenAngle, k.ScreenAngle)));
        
        var detail = $"C {c.ScreenAngle:0}° / M {m.ScreenAngle:0}° / Y {y.ScreenAngle:0}° / K {k.ScreenAngle:0}° " +
                     $"(C/M/K min {darkMin:0}°, yellow min {yellowMin:0}°)";
        
        return new(title, detail,
            darkMin >= 30 && yellowMin >= 15 ? PreflightStatus.Pass : PreflightStatus.Fail);
    }
    
    // Smallest angle between two screen directions (screens repeat every 180 degrees).
    private static double Dist(double a, double b)
    {
        var d = Math.Abs(a - b) % 180.0;
        return Math.Min(d, 180.0 - d);
    }
    
    // ---- Highlight dot protection ------------------------------------------------------------
    private static PreflightResult CheckHighlightDots(PrintJob job, IReadOnlyList<PlateLayer> plates)
    {
        const string title = "Highlight Dot Check";
        var inked = plates.Where(p => !p.Separation.IsTechnical).ToList();
        if (inked.Count == 0)
            return new(title, "No raster data loaded.", PreflightStatus.NotRun);
        
        // Values v where 0 < v/255*100 < threshold.
        var maxValue = (int)Math.Ceiling(HighlightThresholdPercent * 255.0 / 100.0) - 1;
        var worstFraction = 0.0;
        var worstName = inked[0].Name;
        
        foreach (var plate in inked)
        {
            long count = 0;
            var data = plate.Coverage;
            var w = plate.Width;
            Parallel.For(0, plate.Height, () => 0L, (row, _, local) =>
            {
                var start = row * w;
                for (var x = 0; x < w; x++)
                {
                    var v = data[start + x];
                    if (v >= 1 && v <= maxValue) local++;
                }
                return local;
            }, local => Interlocked.Add(ref count, local));
            
            var fraction = count / (double)((long)plate.Width * plate.Height);
            if (fraction > worstFraction)
            {
                worstFraction = fraction;
                worstName = plate.Name;
            }
        }
        
        var loadedProcess = inked.Count(plate => IsProcess(plate.Separation.Color));
        var note = CompletenessNote(job, plates, loadedProcess, out var partial);

        var detail = $"Worst plate: {worstName}, {worstFraction * 100:0.000}% of area below " +
                     $"{HighlightThresholdPercent:0.#}% tone (limit {HighlightMaxAreaFraction * 100:0.00}%)." + note;
        return new(title, detail,
            partial || worstFraction > HighlightMaxAreaFraction
                ? PreflightStatus.Warning
                : PreflightStatus.Pass);
    }
    
    // ---- Total area coverage (ink limit) -----------------------------------------------------
    private static PreflightResult CheckTotalInk(PrintJob job, IReadOnlyList<PlateLayer> plates)
    {
        const string title = "Total Ink Coverage";
        var process = plates.Where(p => IsProcess(p.Separation.Color)).ToList();
        if (process.Count == 0)
            return new(title, "No process plates with raster data loaded.", PreflightStatus.NotRun);
        
        var width = process[0].Width;
        var height = process[0].Height;
        if (process.Any(p => p.Width != width || p.Height != height))
            return new(title, "Process plates have different sizes.", PreflightStatus.NotRun);
        
        var arrays = process.Select(p => p.Coverage).ToArray();
        var rowMax = new int[height];
        Parallel.For(0, height, y =>
        {
            var start = y * width;
            var max = 0;
            for (var x = 0; x < width; x++)
            {
                var sum = 0;
                for (var i = 0; i < arrays.Length; i++)
                    sum += arrays[i][start + x];
                if (sum > max) max = sum;
            }
            rowMax[y] = max;
        });
        
        var tac = rowMax.Max() * 100.0 / 255.0;
        var limit = job.ProofSettings.InkLimit;
        var note = CompletenessNote(job, plates, process.Count, out var partial);
        var detail = $"Maximum {tac:0}% (limit {limit:0}%)." + note;

        return new(title, detail,
            partial ? PreflightStatus.Warning
                    : tac <= limit ? PreflightStatus.Pass : PreflightStatus.Fail);
    }

    /// <summary>Append the press profile name so a warning shows which rule set produced it.</summary>
    private static string PressSuffix(PreflightSettings settings) =>
        string.IsNullOrWhiteSpace(settings.PressName) ? string.Empty : $" [press: {settings.PressName}]";

    // ---- Content versus marks/slug -------------------------------------------------------------
    private static PreflightResult CheckContentVsMarks(PrintJob job, IReadOnlyList<PlateLayer> plates,
                                                       PreflightSettings settings)
    {
        const string title = "Content vs Marks";

        var loaded = plates.Where(p => !p.Separation.IsTechnical && !p.InkBounds.IsEmpty).ToList();
        if (loaded.Count == 0)
            return new(title, "No ink on any loaded plate.", PreflightStatus.Pass);

        var dpi = job.Metadata.Resolution;
        var slugPixels = dpi > 0 ? settings.SlugBandMm / 25.4 * dpi : 0;

        var outsideTrim = new List<string>();
        var tiny = new List<string>();
        PlateLayer? firstFlagged = null;

        foreach (var plate in loaded)
        {
            var tinyByArea = plate.InkedPixelPercent < settings.TinyInkedPixelPercent &&
                             plate.InkedAreaPercent < settings.TinyCoveragePercent;
            if (tinyByArea)
            {
                tiny.Add($"{plate.Name} ({plate.InkedPixelPercent:0.00}% of pixels inked)");
                firstFlagged ??= plate;
            }

            if (slugPixels < 1)
                continue;

            var left = (int)Math.Round(slugPixels);
            var top = left;
            var right = plate.Width - left;
            var bottom = plate.Height - top;
            if (right - left < 1 || bottom - top < 1)
                continue;

            var inkInside = CountInkInside(plate, left, top, right, bottom);
            if (inkInside == 0 && !tinyByArea)
            {
                outsideTrim.Add($"{plate.Name} (no ink inside a {settings.SlugBandMm:0.#} mm trim inset)");
                firstFlagged ??= plate;
            }
        }

        if (outsideTrim.Count > 0)
            return new(title,
                "Only marks/slug content: " + string.Join("; ", outsideTrim) +
                ". The ink sits entirely outside the trim area." + PressSuffix(settings),
                PreflightStatus.Warning)
            { Focus = FocusOf(firstFlagged) };

        if (tiny.Count > 0)
            return new(title,
                "Suspiciously little content: " + string.Join("; ", tiny) +
                ". Check that the plate is not marks-only." + PressSuffix(settings),
                PreflightStatus.Warning)
            { Focus = FocusOf(firstFlagged) };

        return new(title,
            "All plates carry ink inside the content area.", PreflightStatus.Pass);
    }

    /// <summary>Count inked pixels strictly inside the given rectangle.</summary>
    private static long CountInkInside(PlateLayer plate, int left, int top, int right, int bottom)
    {
        long inside = 0;
        var coverage = plate.Coverage;

        Parallel.For(top, bottom, () => 0L, (y, _, local) =>
        {
            var start = y * plate.Width;
            for (var x = left; x < right; x++)
                if (coverage[start + x] != 0)
                    local++;

            return local;
        }, local => Interlocked.Add(ref inside, local));

        return inside;
    }

    // ---- Polarity ------------------------------------------------------------------------------
    private static PreflightResult CheckPlatePolarity(IReadOnlyList<PlateLayer> plates, PreflightSettings settings)
    {
        const string title = "Plate Polarity";

        var loaded = plates.Where(p => !p.Separation.IsTechnical).ToList();
        if (loaded.Count == 0)
            return new(title, "No raster data loaded.", PreflightStatus.NotRun);

        var nearSolid = loaded
            .Where(p => p.InkedAreaPercent > settings.NearSolidPercent)
            .Select(p => $"{p.Name} at {p.InkedAreaPercent:0.00}%")
            .ToList();

        var firstSolid = loaded.FirstOrDefault(p => p.InkedAreaPercent > settings.NearSolidPercent);
        if (nearSolid.Count > 0)
            return new(title,
                "Near-solid plate: " + string.Join("; ", nearSolid) +
                $". A plate that should be mostly paper above {settings.NearSolidPercent:0.#}% usually means " +
                "wrong polarity (inverted plate)." + PressSuffix(settings),
                PreflightStatus.Warning)
            { Focus = firstSolid is null ? null : FocusOfPlate(firstSolid) };

        return new(title,
            $"No plate is above {settings.NearSolidPercent:0.#}% coverage; polarity looks plausible." + PressSuffix(settings),
            PreflightStatus.Pass);
    }

    // ---- Sibling comparison --------------------------------------------------------------------
    private static PreflightResult CheckMissingSeparation(IReadOnlyList<PlateLayer> plates,
                                                          PreflightSettings settings)
    {
        const string title = "Missing Separation";

        var process = plates.Where(p => IsProcess(p.Separation.Color)).ToList();
        if (process.Count < 4)
        {
            var missingColors = new[]
                {
                    (SeparationColor.ProcessCyan, "Cyan"),
                    (SeparationColor.ProcessMagenta, "Magenta"),
                    (SeparationColor.ProcessYellow, "Yellow"),
                    (SeparationColor.ProcessBlack, "Black")
                }
                .Where(entry => process.All(plate => plate.Separation.Color != entry.Item1))
                .Select(entry => entry.Item2)
                .ToArray();

            var loaded = process.Count == 0
                ? "none"
                : string.Join(", ", process.Select(plate => $"{plate.Name} {plate.InkedAreaPercent:0.0}%"));

            var detail = missingColors.Length > 0
                ? $"Missing: {string.Join(", ", missingColors)}. Loaded: {loaded}."
                : $"Needs all four process plates with raster data loaded (loaded: {loaded}).";

            return new(title, detail + PressSuffix(settings), PreflightStatus.NotRun);
        }

        var meaningful = process.Where(p => p.InkedAreaPercent >= settings.MeaningfulCoveragePercent).ToList();
        var sparse = process.Where(p => p.InkedAreaPercent < settings.MissingSeparationPercent).ToList();

        if (meaningful.Count >= 3 && sparse.Count > 0)
        {
            var sparseText = string.Join(", ", sparse.Select(p => $"{p.Name} {p.InkedAreaPercent:0.000}%"));
            var others = string.Join(", ", meaningful.Select(p => $"{p.Name} {p.InkedAreaPercent:0.0}%"));
            return new(title,
                $"Possible missing separation: {sparseText} while {others} carry real ink." + PressSuffix(settings),
                PreflightStatus.Warning)
            { Focus = FocusOfPlate(sparse[0]) };
        }

        var summary = string.Join(", ", process.Select(p => $"{p.Name} {p.InkedAreaPercent:0.0}%"));
        return new(title, $"Process coverage: {summary}." + PressSuffix(settings), PreflightStatus.Pass);
    }

    private static FocusRegion? FocusOf(PlateLayer? plate)
    {
        if (plate is null || plate.InkBounds.IsEmpty)
            return null;

        var bounds = plate.InkBounds;
        var pad = Math.Max(8, Math.Max(bounds.Width, bounds.Height) / 10);
        return new FocusRegion(plate.Name,
            Math.Max(0, bounds.X - pad), Math.Max(0, bounds.Y - pad),
            bounds.Width + pad * 2, bounds.Height + pad * 2);
    }

    private static FocusRegion FocusOfPlate(PlateLayer plate) =>
        new(plate.Name, 0, 0, plate.Width, plate.Height);

    // ---- Trap gaps: paper hairlines between two different inks --------------------------------
    private static PreflightResult CheckTrapGaps(PrintJob job, IReadOnlyList<PlateLayer> plates,
                                                 PreflightSettings settings)
    {
        const string title = "Trap Gaps";
        const byte inkThreshold = 16; // ignore faint anti-aliased edges

        var process = plates.Where(p => IsProcess(p.Separation.Color)).ToList();
        if (process.Count < 2)
            return new(title, "Needs at least two process plates with raster data.", PreflightStatus.NotRun);

        var width = process[0].Width;
        var height = process[0].Height;
        if (process.Any(p => p.Width != width || p.Height != height))
            return new(title, "Process plates have different sizes.", PreflightStatus.NotRun);

        var dpi = job.Metadata.Resolution;
        if (dpi <= 0)
            return new(title, "Resolution unknown; cannot judge gap width in millimetres.",
                       PreflightStatus.NotRun);

        var arrays = process.Select(p => p.Coverage).ToArray();
        var gapPixels = Math.Max(1, (int)Math.Round(settings.TrapGapMm / 25.4 * dpi));
        var gate = new object();
        long gaps = 0;
        var firstX = -1;
        var firstY = -1;

        int InkAt(int x, int y)
        {
            var index = y * width + x;
            for (var i = 0; i < arrays.Length; i++)
                if (arrays[i][index] >= inkThreshold)
                    return i;
            return -1;
        }

        void Scan(bool horizontal)
        {
            Parallel.For(0, horizontal ? height : width, () => (count: 0L, fx: -1, fy: -1),
                (line, _, local) =>
                {
                    var lastPlate = -1;
                    var last = -1;
                    var length = horizontal ? width : height;

                    for (var step = 0; step < length; step++)
                    {
                        var x = horizontal ? step : line;
                        var y = horizontal ? line : step;
                        var ink = InkAt(x, y);
                        if (ink < 0)
                            continue;

                        if (lastPlate >= 0 && ink != lastPlate)
                        {
                            var gap = step - last - 1;
                            if (gap >= 1 && gap <= gapPixels)
                            {
                                local.count++;
                                if (local.fx < 0)
                                {
                                    local.fx = horizontal ? last + 1 : x;
                                    local.fy = horizontal ? y : last + 1;
                                }
                            }
                        }

                        lastPlate = ink;
                        last = step;
                    }

                    return local;
                },
                local =>
                {
                    lock (gate)
                    {
                        gaps += local.count;
                        if (firstX < 0 && local.fx >= 0)
                        {
                            firstX = local.fx;
                            firstY = local.fy;
                        }
                    }
                });
        }

        Scan(horizontal: true);
        Scan(horizontal: false);

        if (gaps < settings.TrapGapMinPixels)
            return new(title,
                $"No trap gaps: fewer than {settings.TrapGapMinPixels:0} paper hairlines " +
                $"≤ {settings.TrapGapMm:0.###} mm between different inks." + PressSuffix(settings),
                PreflightStatus.Pass);

        var pixelsPerMm = dpi / 25.4;
        var location = firstX >= 0
            ? $" First at {firstX / pixelsPerMm:0.0} x {firstY / pixelsPerMm:0.0} mm."
            : string.Empty;

        var focus = firstX >= 0
            ? new FocusRegion(string.Empty,
                Math.Max(0, firstX - 40), Math.Max(0, firstY - 40), 80, 80)
            : null;

        return new(title,
            $"{gaps} paper hairline(s) ≤ {settings.TrapGapMm:0.###} mm between different inks — " +
            $"a trapping or gap-line risk.{location}" + PressSuffix(settings),
            PreflightStatus.Warning)
        { Focus = focus };
    }

    // ---- Barcode quiet zone & BWR --------------------------------------------------------
    private static PreflightResult CheckBarcodeQuietZoneAndBwr(PrintJob job, IReadOnlyList<PlateLayer> plates, PreflightSettings settings)
    {
        const string title = "Barcode Quiet Zone & BWR";

        if (!settings.EnableBarcodeCheck)
            return new(title, "Barcode check disabled in preflight settings.", PreflightStatus.NotRun);

        var process = plates.Where(p => IsProcess(p.Separation.Color)).ToList();
        if (process.Count == 0)
            return new(title, "No process plates with raster data loaded.", PreflightStatus.NotRun);

        var allResults = new List<BarcodeAnalyzer.BarcodeResult>();
        foreach (var plate in process)
        {
            var results = BarcodeAnalyzer.Analyze(plate, job, settings);
            allResults.AddRange(results);
        }

        if (allResults.Count == 0)
            return new(title, "No barcodes detected on process plates.", PreflightStatus.NotRun);

        var failed = allResults.Where(r => !r.QuietZoneLeftPass || !r.QuietZoneRightPass || !r.BwrPass).ToList();
        var passed = allResults.Where(r => r.QuietZoneLeftPass && r.QuietZoneRightPass && r.BwrPass).ToList();

        var detailParts = new List<string>();
        if (failed.Count > 0)
        {
            detailParts.Add($"FAIL: {failed.Count} barcode(s) with issues:");
            foreach (var r in failed)
            {
                var issues = new List<string>();
                if (!r.QuietZoneLeftPass) issues.Add($"left QZ {r.QuietZoneLeftMm:0.00}mm < {settings.BarcodeQuietZoneMm}mm");
                if (!r.QuietZoneRightPass) issues.Add($"right QZ {r.QuietZoneRightMm:0.00}mm < {settings.BarcodeQuietZoneMm}mm");
                if (!r.BwrPass) issues.Add($"BWR {r.BwrMm:0.000}mm > {settings.BarcodeBWRToleranceMm}mm");
                detailParts.Add($"  {r.PlateName} ({r.Symbology}): {string.Join(", ", issues)}");
            }
        }

        if (passed.Count > 0)
        {
            detailParts.Add($"PASS: {passed.Count} barcode(s) meet quiet zone and BWR requirements.");
        }

        var detail = string.Join(" ", detailParts);
        var status = failed.Count > 0 ? PreflightStatus.Fail : PreflightStatus.Pass;

        var focus = failed.Count > 0 ? failed.First().Focus : passed.FirstOrDefault()?.Focus;

        return new(title, detail, status) { Focus = focus };
    }


// ---- Dieline knockout/overprint --------------------------------------------------------
    private static PreflightResult CheckDielineKnockoutOverprint(PrintJob job, IReadOnlyList<PlateLayer> plates, PreflightSettings settings)
    {
        const string title = "Dieline Knockout / Overprint";

        if (!settings.EnableDielineCheck)
            return new(title, "Dieline check disabled in preflight settings.", PreflightStatus.NotRun);

        var allResults = DielineAnalyzer.Analyze(job, plates, settings);

        if (allResults.Count == 0)
            return new(title, "No dieline plates detected.", PreflightStatus.NotRun);

        var failed = allResults.Where(r => !r.KnockoutPass || !r.OverprintPass).ToList();
        var passed = allResults.Where(r => r.KnockoutPass && r.OverprintPass).ToList();

        var detailParts = new List<string>();
        if (failed.Count > 0)
        {
            detailParts.Add($"FAIL: {failed.Count} dieline(s) with issues:");
            foreach (var r in failed)
            {
                var issues = new List<string>();
                if (!r.KnockoutPass) issues.Add($"ink under dieline {r.InkUnderDielinePercent:0.00}% > {settings.DielineMaxInkUnderPercent:0.##}%");
                if (!r.OverprintPass) issues.Add("overprint issue");
                detailParts.Add($"  {r.DielinePlateName} ({r.DielineType}): {string.Join(", ", issues)}");
            }
        }

        if (passed.Count > 0)
        {
            detailParts.Add($"PASS: {passed.Count} dieline(s) meet knockout/overprint requirements.");
        }

        var detail = string.Join(" ", detailParts);
        var status = failed.Count > 0 ? PreflightStatus.Fail : PreflightStatus.Pass;

        var focus = failed.Count > 0 ? failed.First().Focus : passed.FirstOrDefault()?.Focus;

        return new(title, detail, status) { Focus = focus };
    }

}
