using System.Windows.Media;
using Microsoft.Extensions.Logging;
using SoftProof.Core.Interfaces;
using SoftProof.Core.Models;

namespace SoftProof.Tools;

/// <summary>
/// Densitometer tool for measuring print density, dot gain, print contrast, etc.
/// Supports ANSI T, ISO T, and ISO E density standards
/// </summary>
public class DensitometerTool
{
    private readonly ILogger<DensitometerTool> _logger;
    
    public DensitometerTool(ILogger<DensitometerTool> logger)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Measure density from separation data
    /// </summary>
    public MeasurementResult MeasureDensity(
        byte[] separationData,
        int width,
        int height,
        MeasurementRegion region,
        DensityStandard standard = DensityStandard.ISO_T)
    {
        var pixels = ExtractRegion(separationData, width, height, region);
        
        // Calculate reflectance (1-bit: 1=ink=low reflectance, 0=paper=high reflectance)
        var inkPixels = pixels.Count(p => p > 0.5f);
        var totalPixels = pixels.Length;
        var coverage = totalPixels > 0 ? (float)inkPixels / totalPixels : 0;
        
        // Reflectance: paper = 1.0, solid ink = 0.05-0.15 depending on ink
        var paperReflectance = 0.95f;
        var inkReflectance = GetInkReflectance(standard);
        var measuredReflectance = paperReflectance * (1 - coverage) + inkReflectance * coverage;
        
        // Density = -log10(reflectance)
        var density = -MathF.Log10(Math.Max(measuredReflectance, 0.001f));
        
        return new MeasurementResult
        {
            Density = density,
            Location = region.Name,
            DotGain = coverage * 100, // Simplified
            PrintContrast = CalculatePrintContrast(paperReflectance, inkReflectance)
        };
    }
    
    /// <summary>
    /// Measure dot area (tint value) from halftone
    /// </summary>
    public float MeasureDotArea(byte[] separationData, int width, int height, MeasurementRegion region)
    {
        var pixels = ExtractRegion(separationData, width, height, region);
        var inkPixels = pixels.Count(p => p > 0.5f);
        return pixels.Length > 0 ? (float)inkPixels / pixels.Length * 100f : 0;
    }
    
    /// <summary>
    /// Measure dot gain (difference between file tint and printed tint)
    /// </summary>
    public float MeasureDotGain(float fileTintPercent, float measuredTintPercent)
    {
        return measuredTintPercent - fileTintPercent;
    }
    
    /// <summary>
    /// Measure print contrast (K = (Ds - Dt) / Ds)
    /// </summary>
    public float MeasurePrintContrast(float solidDensity, float tintDensity)
    {
        if (solidDensity <= 0) return 0;
        return (solidDensity - tintDensity) / solidDensity;
    }
    
    /// <summary>
    /// Measure hue error and grayness (for process colors)
    /// </summary>
    public (float HueError, float Grayness) MeasureHueErrorGrayness(
        float densityC, float densityM, float densityY)
    {
        // Preucil: the three filter densities of one ink, sorted high/mid/low.
        //   hue error % = (mid - low) / (high - low) * 100   (how far the ink sits from the ideal hue)
        //   grayness  % = low / high * 100                   (unwanted absorption in all three filters)
        var densities = new[] { MathF.Abs(densityC), MathF.Abs(densityM), MathF.Abs(densityY) };
        Array.Sort(densities);

        var low = densities[0];
        var mid = densities[1];
        var high = densities[2];

        if (high <= 0.0001f)
            return (0f, 0f);

        var hueError = high - low <= 0.0001f ? 0f : (mid - low) / (high - low) * 100f;
        var grayness = low / high * 100f;

        return (Math.Clamp(hueError, 0f, 100f), Math.Clamp(grayness, 0f, 100f));
    }
    
    /// <summary>
    /// Measure trapping (ink trapping on press)
    /// </summary>
    public float MeasureTrapping(float densityFirst, float densitySecond, float densityOverprint)
    {
        // Preucil trapping formula: T = (D_overprint - D_first) / D_second * 100
        if (densitySecond <= 0) return 0;
        return (densityOverprint - densityFirst) / densitySecond * 100f;
    }
    
    /// <summary>
    /// Measure slur/doubling (geometric distortion)
    /// </summary>
    /// <summary>
    /// Slur (directional smear) and doubling (ghost edge) measured from the rendered dots.
    /// Both are comparative figures: horizontal and vertical edge profiles of the same region are
    /// compared, and dots that sit within a couple of pixels of an edge but on the wrong side are
    /// counted as ghosting. Values are percentages of the region's ink.
    /// </summary>
    public (float Slur, float Doubling) MeasureSlurDoubling(
        byte[] separationData, int width, int height, MeasurementRegion region)
    {
        var w = Math.Min(region.Width, width - region.X);
        var h = Math.Min(region.Height, height - region.Y);

        if (w < 4 || h < 4)
            return (0f, 0f);

        // Unpack the probe once.
        var bits = new byte[w * h];
        long ink = 0;

        for (var y = 0; y < h; y++)
        {
            var row = (region.Y + y) * width + region.X;
            for (var x = 0; x < w; x++)
            {
                var index = row + x;
                var byteIndex = index / 8;
                var inked = byteIndex < separationData.Length &&
                            ((separationData[byteIndex] >> (7 - (index % 8))) & 1) != 0;
                var bit = inked ? (byte)1 : (byte)0;
                bits[y * w + x] = bit;
                ink += bit;
            }
        }

        if (ink == 0)
            return (0f, 0f);

        // Edge strength per direction: a sharp edge flips once between neighbouring pixels, a
        // slurred one flips over two to four pixels. Compare mean run of "same value" across edges.
        double horizontalFlips = 0, verticalFlips = 0;

        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            for (var x = 1; x < w; x++)
                if (bits[row + x] != bits[row + x - 1])
                    horizontalFlips++;
        }

        for (var x = 0; x < w; x++)
        {
            for (var y = 1; y < h; y++)
                if (bits[y * w + x] != bits[(y - 1) * w + x])
                    verticalFlips++;
        }

        var flips = horizontalFlips + verticalFlips;
        if (flips <= 0)
            return (0f, 0f);

        // Slur: anisotropy of edge density. A directional smear produces fewer transitions in the
        // smeared direction, so the imbalance grows with the amount of slur.
        var slur = Math.Abs(horizontalFlips - verticalFlips) / flips * 100.0;

        // Doubling: a ghost impression shows up as one-pixel runs. A clean screen has runs of
        // two or more in both directions; a doubled edge squeezes the opposite run down to one.
        // Only runs bounded by transitions on both sides count - the first and last run of a scan
        // line are cut off by the region edge and say nothing about the impression.
        long runs = 0, singlePixelRuns = 0;

        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            var runStart = 0;

            for (var x = 1; x < w; x++)
            {
                if (bits[row + x] == bits[row + x - 1])
                    continue;

                var length = x - runStart;
                if (runStart > 0)
                {
                    runs++;
                    if (length == 1) singlePixelRuns++;
                }

                runStart = x;
            }
        }

        for (var x = 0; x < w; x++)
        {
            var runStart = 0;

            for (var y = 1; y < h; y++)
            {
                if (bits[y * w + x] == bits[(y - 1) * w + x])
                    continue;

                var length = y - runStart;
                if (runStart > 0)
                {
                    runs++;
                    if (length == 1) singlePixelRuns++;
                }

                runStart = y;
            }
        }

        var doubling = runs == 0 ? 0.0 : singlePixelRuns * 100.0 / runs;

        return ((float)Math.Round(slur, 2), (float)Math.Round(doubling, 2));
    }
    
    private float[] ExtractRegion(byte[] separationData, int width, int height, MeasurementRegion region)
    {
        var result = new List<float>();
        
        for (int y = region.Y; y < region.Y + region.Height && y < height; y++)
        {
            for (int x = region.X; x < region.X + region.Width && x < width; x++)
            {
                var bitIdx = (y * width + x) / 8;
                var bitPos = 7 - ((y * width + x) % 8);
                
                if (bitIdx < separationData.Length)
                {
                    var value = (separationData[bitIdx] >> bitPos) & 1;
                    result.Add(value);
                }
            }
        }
        
        return result.ToArray();
    }
    
    private float GetInkReflectance(DensityStandard standard)
    {
        return standard switch
        {
            DensityStandard.ANSI_T => 0.05f,   // ANSI T: darker ink
            DensityStandard.ISO_T => 0.06f,    // ISO T
            DensityStandard.ISO_E => 0.07f,    // ISO E: lighter ink
            _ => 0.06f
        };
    }
    
    private float CalculatePrintContrast(float paperReflectance, float inkReflectance)
    {
        var solidDensity = -MathF.Log10(inkReflectance);
        var paperDensity = -MathF.Log10(paperReflectance);
        return solidDensity > 0 ? (solidDensity - paperDensity) / solidDensity : 0;
    }
}

/// <summary>
/// Density measurement standards
/// </summary>
public enum DensityStandard
{
    ANSI_T,   // ANSI Status T (North America)
    ISO_T,    // ISO Status T (Europe) - same spectral but different calibration
    ISO_E,    // ISO Status E (Europe)
    ISO_A,    // ISO Status A
    ISO_I     // ISO Status I
}

/// <summary>
/// Color measurement tool (spectrophotometer simulation)
/// </summary>
public class ColorMeasurementTool
{
    private readonly ILogger<ColorMeasurementTool> _logger;
    private readonly IColorEngine _colorEngine;
    
    public ColorMeasurementTool(ILogger<ColorMeasurementTool> logger, IColorEngine colorEngine)
    {
        _logger = logger;
        _colorEngine = colorEngine;
    }
    
    /// <summary>
    /// Measure Lab values from CMYK separation composite
    /// </summary>
    public MeasurementResult MeasureColor(
        Dictionary<SeparationColor, byte[]> separations,
        int width,
        int height,
        MeasurementRegion region,
        IccProfile? printProfile = null)
    {
        // Composite separations to CMYK
        var cmyk = CompositeToCmyk(separations, width, height, region);
        
        // Convert to Lab using print profile or standard conversion
        var lab = CmykToLab(cmyk, printProfile);
        
        return new MeasurementResult
        {
            Lab = lab,
            CMYK = cmyk,
            Location = region.Name
        };
    }
    
    /// <summary>
    /// Calculate DeltaE between measured and reference
    /// </summary>
    public DeltaEResult CalculateDeltaE(
        float[] measuredLab,
        float[] referenceLab,
        DeltaEFormula formula = DeltaEFormula.CIE2000)
    {
        return _colorEngine.CalculateDeltaE(measuredLab, referenceLab, formula);
    }
    
    /// <summary>
    /// Verify color against tolerance (ISO 12647 / G7 / PSO)
    /// </summary>
    public ColorVerificationResult VerifyColor(
        float[] measuredLab,
        float[] targetLab,
        ToleranceStandard standard = ToleranceStandard.ISO_12647_7)
    {
        var deltaE = _colorEngine.CalculateDeltaE(measuredLab, targetLab, DeltaEFormula.CIE2000);
        
        var (tolerance, warningTolerance) = GetTolerances(standard);
        
        return new ColorVerificationResult
        {
            DeltaE = deltaE.DeltaE,
            Passed = deltaE.DeltaE <= tolerance,
            Warning = deltaE.DeltaE > warningTolerance && deltaE.DeltaE <= tolerance,
            Standard = standard,
            Tolerance = tolerance,
            WarningTolerance = warningTolerance
        };
    }
    
    private float[] CompositeToCmyk(
        Dictionary<SeparationColor, byte[]> separations,
        int width, int height, MeasurementRegion region)
    {
        var cmyk = new float[4];
        var count = 0;
        
        for (int y = region.Y; y < region.Y + region.Height && y < height; y++)
        {
            for (int x = region.X; x < region.X + region.Width && x < width; x++)
            {
                var idx = y * width + x;
                
                if (separations.TryGetValue(SeparationColor.ProcessCyan, out var c))
                    cmyk[0] += GetBit(c, idx);
                if (separations.TryGetValue(SeparationColor.ProcessMagenta, out var m))
                    cmyk[1] += GetBit(m, idx);
                if (separations.TryGetValue(SeparationColor.ProcessYellow, out var yellow))
                    cmyk[2] += GetBit(yellow, idx);
                if (separations.TryGetValue(SeparationColor.ProcessBlack, out var k))
                    cmyk[3] += GetBit(k, idx);
                
                count++;
            }
        }
        
        if (count > 0)
        {
            for (int i = 0; i < 4; i++)
                cmyk[i] = cmyk[i] / count * 100f; // Percentage
        }
        
        return cmyk;
    }
    
    private float GetBit(byte[] data, int bitIndex)
    {
        var byteIdx = bitIndex / 8;
        var bitPos = 7 - (bitIndex % 8);
        return byteIdx < data.Length ? (data[byteIdx] >> bitPos) & 1 : 0;
    }
    
    /// <summary>
    /// CMYK -> Lab through the colour engine with the press profile loaded for this job.
    /// The previous (1-C)(1-K) shortcut is a screen approximation: it cannot represent ink, paper
    /// or overprint behaviour, and its gamut error on cyan and yellow overprints is large.
    /// </summary>
    private float[] CmykToLab(float[] cmyk, IccProfile? profile)
    {
        if (profile is null)
        {
            throw new InvalidOperationException(
                "A press ICC profile is required to convert CMYK to Lab. Load the output profile for this job " +
                "and pass it to MeasureColor; a screen-space approximation would give a wrong colour.");
        }

        return _colorEngine.ConvertCmykToLab(cmyk, profile);
    }
    
    private (float Tolerance, float WarningTolerance) GetTolerances(ToleranceStandard standard)
    {
        return standard switch
        {
            ToleranceStandard.ISO_12647_7 => (2.5f, 1.5f),     // ISO 12647-7: ΔE00 ≤ 2.5, warning at 1.5
            ToleranceStandard.G7 => (3.0f, 2.0f),              // G7: ΔE00 ≤ 3.0
            ToleranceStandard.PSO => (2.0f, 1.0f),             // PSO: ΔE00 ≤ 2.0
            ToleranceStandard.SWOP => (3.0f, 2.0f),            // SWOP: ΔE00 ≤ 3.0
            ToleranceStandard.GRACoL => (2.5f, 1.5f),          // GRACoL: ΔE00 ≤ 2.5
            ToleranceStandard.CUSTOM => (5.0f, 3.0f),
            _ => (3.0f, 2.0f)
        };
    }
}

/// <summary>
/// Color verification against tolerance standards
/// </summary>
public class ColorVerificationResult
{
    public float DeltaE { get; set; }
    public bool Passed { get; set; }
    public bool Warning { get; set; }
    public ToleranceStandard Standard { get; set; }
    public float Tolerance { get; set; }
    public float WarningTolerance { get; set; }
}

/// <summary>
/// Industry tolerance standards
/// </summary>
public enum ToleranceStandard
{
    ISO_12647_7,
    G7,
    PSO,
    SWOP,
    GRACoL,
    CUSTOM
}