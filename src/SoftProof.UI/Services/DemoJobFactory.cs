using SoftProof.Core.Models;

namespace SoftProof.UI.Services;

public sealed record DemoJob(PrintJob Job, IReadOnlyList<PlateLayer> Plates);

/// <summary>
/// Builds a synthetic 8-plate carton job (CMYK + two spots + spot varnish + dieline) so the viewer,
/// plate panel, densitometer and preflight can be exercised without a RIP file.
/// Page: 210 x 297 mm at 254 DPI (10 pixels per millimetre).
/// </summary>
public static class DemoJobFactory
{
    private const int Dpi = 254;
    private const double PxPerMm = Dpi / 25.4; // exactly 10
    private const int Width = 2100;
    private const int Height = 2970;
    
    public static DemoJob Create()
    {
        var cyan = new byte[Width * Height];
        var magenta = new byte[Width * Height];
        var yellow = new byte[Width * Height];
        var black = new byte[Width * Height];
        var gold = new byte[Width * Height];
        var blue = new byte[Width * Height];
        var gloss = new byte[Width * Height];
        var dieline = new byte[Width * Height];
        
        PaintGoldBanner(gold, black);
        PaintPhotoPanel(cyan, magenta, yellow, black);
        PaintBlueBand(blue);
        PaintGloss(gloss);
        PaintBarcode(black);
        PaintToneRamps(cyan, magenta, yellow, black);
        PaintColorBar(cyan, magenta, yellow, black, gold, blue, gloss);
        PaintDieline(dieline);
        
        var separations = new List<Separation>
        {
            new() { Name = "Cyan", Index = 0, Color = SeparationColor.ProcessCyan,
                    ScreenAngle = 15, ScreenLpi = 175, DotShape = "Euclidean" },
            new() { Name = "Magenta", Index = 1, Color = SeparationColor.ProcessMagenta,
                    ScreenAngle = 75, ScreenLpi = 175, DotShape = "Euclidean" },
            new() { Name = "Yellow", Index = 2, Color = SeparationColor.ProcessYellow,
                    ScreenAngle = 0, ScreenLpi = 175, DotShape = "Euclidean" },
            new() { Name = "Black", Index = 3, Color = SeparationColor.ProcessBlack,
                    ScreenAngle = 45, ScreenLpi = 175, DotShape = "Euclidean" },
            new() { Name = "PANTONE 871 C (Gold)", Index = 4, Color = SeparationColor.SpotColor,
                    IsSpotColor = true, SpotColorName = "PANTONE 871 C",
                    ScreenAngle = 45, ScreenLpi = 175, DotShape = "Round" },
            new() { Name = "PANTONE 2133 C (Reflex)", Index = 5, Color = SeparationColor.SpotColor,
                    IsSpotColor = true, SpotColorName = "PANTONE 2133 C",
                    ScreenAngle = 15, ScreenLpi = 175, DotShape = "Round" },
            new() { Name = "Spot UV Gloss", Index = 6, Color = SeparationColor.Varnish },
            new() { Name = "Technical Dieline", Index = 7, Color = SeparationColor.Red, IsTechnical = true }
        };
        
        var plates = new List<PlateLayer>
        {
            new(separations[0], Width, Height, cyan, 0, 174, 239),
            new(separations[1], Width, Height, magenta, 236, 0, 140),
            new(separations[2], Width, Height, yellow, 255, 242, 0),
            new(separations[3], Width, Height, black, 35, 31, 32),
            new(separations[4], Width, Height, gold, 200, 160, 40),
            new(separations[5], Width, Height, blue, 51, 102, 170),
            new(separations[6], Width, Height, gloss, 160, 90, 255),
            new(separations[7], Width, Height, dieline, 235, 64, 82)
        };
        
        foreach (var plate in plates)
            plate.ComputeStats();
        
        var job = new PrintJob
        {
            Name = "Demo_Carton_B1",
            FilePath = "(built-in demo)",
            Format = RipFormat.Unknown,
            Status = JobStatus.Ready,
            Separations = separations,
            Metadata = new JobMetadata
            {
                RipName = "SoftProof demo generator",
                RipVersion = "1.0",
                PressName = "Demo Press 106 (6-colour + coater)",
                Width = Width,
                Height = Height,
                Resolution = Dpi,
                BitDepth = 8,
                ColorSpace = ColorSpace.MultiChannel,
                SeparationNames = separations.Select(s => s.Name).ToList()
            },
            ProofSettings = new ProofSettings { ScreenFrequency = 175, ScreeningType = ScreeningType.AM }
        };
        
        return new DemoJob(job, plates);
    }
    
    // ---- artwork -----------------------------------------------------------------------------
    
    private static void PaintGoldBanner(byte[] gold, byte[] black)
    {
        Rect(gold, 30, 25, 180, 85, 255);
        Rect(black, 45, 40, 165, 48, 255);
        Rect(black, 60, 56, 150, 62, 255);
    }
    
    private static void PaintPhotoPanel(byte[] c, byte[] m, byte[] y, byte[] k)
    {
        Rect(k, 45, 110, 165, 190, Tone(18));
        Ellipse(c, 105, 150, 60, 40, t => Soft(Tone(80) * (1 - t * t)));
        Ellipse(m, 105, 150, 50, 33, t => Soft(Tone(35) * (1 - t)));
        Ellipse(y, 105, 150, 40, 28, t => Soft(Tone(24) * (1 - t * t * t)));
        Ellipse(k, 105, 150, 15, 15, _ => Tone(92));
    }
    
    private static void PaintBlueBand(byte[] blue)
    {
        Rect(blue, 30, 200, 180, 228, 255);
        Rect(blue, 30, 231, 180, 235, Tone(40));
    }
    
    private static void PaintGloss(byte[] gloss)
    {
        Ellipse(gloss, 105, 150, 24, 24, t => t >= 21.0 / 24.0 ? (byte)255 : (byte)0);
        Rect(gloss, 60, 236, 150, 241, 255);
    }
    
    private static void PaintBarcode(byte[] black)
    {
        double[] widths = { 0.4, 0.8, 0.4, 1.2, 0.4, 0.4, 0.8, 0.4, 1.2, 0.8 };
        var x = 72.0;
        var i = 0;
        while (x < 138.0)
        {
            var w = widths[i % widths.Length];
            Rect(black, x, 246, x + w, 268, 255);
            x += w + (i % 3 == 1 ? 0.8 : 0.4);
            i++;
        }
    }
    
    private static void PaintToneRamps(byte[] c, byte[] m, byte[] y, byte[] k)
    {
        int[] steps = { 3, 5, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
        var plates = new[] { c, m, y, k };
        for (var row = 0; row < plates.Length; row++)
        {
            var y0 = 270 + row * 4;
            for (var s = 0; s < steps.Length; s++)
            {
                var x0 = 33 + s * 12;
                Rect(plates[row], x0, y0, x0 + 12, y0 + 3.6, Tone(steps[s]));
            }
        }
    }
    
    private static void PaintColorBar(byte[] c, byte[] m, byte[] y, byte[] k, byte[] gold, byte[] blue, byte[] gloss)
    {
        var plates = new[] { c, m, y, k, gold, blue, gloss };
        for (var i = 0; i < plates.Length; i++)
            Rect(plates[i], 70 + i * 8, 8, 78 + i * 8, 14, 255);
    }
    
    private static void PaintDieline(byte[] d)
    {
        // Outline
        Rect(d, 5, 5, 205, 5.4, 255);
        Rect(d, 5, 291.6, 205, 292, 255);
        Rect(d, 5, 5, 5.4, 292, 255);
        Rect(d, 204.6, 5, 205, 292, 255);
        
        // Dashed fold lines
        foreach (var fy in new[] { 100.0, 200.0 })
        {
            for (var x = 5.0; x < 205.0; x += 12)
                Rect(d, x, fy - 0.15, Math.Min(x + 8, 205), fy + 0.15, 255);
        }
        
        // Registration marks
        foreach (var (cx, cy) in new[] { (14.0, 20.0), (196.0, 20.0), (14.0, 283.0), (196.0, 283.0) })
        {
            Rect(d, cx - 4, cy - 0.15, cx + 4, cy + 0.15, 255);
            Rect(d, cx - 0.15, cy - 4, cx + 0.15, cy + 4, 255);
            Ellipse(d, cx, cy, 2.6, 2.6, t => t >= 2.3 / 2.6 ? (byte)255 : (byte)0);
        }
    }
    
    // ---- drawing helpers ----------------------------------------------------------------------
    
    /// <summary>Percent tone to 8-bit coverage.</summary>
    private static byte Tone(double percent) => (byte)Math.Round(Math.Clamp(percent, 0, 100) * 2.55);
    
    /// <summary>Drops sub-2% values so smooth gradients end cleanly instead of leaving stray highlight dots.</summary>
    private static byte Soft(double value) => value < 8 ? (byte)0 : (byte)Math.Min(255, Math.Round(value));
    
    private static void Rect(byte[] plate, double x0, double y0, double x1, double y1, byte value)
    {
        var ix0 = Math.Clamp((int)Math.Round(x0 * PxPerMm), 0, Width);
        var ix1 = Math.Clamp((int)Math.Round(x1 * PxPerMm), 0, Width);
        var iy0 = Math.Clamp((int)Math.Round(y0 * PxPerMm), 0, Height);
        var iy1 = Math.Clamp((int)Math.Round(y1 * PxPerMm), 0, Height);
        
        for (var py = iy0; py < iy1; py++)
        {
            var row = py * Width;
            for (var px = ix0; px < ix1; px++)
            {
                if (plate[row + px] < value)
                    plate[row + px] = value;
            }
        }
    }
    
    /// <summary>Fills an ellipse; <paramref name="profile"/> maps normalised radius (0 centre .. 1 edge) to coverage.</summary>
    private static void Ellipse(byte[] plate, double cx, double cy, double rx, double ry, Func<double, byte> profile)
    {
        var ix0 = Math.Clamp((int)Math.Floor((cx - rx) * PxPerMm), 0, Width - 1);
        var ix1 = Math.Clamp((int)Math.Ceiling((cx + rx) * PxPerMm), 0, Width - 1);
        var iy0 = Math.Clamp((int)Math.Floor((cy - ry) * PxPerMm), 0, Height - 1);
        var iy1 = Math.Clamp((int)Math.Ceiling((cy + ry) * PxPerMm), 0, Height - 1);
        
        for (var py = iy0; py <= iy1; py++)
        {
            var dy = ((py + 0.5) / PxPerMm - cy) / ry;
            var row = py * Width;
            for (var px = ix0; px <= ix1; px++)
            {
                var dx = ((px + 0.5) / PxPerMm - cx) / rx;
                var t = Math.Sqrt(dx * dx + dy * dy);
                if (t > 1) continue;
                
                var v = profile(t);
                if (v > plate[row + px])
                    plate[row + px] = v;
            }
        }
    }
}
