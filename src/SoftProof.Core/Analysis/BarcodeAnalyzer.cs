using System;
using System.Collections.Generic;
using System.Linq;
using SoftProof.Core.Models;

namespace SoftProof.Core.Analysis;

/// <summary>
/// Analyzes 1D barcodes (EAN/UPC, Code 128, ITF-14, etc.) on a plate for quiet zone and BWR compliance.
/// Works on 8-bit coverage data (0=paper, 255=ink) at plate resolution.
/// </summary>
public static class BarcodeAnalyzer
{
    /// <summary>
    /// Result of barcode analysis on a plate.
    /// </summary>
    public sealed record BarcodeResult
    {
        public string PlateName { get; init; } = string.Empty;
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public BarcodeSymbology Symbology { get; init; } = BarcodeSymbology.Unknown;
        public double QuietZoneLeftMm { get; init; }
        public double QuietZoneRightMm { get; init; }
        public double MinBarWidthMm { get; init; }
        public double MaxBarWidthMm { get; init; }
        public double AvgBarWidthMm { get; init; }
        public double NarrowBarWidthMm { get; init; }
        public double BwrMm { get; init; } // Bar Width Reduction (positive = bars shrunk)
        public int BarCount { get; init; }
        public bool QuietZoneLeftPass { get; init; }
        public bool QuietZoneRightPass { get; init; }
        public bool BwrPass { get; init; }
        public string Detail { get; init; } = string.Empty;
        public FocusRegion? Focus { get; init; }
    }

    /// <summary>
    /// Supported 1D barcode symbologies.
    /// </summary>
    public enum BarcodeSymbology
    {
        Unknown,
        EAN13,
        EAN8,
        UPCA,
        UPCE,
        Code128,
        ITF14,
        Code39,
        Codabar
    }

    /// <summary>
    /// Analyzes a plate for 1D barcodes and returns quiet zone / BWR results.
    /// </summary>
    /// <param name="plate">Plate with 8-bit coverage data (0=paper, 255=ink).</param>
    /// <param name="job">Print job for metadata (DPI, etc.).</param>
    /// <param name="settings">Barcode check thresholds.</param>
    /// <returns>List of barcode findings (empty if none detected).</returns>
    public static IReadOnlyList<BarcodeResult> Analyze(PlateLayer plate, PrintJob job, PreflightSettings settings)
    {
        var results = new List<BarcodeResult>();
        if (!settings.EnableBarcodeCheck || plate.Coverage == null || plate.Width == 0 || plate.Height == 0)
            return results;

        var dpi = job.Metadata.Resolution > 0 ? job.Metadata.Resolution : 2400; // fallback
        var pixelsPerMm = dpi / 25.4;
        var minHeightPx = (int)Math.Round(settings.BarcodeMinHeightMm * pixelsPerMm);
        var quietZonePx = (int)Math.Round(settings.BarcodeQuietZoneMm * pixelsPerMm);
        var bwrTolerancePx = (int)Math.Round(settings.BarcodeBWRToleranceMm * pixelsPerMm);
        var narrowBarPx = Math.Max(1, (int)Math.Round(settings.BarcodeNarrowBarMm * pixelsPerMm));
        var quietZoneMultiple = 10; // ISO/IEC 15420: quiet zone >= 10x narrow bar

        // Convert coverage to binary (ink/paper) using threshold
        var threshold = 128;
        var binary = new bool[plate.Coverage.Length];
        for (var i = 0; i < binary.Length; i++)
            binary[i] = plate.Coverage[i] >= 128;

        // Scan horizontal lines at different heights to find barcode-like patterns
        var scanLines = SelectScanLines(plate.Height, minHeightPx);
        var foundBarcodes = new List<BarcodeCandidate>();

        foreach (var y in scanLines)
        {
            var transitions = FindTransitions(binary, plate.Width, y);
            if (transitions.Count < 6) // need at least a few bars/spaces
                continue;

            var candidate = AnalyzeTransitions(transitions, plate.Width, y, narrowBarPx, dpi);
            if (candidate != null && candidate.BarCount >= 6)
            {
                // Check if this candidate overlaps with already found ones
                if (!foundBarcodes.Any(f => Overlaps(f, candidate)))
                    foundBarcodes.Add(candidate);
            }
        }

        // Convert candidates to results with quiet zone / BWR analysis
        foreach (var c in foundBarcodes)
        {
            var leftQzMm = c.LeftQuietZonePx / (double)pixelsPerMm;
            var rightQzMm = c.RightQuietZonePx / (double)pixelsPerMm;
            var bwrMm = c.BwrPx / (double)pixelsPerMm;

            var minQuietZoneMm = Math.Max(settings.BarcodeQuietZoneMm, c.NarrowBarMm * 10);
            var bwrToleranceMm = settings.BarcodeBWRToleranceMm;

            var result = new BarcodeResult
            {
                PlateName = $"Plate_{plate.Separation?.Name ?? "unknown"}",
                X = c.X,
                Y = c.Y,
                Width = c.Width,
                Height = c.Height,
                Symbology = c.Symbology,
                QuietZoneLeftMm = leftQzMm,
                QuietZoneRightMm = rightQzMm,
                MinBarWidthMm = c.MinBarWidthPx / (double)(25.4 / c.Dpi),
                MaxBarWidthMm = c.MaxBarWidthPx / (double)(25.4 / c.Dpi),
                AvgBarWidthMm = c.AvgBarWidthPx / (double)(25.4 / c.Dpi),
                NarrowBarWidthMm = c.NarrowBarMm,
                BwrMm = bwrMm,
                BarCount = c.BarCount,
                QuietZoneLeftPass = leftQzMm >= minQuietZoneMm,
                QuietZoneRightPass = rightQzMm >= minQuietZoneMm,
                BwrPass = bwrMm <= settings.BarcodeBWRToleranceMm,
                Detail = BuildDetail(c, minQuietZoneMm, settings.BarcodeBWRToleranceMm),
                Focus = new FocusRegion("Barcode", c.X, c.Y, c.Width, c.Height)
            };
            results.Add(result);
        }

        return results;
    }

    // ----- Internal analysis structures -----

    private sealed class BarcodeCandidate
    {
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public int Dpi { get; init; }
        public BarcodeSymbology Symbology { get; init; }
        public int BarCount { get; init; }
        public int MinBarWidthPx { get; init; }
        public int MaxBarWidthPx { get; init; }
        public int AvgBarWidthPx { get; init; }
        public int NarrowBarPx { get; init; }
        public double NarrowBarMm { get; init; }
        public int BwrPx { get; init; }
        public int LeftQuietZonePx { get; init; }
        public int RightQuietZonePx { get; init; }
    }

    // ----- Scan line selection -----

    private static IEnumerable<int> SelectScanLines(int height, int minHeightPx)
    {
        // Scan at 5 evenly spaced lines within the middle 60% of the plate
        var start = height / 5;
        var end = height * 4 / 5;
        var count = 5;
        var step = Math.Max(1, (end - start) / count);
        for (var y = start; y < end; y += step)
            yield return y;
    }

    // ----- Transition detection -----

    private static List<int> FindTransitions(bool[] binary, int width, int y)
    {
        var transitions = new List<int>();
        var start = y * width;
        var last = binary[start];
        for (var x = 1; x < width; x++)
        {
            var current = binary[start + x];
            if (current != last)
            {
                transitions.Add(x);
                last = current;
            }
        }
        return transitions;
    }

    // ----- Transition analysis -----

    private static BarcodeCandidate? AnalyzeTransitions(List<int> transitions, int width, int y, int narrowBarPxInput, int dpi)
    {
        if (transitions.Count < 6)
            return null;

        // Calculate bar/space widths
        var widths = new List<int>();
        var prev = 0;
        foreach (var t in transitions)
        {
            widths.Add(t - prev);
            prev = t;
        }
        widths.Add(width - prev); // last space to edge

        // Need at least 6 bars (12 transitions = 13 widths)
        if (widths.Count < 13)
            return null;

        // Find narrow bar (smallest width that appears frequently)
        var minWidth = widths.Min();
        var narrowCandidates = widths.Where(w => w <= minWidth * 1.5).ToList();
        if (narrowCandidates.Count < 4)
            return null;

        var narrowBarPx = (int)Math.Round(narrowCandidates.Average());
        if (narrowBarPx < 1)
            narrowBarPx = 1;

        // Estimate barcode bounds (first bar to last bar)
        var firstBar = transitions.First();
        var lastBar = transitions.Last();
        var barcodeWidth = lastBar - firstBar;

        // Measure quiet zones
        var leftQz = firstBar;
        var rightQz = width - lastBar;

        // Calculate BWR: difference between expected narrow bar and actual narrow bars
        // BWR = nominal_narrow - actual_narrow (positive = bars shrunk)
        var nominalNarrow = narrowBarPxInput;
        var actualNarrows = widths.Where(w => w <= narrowBarPxInput * 1.5 && w > 0).ToList();
        var avgActualNarrow = actualNarrows.Count > 0 ? actualNarrows.Average() : nominalNarrow;
        var bwrPx = (int)Math.Round(nominalNarrow - avgActualNarrow);

        // Estimate symbology from pattern
        var symbology = GuessSymbology(widths, narrowBarPxInput);

        // Bar count = number of bars (dark elements)
        var barCount = widths.Select((w, idx) => new { w, idx }).Count(x => x.idx % 2 == 0 && x.w > 0);

        return new BarcodeCandidate
        {
            X = firstBar,
            Y = y, // placeholder, will be set by caller
            Width = barcodeWidth,
            Height = 1, // single scan line
            Dpi = dpi,
            Symbology = symbology,
            BarCount = barCount,
            MinBarWidthPx = widths.Where(w => w > 0).Min(),
            MaxBarWidthPx = widths.Max(),
            AvgBarWidthPx = (int)Math.Round(widths.Where(w => w > 0).Average()),
            NarrowBarPx = narrowBarPx,
            NarrowBarMm = narrowBarPx / (25.4 / (double)dpi),
            BwrPx = Math.Max(0, bwrPx),
            LeftQuietZonePx = leftQz,
            RightQuietZonePx = rightQz
        };
    }

    private static BarcodeSymbology GuessSymbology(List<int> widths, int narrowBarPx)
    {
        // Very basic heuristic based on number of bars and width ratios
        var barCount = widths.Select((w, idx) => new { w, idx }).Count(x => x.idx % 2 == 0 && x.w > 0);
        var maxWidth = widths.Max();
        var minNonZero = widths.Where(w => w > 0).Min();
        var ratio = maxWidth / (double)minNonZero;

        if (barCount == 30 && ratio < 3) return BarcodeSymbology.EAN13;
        if (barCount == 22 && ratio < 3) return BarcodeSymbology.EAN8;
        if (barCount == 24 && ratio < 3) return BarcodeSymbology.UPCA;
        if (barCount >= 20 && ratio >= 3) return BarcodeSymbology.Code128;
        if (barCount >= 16 && ratio >= 2.5) return BarcodeSymbology.ITF14;
        if (barCount >= 20 && ratio >= 2) return BarcodeSymbology.Code39;
        if (barCount >= 16 && ratio >= 2) return BarcodeSymbology.Codabar;
        return BarcodeSymbology.Unknown;
    }

    private static bool Overlaps(BarcodeCandidate a, BarcodeCandidate b)
    {
        var aLeft = a.X;
        var aRight = a.X + a.Width;
        var bLeft = b.X;
        var bRight = b.X + b.Width;
        return !(aRight < bLeft || bRight < aLeft);
    }

    private static string BuildDetail(BarcodeCandidate c, double minQuietZoneMm, double bwrToleranceMm)
    {
        return $"Symbology: {c.Symbology}, Bars: {c.BarCount}, " +
               $"Left QZ: {c.LeftQuietZonePx}px, Right QZ: {c.RightQuietZonePx}px, " +
               $"BWR: {c.BwrPx}px, Narrow bar: {c.NarrowBarPx}px";
    }
}