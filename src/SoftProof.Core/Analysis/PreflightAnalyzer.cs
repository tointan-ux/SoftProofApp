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

public sealed record PreflightResult(string Title, string Detail, PreflightStatus Status);

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
        return new List<PreflightResult>
        {
            CheckScreenAngles(job),
            CheckHighlightDots(plates),
            CheckTotalInk(job, plates),
            new("Barcode Quiet Zone & BWR",
                "Not run: barcode analysis is not implemented yet.", PreflightStatus.NotRun),
            new("Dieline Knockout / Overprint",
                "Not run: dieline analysis is not implemented yet.", PreflightStatus.NotRun)
        };
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
    private static PreflightResult CheckHighlightDots(IReadOnlyList<PlateLayer> plates)
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
        
        var detail = $"Worst plate: {worstName}, {worstFraction * 100:0.000}% of area below " +
                     $"{HighlightThresholdPercent:0.#}% tone (limit {HighlightMaxAreaFraction * 100:0.00}%).";
        return new(title, detail,
            worstFraction <= HighlightMaxAreaFraction ? PreflightStatus.Pass : PreflightStatus.Warning);
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
        var detail = $"Maximum {tac:0}% (limit {limit:0}%).";
        return new(title, detail, tac <= limit ? PreflightStatus.Pass : PreflightStatus.Fail);
    }
}
