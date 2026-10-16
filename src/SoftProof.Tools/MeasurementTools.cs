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
        // For cyan: hue error = (M+Y)/2 / C, grayness = min(M,Y)/C
        // Simplified for single separation
        return (0, 0);
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
    public (float Slur, float Doubling) MeasureSlurDoubling(
        byte[] separationData, int width, int height, MeasurementRegion region)
    {
        // Would require analyzing edge sharpness and geometric distortion
        // Placeholder implementation
        return (0, 0);
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
    
    private float[] CmykToLab(float[] cmyk, IccProfile? profile)
    {
        // Simplified CMYK to Lab conversion (without ICC profile)
        // In production, use the color engine with proper profile
        
        var c = cmyk[0] / 100f;
        var m = cmyk[1] / 100f;
        var y = cmyk[2] / 100f;
        var k = cmyk[3] / 100f;
        
        // Rough CMYK -> RGB -> Lab
        var r = (1 - c) * (1 - k);
        var g = (1 - m) * (1 - k);
        var b = (1 - y) * (1 - k);
        
        // sRGB to XYZ
        var rLin = RgbToLinear(r);
        var gLin = RgbToLinear(g);
        var bLin = RgbToLinear(b);
        
        var X = rLin * 0.4124f + gLin * 0.3576f + bLin * 0.1805f;
        var Y = rLin * 0.2126f + gLin * 0.7152f + bLin * 0.0722f;
        var Z = rLin * 0.0193f + gLin * 0.1192f + bLin * 0.9505f;
        
        // XYZ to Lab (D65)
        var Xn = 0.95047f; var Yn = 1.0f; var Zn = 1.08883f;
        
        var fX = XyzToLabF(X / Xn);
        var fY = XyzToLabF(Y / Yn);
        var fZ = XyzToLabF(Z / Zn);
        
        var L = 116 * fY - 16;
        var a = 500 * (fX - fY);
        var bLab = 200 * (fY - fZ);
        
        return new[] { Math.Clamp(L, 0, 100), a, bLab };
    }
    
    private float RgbToLinear(float c)
    {
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }
    
    private float XyzToLabF(float t)
    {
        const float delta = 6f / 29f;
        return t > delta * delta * delta ? MathF.Cbrt(t) : t / (3 * delta * delta) + 4f / 29f;
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