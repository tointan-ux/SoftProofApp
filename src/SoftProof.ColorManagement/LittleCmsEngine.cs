using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SoftProof.Core.Interfaces;
using SoftProof.Core.Models;

namespace SoftProof.ColorManagement;

/// <summary>
/// LittleCMS (lcms2) native interop
/// </summary>
internal static class LcmsNative
{
    private const string LibName = "lcms2";
    
    static LcmsNative()
    {
        // Resolve lcms2.dll explicitly so the app works whether the file sits next to the exe
        // or in runtimes\win-x64\native, and so a missing file gives an actionable error.
        NativeLibrary.SetDllImportResolver(typeof(LcmsNative).Assembly, ResolveLcms);
    }
    
    private static IntPtr ResolveLcms(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibName, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero; // not ours: use default probing
        
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "lcms2.dll"),
            Path.Combine(baseDir, "runtimes", "win-x64", "native", "lcms2.dll")
        };
        
        foreach (var path in candidates)
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                return handle;
        }
        
        throw new DllNotFoundException(
            "Could not load lcms2.dll (Little CMS, 64-bit). Looked in:\n  " +
            string.Join("\n  ", candidates) +
            "\nMake sure lcms2.dll is next to SoftProof.exe and the process is 64-bit.");
    }
    
    // Profile handling
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr cmsOpenProfileFromFile(string filename, string access);
    
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr cmsOpenProfileFromMem(IntPtr memPtr, UIntPtr dwSize);
    
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern int cmsCloseProfile(IntPtr hProfile);
    
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr cmsCreateProfilePlaceholder(IntPtr context);
    
    // Profile info
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern int cmsGetProfileInfo(IntPtr hProfile, int infoType, string languageCode, string countryCode, IntPtr buffer, int bufferSize);
    
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern int cmsGetColorSpace(IntPtr hProfile);
    
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern int cmsGetDeviceClass(IntPtr hProfile);
    
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern int cmsGetEncodedICCversion(IntPtr hProfile);
    
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern int cmsSaveProfileToFile(IntPtr hProfile, string filename);
    
    // Transform creation
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr cmsCreateTransform(
        IntPtr inputProfile, int inputFormat,
        IntPtr outputProfile, int outputFormat,
        int intent, int flags);
    
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr cmsCreateProofingTransform(
        IntPtr inputProfile, int inputFormat,
        IntPtr outputProfile, int outputFormat,
        IntPtr proofingProfile, int proofingFormat,
        int intent, int proofingIntent, int flags);
    
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern int cmsDeleteTransform(IntPtr hTransform);
    
    // Transform execution
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern int cmsDoTransform(IntPtr hTransform, IntPtr input, IntPtr output, UIntPtr size);
    
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern int cmsDoTransformLineStride(IntPtr hTransform, IntPtr input, IntPtr output, UIntPtr pixelsPerLine, UIntPtr nLines, int inputStride, int outputStride);
    
    // Formatters
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern int cmsFormatterForColorspace(int colorSpace, int bits, int isFloat);
    
    // Color space signatures
    public const int cmsSigGrayData = 0x47524159;    // 'GRAY'
    public const int cmsSigRGBData = 0x52474220;     // 'RGB '
    public const int cmsSigCMYKData = 0x434D594B;    // 'CMYK'
    public const int cmsSigLabData = 0x4C616220;     // 'Lab '
    public const int cmsSigXYZData = 0x58595A20;     // 'XYZ '
    public const int cmsSigNamedColorData = 0x6E636F6C; // 'ncol'
    
    // Profile class signatures
    public const int cmsSigInputClass = 0x73636E72;  // 'scnr'
    public const int cmsSigDisplayClass = 0x6D6E7472; // 'mntr'
    public const int cmsSigOutputClass = 0x70727472;  // 'prtr'
    public const int cmsSigLinkClass = 0x6C696E6B;    // 'link'
    public const int cmsSigAbstractClass = 0x61627374; // 'abst'
    public const int cmsSigColorSpaceClass = 0x73706163; // 'spac'
    public const int cmsSigNamedColorClass = 0x6E636F6C; // 'ncol'
    
    // Intent values
    public const int INTENT_PERCEPTUAL = 0;
    public const int INTENT_RELATIVE_COLORIMETRIC = 1;
    public const int INTENT_SATURATION = 2;
    public const int INTENT_ABSOLUTE_COLORIMETRIC = 3;
    
    // Flags
    public const int cmsFLAGS_BLACKPOINTCOMPENSATION = 0x00002000;
    public const int cmsFLAGS_NOWHITEONWHITEFIXUP = 0x00004000;
    public const int cmsFLAGS_HIGHRESPRECALC = 0x00008000;
    public const int cmsFLAGS_LOWRESPRECALC = 0x00010000;
    public const int cmsFLAGS_SOFTPROOFING = 0x00040000;
    
    // Formatter bit-layout helpers.
    // In lcms2 these _SH constants are SHIFT AMOUNTS (bit positions), not
    // ready-to-OR flag values.  The old code OR'd them directly, which set
    // the wrong bits and produced garbage pixel-format descriptors.
    // Correct usage:  (1 << FLOAT_SH) to set the float flag, etc.
    public static int BYTES_SH(int x) => x;
    public static int CHANNELS_SH(int x) => x << 3;
    public static int FORMATTER_FLAGS_SH(int x) => x << 16;
    public static int COLORSPACE_SH(int x) => x << 16;

    // Shift amounts for formatter flags (per lcms2 cms.h)
    public const int FLOAT_SH = 0;
    public const int LITTLE_ENDIAN_SH = 1;
    public const int SWAP_FIRST_SH = 2;
    public const int OPTIMIZED_SH = 3;
    public const int PLANAR_SH = 4;
    public const int CAN_CHANGE_FORMATTER_SH = 5;
    public const int DO_NOT_CHECK_SH = 6;
    public const int EXTRA_SH = 7;
    public const int ALPHA_FIRST_SH = 8;
    public const int EXTRA_ALPHA_FIRST_SH = 9;
    public const int GUESS_DEVICE_CLASS_SH = 10;
    public const int KEEP_SEQUENCE_SH = 11;

    // Pre-shifted flag values for convenience
    public const int FLOAT_BIT = 1 << FLOAT_SH;          // 0x0001
    public const int OPTIMIZED_BIT = 1 << OPTIMIZED_SH;  // 0x0008
    
    // Error handling
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr cmsGetErrorHandler(IntPtr context);
    
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern void cmsSetErrorHandler(IntPtr context, IntPtr handler);
    
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr cmsCreateContext(IntPtr userData, IntPtr plugin);
    
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern void cmsDeleteContext(IntPtr context);
    
    // Profile iteration
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern int cmsGetProfileInfoASCII(IntPtr hProfile, int infoType, string languageCode, string countryCode, IntPtr buffer, int bufferSize);
    
    // DeltaE
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern double cmsDeltaE(IntPtr Lab1, IntPtr Lab2);
    
    // NOTE: lcms2 exports this as cmsCIE2000DeltaE (there is no export named cmsDeltaE2000).
    [DllImport("lcms2", EntryPoint = "cmsCIE2000DeltaE", CallingConvention = CallingConvention.Cdecl)]
    public static extern double cmsDeltaE2000(IntPtr Lab1, IntPtr Lab2, double Kl, double Kc, double Kh);
}

/// <summary>
/// LittleCMS-based color engine implementation
/// </summary>
public class LittleCmsEngine : IColorEngine, IDisposable
{
    private readonly ILogger<LittleCmsEngine> _logger;
    private readonly IntPtr _context;
    private bool _disposed;
    
    public LittleCmsEngine(ILogger<LittleCmsEngine> logger)
    {
        _logger = logger;
        _context = LcmsNative.cmsCreateContext(IntPtr.Zero, IntPtr.Zero);
        
        // Set error handler
        // Note: In production, implement proper error handling callback
    }
    
    public async Task<IccProfile> LoadProfileAsync(string profilePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        if (!File.Exists(profilePath))
            throw new FileNotFoundException($"ICC profile not found: {profilePath}");
        
        var handle = LcmsNative.cmsOpenProfileFromFile(profilePath, "r");
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to load ICC profile: {profilePath}");
        
        var profile = new IccProfile
        {
            Path = profilePath,
            Name = Path.GetFileNameWithoutExtension(profilePath),
            Handle = handle,
            ColorSpace = GetColorSpace(handle),
            ProfileClass = GetProfileClass(handle),
            Version = LcmsNative.cmsGetEncodedICCversion(handle),
            RawData = await File.ReadAllBytesAsync(profilePath, cancellationToken)
        };
        
        // Get description
        profile.Description = GetProfileDescription(handle);
        
        _logger.LogInformation("Loaded ICC profile: {Name} ({ColorSpace}, Class: {Class})", 
            profile.Name, profile.ColorSpace, profile.ProfileClass);
        
        return profile;
    }
    
    public async Task<ColorTransform> CreateTransformAsync(
        IccProfile sourceProfile,
        IccProfile destProfile,
        RenderingIntent intent,
        bool blackPointCompensation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        var inputFormat = GetFormatter(sourceProfile.ColorSpace, false);
        var outputFormat = GetFormatter(destProfile.ColorSpace, false);
        
        var flags = 0;
        if (blackPointCompensation)
            flags |= LcmsNative.cmsFLAGS_BLACKPOINTCOMPENSATION;
        
        var transformHandle = LcmsNative.cmsCreateTransform(
            sourceProfile.Handle, inputFormat,
            destProfile.Handle, outputFormat,
            (int)intent, flags);
        
        if (transformHandle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create color transform");
        
        return new ColorTransform
        {
            Handle = transformHandle,
            SourceProfile = sourceProfile,
            DestProfile = destProfile,
            Intent = intent,
            BlackPointCompensation = blackPointCompensation
        };
    }
    
    public async Task<ProofTransform> CreateProofTransformAsync(
        IccProfile displayProfile,
        IccProfile simulationProfile,
        ProofSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        // For proofing, we need:
        // 1. Input (document) -> Simulation (print) transform
        // 2. Simulation -> Display transform
        // The proofing transform combines both
        
        var inputFormat = GetFormatter(ColorSpace.CMYK, false); // Assume CMYK input
        var proofingFormat = GetFormatter(simulationProfile.ColorSpace, false);
        var displayFormat = GetFormatter(displayProfile.ColorSpace, false);
        
        var flags = LcmsNative.cmsFLAGS_SOFTPROOFING;
        if (settings.BlackPointCompensation)
            flags |= LcmsNative.cmsFLAGS_BLACKPOINTCOMPENSATION;
        
        // The proofing transform needs an actual input profile. We use the
        // simulation profile as the input (document) profile — this is the
        // standard soft-proofing setup where the document is already in the
        // print space and we simulate how it will look on the display.
        // Previously this passed IntPtr.Zero, which made the transform
        // always fail or produce undefined output.
        var proofHandle = LcmsNative.cmsCreateProofingTransform(
            simulationProfile.Handle, inputFormat,
            displayProfile.Handle, displayFormat,
            simulationProfile.Handle, proofingFormat,
            (int)settings.RenderingIntent,
            (int)RenderingIntent.RelativeColorimetric, // Proofing intent
            flags);
        
        if (proofHandle == IntPtr.Zero)
        {
            // Fallback: create separate transforms
            return await CreateProofTransformFallbackAsync(displayProfile, simulationProfile, settings, cancellationToken);
        }
        
        var transform = new ProofTransform
        {
            Settings = settings,
        };
        // Store the proofing transform handle in SimulationTransform so
        // callers that use it get the combined proofing pipeline, and
        // Dispose() will free it correctly.
        // We wrap it in a ColorTransform so the existing disposal logic applies.
        transform.SimulationTransform = new ColorTransform
        {
            Handle = proofHandle,
            SourceProfile = simulationProfile,
            DestProfile = displayProfile,
            Intent = settings.RenderingIntent,
            BlackPointCompensation = settings.BlackPointCompensation
        };
        return transform;
    }
    
    private async Task<ProofTransform> CreateProofTransformFallbackAsync(
        IccProfile displayProfile,
        IccProfile simulationProfile,
        ProofSettings settings,
        CancellationToken cancellationToken)
    {
        // Create simulation transform (document -> print simulation)
        // Use the simulation profile as the source rather than a null handle.
        var simTransform = await CreateTransformAsync(
            simulationProfile,
            simulationProfile,
            settings.RenderingIntent,
            settings.BlackPointCompensation,
            cancellationToken);
        
        // Create display transform (simulation -> display)
        var dispTransform = await CreateTransformAsync(
            simulationProfile,
            displayProfile,
            RenderingIntent.RelativeColorimetric,
            true,
            cancellationToken);
        
        return new ProofTransform
        {
            SimulationTransform = simTransform,
            DisplayTransform = dispTransform,
            Settings = settings
        };
    }
    
    public async Task<BitmapData> ApplyTransformAsync(
        ColorTransform transform,
        BitmapData source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        var dest = new BitmapData
        {
            Width = source.Width,
            Height = source.Height,
            Format = GetOutputFormat(transform.DestProfile.ColorSpace),
            Stride = CalculateStride(source.Width, GetOutputFormat(transform.DestProfile.ColorSpace))
        };
        
        dest.Pixels = new byte[dest.Stride * dest.Height];
        
        // Pin arrays for native access
        var sourceHandle = GCHandle.Alloc(source.Pixels, GCHandleType.Pinned);
        var destHandle = GCHandle.Alloc(dest.Pixels, GCHandleType.Pinned);
        
        try
        {
            var pixelCount = (UIntPtr)(source.Width * source.Height);
            var result = LcmsNative.cmsDoTransform(
                transform.Handle,
                sourceHandle.AddrOfPinnedObject(),
                destHandle.AddrOfPinnedObject(),
                pixelCount);
            
            if (result == 0)
                throw new InvalidOperationException("Color transform failed");
        }
        finally
        {
            if (sourceHandle.IsAllocated) sourceHandle.Free();
            if (destHandle.IsAllocated) destHandle.Free();
        }
        
        return dest;
    }
    
    public DeltaEResult CalculateDeltaE(float[] lab1, float[] lab2, DeltaEFormula formula = DeltaEFormula.CIE2000)
    {
        if (lab1.Length < 3 || lab2.Length < 3)
            throw new ArgumentException("Lab arrays must have at least 3 elements");
        
        // Use LittleCMS for CIE2000
        if (formula == DeltaEFormula.CIE2000)
        {
            var lab1Ptr = Marshal.AllocHGlobal(3 * sizeof(double));
            var lab2Ptr = Marshal.AllocHGlobal(3 * sizeof(double));
            
            try
            {
                Marshal.Copy(Array.ConvertAll(lab1, x => (double)x), 0, lab1Ptr, 3);
                Marshal.Copy(Array.ConvertAll(lab2, x => (double)x), 0, lab2Ptr, 3);
                
                var deltaE = LcmsNative.cmsDeltaE2000(lab1Ptr, lab2Ptr, 1.0, 1.0, 1.0);
                
                return new DeltaEResult
                {
                    DeltaE = (float)deltaE,
                    Formula = formula
                };
            }
            finally
            {
                Marshal.FreeHGlobal(lab1Ptr);
                Marshal.FreeHGlobal(lab2Ptr);
            }
        }
        
        // Fallback: simple CIE76
        var dL = lab1[0] - lab2[0];
        var da = lab1[1] - lab2[1];
        var db = lab1[2] - lab2[2];
        var deltaE76 = MathF.Sqrt(dL * dL + da * da + db * db);
        
        return new DeltaEResult
        {
            DeltaE = deltaE76,
            DeltaL = dL,
            DeltaC = MathF.Sqrt(lab1[1]*lab1[1] + lab1[2]*lab1[2]) - MathF.Sqrt(lab2[1]*lab2[1] + lab2[2]*lab2[2]),
            DeltaH = 0,
            Formula = DeltaEFormula.CIE76
        };
    }
    
    private ColorSpace GetColorSpace(IntPtr handle)
    {
        var cs = LcmsNative.cmsGetColorSpace(handle);
        return cs switch
        {
            LcmsNative.cmsSigGrayData => ColorSpace.Gray,
            LcmsNative.cmsSigRGBData => ColorSpace.RGB,
            LcmsNative.cmsSigCMYKData => ColorSpace.CMYK,
            LcmsNative.cmsSigLabData => ColorSpace.Lab,
            _ => ColorSpace.MultiChannel
        };
    }
    
    private ProfileClass GetProfileClass(IntPtr handle)
    {
        var pc = LcmsNative.cmsGetDeviceClass(handle);
        return pc switch
        {
            LcmsNative.cmsSigInputClass => ProfileClass.InputDevice,
            LcmsNative.cmsSigDisplayClass => ProfileClass.DisplayDevice,
            LcmsNative.cmsSigOutputClass => ProfileClass.OutputDevice,
            LcmsNative.cmsSigLinkClass => ProfileClass.Link,
            LcmsNative.cmsSigAbstractClass => ProfileClass.Abstract,
            LcmsNative.cmsSigColorSpaceClass => ProfileClass.ColorSpace,
            LcmsNative.cmsSigNamedColorClass => ProfileClass.NamedColor,
            _ => ProfileClass.ColorSpace
        };
    }
    
    private string GetProfileDescription(IntPtr handle)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            var len = LcmsNative.cmsGetProfileInfoASCII(handle, 0, "en", "US", buffer, 256);
            return len > 0 ? Marshal.PtrToStringAnsi(buffer, len) ?? string.Empty : string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
    
    private int GetFormatter(ColorSpace colorSpace, bool isFloat)
    {
        // Use the LittleCMS formatter builder macros correctly.
        // The _SH constants are shift amounts, not direct bit values.
        var channels = colorSpace switch
        {
            ColorSpace.Gray => 1,
            ColorSpace.RGB or ColorSpace.Lab => 3,
            ColorSpace.CMYK => 4,
            _ => 4
        };
        
        var bytes = isFloat ? 4 : 2; // 32-bit float or 16-bit integer
        
        // Build format descriptor: channels in bits 3-10, bytes in bits 0-2
        // per lcms2 TYPE_ definitions (CHANNELS_SH shifts by 3, BYTES_SH by 0)
        var formatter = LcmsNative.CHANNELS_SH(channels) | LcmsNative.BYTES_SH(bytes);
        
        if (isFloat)
            formatter |= LcmsNative.FLOAT_BIT;
        
        return formatter;
    }
    
    private PixelFormat GetOutputFormat(ColorSpace colorSpace)
    {
        return colorSpace switch
        {
            ColorSpace.Gray => PixelFormat.Gray16,
            ColorSpace.RGB => PixelFormat.RGB48,
            ColorSpace.CMYK => PixelFormat.CMYK64,
            ColorSpace.Lab => PixelFormat.Lab48,
            _ => PixelFormat.RGB48
        };
    }
    
    private int CalculateStride(int width, PixelFormat format)
    {
        var bytesPerPixel = format switch
        {
            PixelFormat.Gray8 => 1,
            PixelFormat.Gray16 => 2,
            PixelFormat.RGB24 => 3,
            PixelFormat.RGB48 => 6,
            PixelFormat.CMYK32 => 4,
            PixelFormat.CMYK64 => 8,
            PixelFormat.Lab32 => 4,
            PixelFormat.Lab48 => 6,
            PixelFormat.BGRA32 => 4,
            _ => 4
        };
        
        // Align to 4-byte boundary
        var stride = width * bytesPerPixel;
        return (stride + 3) & ~3;
    }
    
    public void Dispose()
    {
        if (!_disposed)
        {
            if (_context != IntPtr.Zero)
                LcmsNative.cmsDeleteContext(_context);
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
    
    ~LittleCmsEngine() => Dispose();
}