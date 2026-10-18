using System;
using System.Collections.Generic;

namespace SoftProof.Core.Models;

/// <summary>
/// Represents a print job from a RIP
/// </summary>
public class PrintJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public RipFormat Format { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public JobMetadata Metadata { get; set; } = new();
    public List<Separation> Separations { get; set; } = new();
    public ProofSettings ProofSettings { get; set; } = new();
}

/// <summary>
/// Supported RIP output formats
/// </summary>
public enum RipFormat
{
    Unknown,
    HarlequinPageBuffer,    // .pbuf, .hqn
    OneBitTiffSeparations,  // 1-bit TIFF per separation
    MultiChannelTiff,       // Multi-channel TIFF
    LenFile,                // Esko LEN format
    PresstekDI,             // Presstek DI format
    PDF,                    // PDF/X
}

/// <summary>
/// Job processing status
/// </summary>
public enum JobStatus
{
    Pending,
    Loading,
    Ready,
    Proofing,
    Approved,
    Rejected,
    Error
}

/// <summary>
/// Metadata extracted from RIP job
/// </summary>
public class JobMetadata
{
    public string RipName { get; set; } = string.Empty;
    public string RipVersion { get; set; } = string.Empty;
    public string PressName { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public int Resolution { get; set; } = 2400; // DPI
    public int BitDepth { get; set; } = 1;
    public ColorSpace ColorSpace { get; set; } = ColorSpace.CMYK;
    public List<string> SeparationNames { get; set; } = new();
    public Dictionary<string, object> CustomProperties { get; set; } = new();
}

/// <summary>
/// Color space of the job
/// </summary>
public enum ColorSpace
{
    Gray,
    RGB,
    CMYK,
    Lab,
    MultiChannel,
    DeviceN
}

/// <summary>
/// Individual separation (plate) in a job
/// </summary>
public class Separation
{
    public string Name { get; set; } = string.Empty;
    public int Index { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public SeparationColor Color { get; set; } = SeparationColor.ProcessCyan;
    public bool IsSpotColor { get; set; }
    public string SpotColorName { get; set; } = string.Empty;
    public string SpotColorAlternateSpace { get; set; } = string.Empty;
    public float[]? SpotColorAlternateValues { get; set; }

    // Harlequin PBF raster data properties
    public int DataOffset { get; set; }
    public int DataSize { get; set; }
    public byte[]? Data { get; set; }

    /// <summary>Page index inside a multi-page TIFF (one page per plate); 0 otherwise.</summary>
    public int PageIndex { get; set; }

    /// <summary>Screen angle in degrees as reported by the RIP (meaningful only if <see cref="ScreenLpi"/> &gt; 0).</summary>
    public float ScreenAngle { get; set; }
    
    /// <summary>Screen ruling in lines per inch; 0 = unknown / not screened (solid or line art).</summary>
    public float ScreenLpi { get; set; }
    
    /// <summary>Dot shape name as reported by the RIP, e.g. "Euclidean" or "Round".</summary>
    public string DotShape { get; set; } = string.Empty;
    
    /// <summary>Technical plate (dieline, registration, etc.) that is not printed in ink.</summary>
    public bool IsTechnical { get; set; }
}

/// <summary>
/// Standard separation colors
/// </summary>
public enum SeparationColor
{
    ProcessCyan,
    ProcessMagenta,
    ProcessYellow,
    ProcessBlack,
    SpotColor,
    Orange,
    Green,
    Violet,
    Red,
    Blue,
    White,
    Varnish,
    Primer
}

/// <summary>
/// Soft proofing settings (simulation parameters)
/// </summary>
public class ProofSettings
{
    // Display profile (monitor)
    public string DisplayProfilePath { get; set; } = string.Empty;
    
    // Print simulation profile (target press/proof)
    public string SimulationProfilePath { get; set; } = string.Empty;
    
    // Rendering intent for simulation
    public RenderingIntent RenderingIntent { get; set; } = RenderingIntent.RelativeColorimetric;
    
    // Black point compensation
    public bool BlackPointCompensation { get; set; } = true;
    
    // Simulate paper color (white point)
    public bool SimulatePaperColor { get; set; } = true;
    
    // Simulate black ink
    public bool SimulateBlackInk { get; set; } = true;
    
    // Ink limiting
    public float InkLimit { get; set; } = 300.0f; // %
    
    // Dot gain compensation
    public float DotGain { get; set; } = 0.0f; // %
    
    // Screen frequency for simulation
    public float ScreenFrequency { get; set; } = 175.0f; // LPI
    
    // Screening type
    public ScreeningType ScreeningType { get; set; } = ScreeningType.AM;
    
    // Custom device link profile (optional)
    public string DeviceLinkProfilePath { get; set; } = string.Empty;

    /// <summary>
    /// Preflight thresholds for the press this profile describes. A web press with a wide slug
    /// band and a sheetfed press with a narrow one must not share the same numbers.
    /// </summary>
    public PreflightSettings Preflight { get; set; } = new();
}

/// <summary>
/// Per-press preflight thresholds. Defaults match the conservative values the checks shipped
/// with; override them on the press profile (or per job) without code changes.
/// </summary>
public sealed class PreflightSettings
{
    /// <summary>Label for the press these numbers belong to, shown in warnings.</summary>
    public string PressName { get; set; } = string.Empty;

    /// <summary>Width of the slug / marks band around the trim area, in millimetres.</summary>
    public double SlugBandMm { get; set; } = 3.0;

    /// <summary>Below this share of inked pixels a plate is "suspiciously little content" (percent).</summary>
    public double TinyInkedPixelPercent { get; set; } = 0.5;

    /// <summary>Below this average tone a tiny plate is reported (percent).</summary>
    public double TinyCoveragePercent { get; set; } = 2.0;

    /// <summary>Above this coverage a plate is advised as possibly inverted (percent).</summary>
    public double NearSolidPercent { get; set; } = 99.9;

    /// <summary>Coverage a sibling process plate needs to count as having real ink (percent).</summary>
    public double MeaningfulCoveragePercent { get; set; } = 1.0;

    /// <summary>Below this coverage a sibling is reported as a likely missing separation (percent).</summary>
    public double MissingSeparationPercent { get; set; } = 0.05;

    /// <summary>Paper hairline width (mm) that counts as a trap gap between two inks.</summary>
    public double TrapGapMm { get; set; } = 0.15;

    /// <summary>How many gap pixels must be found before the trap check warns.</summary>
    public double TrapGapMinPixels { get; set; } = 4;

    /// <summary>
    /// Copy with values clamped to sane ranges. A typo (negative band, swapped limits) must not
    /// turn every plate into a warning, so bad input collapses to a usable range instead.
    /// </summary>
    public PreflightSettings Normalized() => new()
    {
        PressName = PressName,
        SlugBandMm = Math.Clamp(SlugBandMm, 0, 25),
        TinyInkedPixelPercent = Math.Clamp(TinyInkedPixelPercent, 0, 100),
        TinyCoveragePercent = Math.Clamp(TinyCoveragePercent, 0, 100),
        NearSolidPercent = Math.Clamp(NearSolidPercent, 90, 100),
        MeaningfulCoveragePercent = Math.Clamp(MeaningfulCoveragePercent, 0, 100),
        MissingSeparationPercent = Math.Clamp(MissingSeparationPercent, 0, MeaningfulCoveragePercent),
        TrapGapMm = Math.Clamp(TrapGapMm, 0.01, 2.0),
        TrapGapMinPixels = Math.Clamp(TrapGapMinPixels, 1, 1_000_000)
    };
}

/// <summary>
/// ICC Rendering intents
/// </summary>
public enum RenderingIntent
{
    Perceptual = 0,
    RelativeColorimetric = 1,
    Saturation = 2,
    AbsoluteColorimetric = 3
}

/// <summary>
/// Screening types
/// </summary>
public enum ScreeningType
{
    AM,           // Amplitude Modulated (conventional)
    FM,           // Frequency Modulated (stochastic)
    Hybrid,       // Hybrid AM/FM
    HDS,          // High Definition Screening (stochastic)
    Crystal,      // Crystal screening
    Sublima       // Sublima screening
}

/// <summary>
/// Measurement result from densitometer/colorimeter
/// </summary>
public class MeasurementResult
{
    public float Density { get; set; }
    public float[] Lab { get; set; } = new float[3];
    public float[] CMYK { get; set; } = new float[4];
    public float DeltaE { get; set; }
    public float DeltaE2000 { get; set; }
    public float DotGain { get; set; }
    public float PrintContrast { get; set; }
    public float HueError { get; set; }
    public float Grayness { get; set; }
    public string Location { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Region of interest for measurement
/// </summary>
public class MeasurementRegion
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Name { get; set; } = string.Empty;
}