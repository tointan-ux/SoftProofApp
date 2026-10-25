using SoftProof.Core.Models;
using SoftProof.Core.ColorPipeline;

namespace SoftProof.Core.Interfaces;

/// <summary>
/// Interface for RIP file parsers
/// </summary>
public interface IRipParser
{
    RipFormat SupportedFormat { get; }
    string[] SupportedExtensions { get; }
    
    /// <summary>
    /// Detect if file can be parsed by this parser
    /// </summary>
    Task<bool> CanParseAsync(string filePath, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Parse RIP file and return print job
    /// </summary>
    Task<PrintJob> ParseAsync(string filePath, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Parse multiple files (for multi-file separations)
    /// </summary>
    Task<PrintJob> ParseMultipleAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default);
}

/// <summary>
/// Interface for color management engine
/// </summary>
public interface IColorEngine
{
    /// <summary>
    /// Load ICC profile from file
    /// </summary>
    Task<IccProfile> LoadProfileAsync(string profilePath, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Create transform between two profiles
    /// </summary>
    Task<ColorTransform> CreateTransformAsync(
        IccProfile sourceProfile,
        IccProfile destProfile,
        RenderingIntent intent,
        bool blackPointCompensation,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Create proofing transform (display -> simulation)
    /// </summary>
    Task<ProofTransform> CreateProofTransformAsync(
        IccProfile displayProfile,
        IccProfile simulationProfile,
        ProofSettings settings,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Apply transform to bitmap
    /// </summary>
    Task<BitmapData> ApplyTransformAsync(
        ColorTransform transform,
        BitmapData source,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Apply proofing transform with TVI curves and ink limit enforcement
    /// </summary>
    Task<BitmapData> ApplyProofTransformAsync(
        ProofTransform proofTransform,
        BitmapData source,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Calculate DeltaE between two colors
    /// </summary>
    DeltaEResult CalculateDeltaE(float[] lab1, float[] lab2, DeltaEFormula formula = DeltaEFormula.CIE2000);
}

/// <summary>
/// ICC Profile wrapper
/// </summary>
public class IccProfile : IDisposable
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ColorSpace ColorSpace { get; set; }
    public ProfileClass ProfileClass { get; set; }
    public int Version { get; set; }
    public byte[] RawData { get; set; } = Array.Empty<byte>();
    public IntPtr Handle { get; set; } = IntPtr.Zero;
    public bool IsDisposed { get; private set; }
    
    public void Dispose()
    {
        if (!IsDisposed && Handle != IntPtr.Zero)
        {
            // Native handle cleanup handled by color engine
            Handle = IntPtr.Zero;
            IsDisposed = true;
        }
        GC.SuppressFinalize(this);
    }
    
    ~IccProfile() => Dispose();
}

/// <summary>
/// ICC Profile class
/// </summary>
public enum ProfileClass
{
    InputDevice,
    DisplayDevice,
    OutputDevice,
    Link,
    Abstract,
    ColorSpace,
    NamedColor
}

/// <summary>
/// Color transform
/// </summary>
public class ColorTransform : IDisposable
{
    public IntPtr Handle { get; set; } = IntPtr.Zero;
    public IccProfile SourceProfile { get; set; } = null!;
    public IccProfile DestProfile { get; set; } = null!;
    public RenderingIntent Intent { get; set; }
    public bool BlackPointCompensation { get; set; }
    public bool IsDisposed { get; private set; }
    
    public void Dispose()
    {
        if (!IsDisposed && Handle != IntPtr.Zero)
        {
            Handle = IntPtr.Zero;
            IsDisposed = true;
        }
        GC.SuppressFinalize(this);
    }
    
    ~ColorTransform() => Dispose();
}

/// <summary>
/// Proofing transform (includes simulation + display)
/// </summary>
public class ProofTransform : IDisposable
{
    public ColorTransform? SimulationTransform { get; set; }
    public ColorTransform? DisplayTransform { get; set; }
    public ProofSettings Settings { get; set; } = new();
    public TVIPipelineStage? TVIStage { get; set; }
    public InkLimitStage? InkLimitStage { get; set; }
    public bool IsDisposed { get; private set; }
    
    public void Dispose()
    {
        if (!IsDisposed)
        {
            SimulationTransform?.Dispose();
            DisplayTransform?.Dispose();
            IsDisposed = true;
        }
        GC.SuppressFinalize(this);
    }
    
    ~ProofTransform() => Dispose();
}

/// <summary>
/// Bitmap data for processing
/// </summary>
public class BitmapData
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int Stride { get; set; }
    public PixelFormat Format { get; set; }
    public byte[] Pixels { get; set; } = Array.Empty<byte>();
    public IntPtr Pointer { get; set; } = IntPtr.Zero;
    public bool IsPinned { get; set; }
}

/// <summary>
/// Pixel formats supported
/// </summary>
public enum PixelFormat
{
    Gray8,
    Gray16,
    RGB24,
    RGB48,
    CMYK32,
    CMYK64,
    Lab32,
    Lab48,
    BGRA32
}

/// <summary>
/// DeltaE calculation formulas
/// </summary>
public enum DeltaEFormula
{
    CIE76,
    CIE94,
    CIE2000,
    CMC
}

/// <summary>
/// DeltaE calculation result
/// </summary>
public class DeltaEResult
{
    public float DeltaE { get; set; }
    public float DeltaL { get; set; }
    public float DeltaC { get; set; }
    public float DeltaH { get; set; }
    public DeltaEFormula Formula { get; set; }
}