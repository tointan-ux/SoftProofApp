using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SoftProof.Core.Interfaces;
using SoftProof.Core.Models;
using SoftProof.Core.ColorPipeline;

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
        IntPtr inputProfile, uint inputFormat,
        IntPtr outputProfile, uint outputFormat,
        uint intent, uint flags);
    
    // lcms2 has NO proofingFormat parameter here: 8 arguments, not 9. Passing an extra one put
    // the proofing profile into an int slot and the intent into the flags slot.
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr cmsCreateProofingTransform(
        IntPtr inputProfile, uint inputFormat,
        IntPtr outputProfile, uint outputFormat,
        IntPtr proofingProfile,
        uint intent, uint proofingIntent, uint flags);
    
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern void cmsDeleteTransform(IntPtr hTransform);
    
    // Transform execution. cmsDoTransform returns void, so there is no success code to test:
    // failures are reported through the lcms log handler, and by the transform being NULL.
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern void cmsDoTransform(IntPtr hTransform, IntPtr input, IntPtr output, uint size);
    
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern int cmsDoTransformLineStride(IntPtr hTransform, IntPtr input, IntPtr output, UIntPtr pixelsPerLine, UIntPtr nLines, int inputStride, int outputStride);
    
    // Pixel-format colour spaces (PT_*). These are NOT the ICC signatures below: a format word
    // takes these small enums, while cmsGetColorSpace returns the four-char signatures.
    public const int PT_GRAY = 3;
    public const int PT_RGB = 4;
    public const int PT_CMYK = 6;
    public const int PT_Lab = 10;
    
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
    
    // Transform flags (lcms2.h). The exact bits matter: 0x00040000 is GRIDPOINTS(4), so the
    // old "SOFTPROOFING" value resized the CLUT instead of enabling the proof.
    public const int cmsFLAGS_BLACKPOINTCOMPENSATION = 0x00002000;
    public const int cmsFLAGS_NOWHITEONWHITEFIXUP = 0x00000004;
    public const int cmsFLAGS_HIGHRESPRECALC = 0x00000400;
    public const int cmsFLAGS_LOWRESPRECALC = 0x00000800;
    public const int cmsFLAGS_SOFTPROOFING = 0x00004000;
    
    // Formatter field helpers (lcms2.h): byte width in bits 0-2, channel count from bit 3,
    // colour space in bits 16-19. Verified against lcms2 with real profiles:
    // CMYK 16-bit = 0x00060022, RGB 16-bit = 0x0004001A.
    public static int BYTES_SH(int x) => x;
    public static int CHANNELS_SH(int x) => x << 3;
    public static int COLORSPACE_SH(int x) => x << 16;

    // Formatter flags
    public const int SWAPFIRST_SH = (1 << 14);
    public const int FLOAT_SH = (1 << 22);
    
    // NOTE: lcms2's public API has cmsSetLogErrorHandler, not cmsGetErrorHandler /
    // cmsSetErrorHandler; the old declarations did not resolve to anything real. Removed so
    // nothing can call a function that does not exist.

    // lcms2: cmsCreateContext(void* Plugin, void* UserData) — plugin first.
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr cmsCreateContext(IntPtr plugin, IntPtr userData);
    
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
/// LittleCMS-based color engine implementation with TVI/ink limit support
/// </summary>
public class LittleCmsEngine : IColorEngine, IDisposable
{
    private readonly ILogger<LittleCmsEngine> _logger;
    private readonly IntPtr _context;
    private bool _disposed;

    // Native lcms2 objects own memory the GC cannot see. lcms has explicit destructors
    // (cmsCloseProfile / cmsDeleteTransform) that were previously never called, so every load
    // and every transform leaked until process exit. The engine owns them and frees them here.
    private readonly List<IntPtr> _profiles = new();
    private readonly List<IntPtr> _transforms = new();
    private readonly object _handleGate = new();

    private void TrackProfile(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        lock (_handleGate) _profiles.Add(handle);
    }

    private void TrackTransform(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        lock (_handleGate) _transforms.Add(handle);
    }
    
    public LittleCmsEngine(ILogger<LittleCmsEngine> logger)
    {
        _logger = logger;
        _context = LcmsNative.cmsCreateContext(IntPtr.Zero, IntPtr.Zero);
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
        
        profile.Description = GetProfileDescription(handle);
        TrackProfile(handle);
        
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
            sourceProfile.Handle, (uint)inputFormat,
            destProfile.Handle, (uint)outputFormat,
            (uint)intent, (uint)flags);
        
        if (transformHandle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Failed to create colour transform ({sourceProfile.ColorSpace} -> {destProfile.ColorSpace}). " +
                "Check that both ICC profiles are valid.");
        TrackTransform(transformHandle);
        
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
        
        // Create TVI pipeline stage if TVI curves are provided
        var tviStage = new TVIPipelineStage(settings.TVICurves ?? new Dictionary<SeparationColor, TVICurve>());
        
        // Create ink limit stage
        var inkLimitStage = new InkLimitStage(settings.InkLimit);

        // One proofing transform does the whole job: plate CMYK -> display RGB, with the print
        // profile as the proofing profile. There is no separate "simulation transform" to build
        // from a placeholder profile (that path threw every time and leaked its handle).
        var inputFormat = GetFormatter(simulationProfile.ColorSpace);
        var outputFormat = GetFormatter(displayProfile.ColorSpace);

        var flags = LcmsNative.cmsFLAGS_SOFTPROOFING;
        if (settings.BlackPointCompensation)
            flags |= LcmsNative.cmsFLAGS_BLACKPOINTCOMPENSATION;

        var proofHandle = LcmsNative.cmsCreateProofingTransform(
            simulationProfile.Handle, (uint)inputFormat,   // plates are already print space
            displayProfile.Handle, (uint)outputFormat,
            simulationProfile.Handle,                     // proof through the press profile
            (uint)settings.RenderingIntent,
            (uint)RenderingIntent.RelativeColorimetric,
            (uint)flags);

        if (proofHandle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Failed to create the soft-proof transform " +
                $"({simulationProfile.ColorSpace} -> {displayProfile.ColorSpace}). " +
                "Check the simulation and display ICC profiles.");
        TrackTransform(proofHandle);

        var proofTransform = new ProofTransform
        {
            // The proofing transform already writes display-space pixels, so it is both the
            // simulation and the display stage; carrying the profiles keeps ApplyTransformAsync
            // from dereferencing null ones.
            SimulationTransform = new ColorTransform
            {
                Handle = proofHandle,
                SourceProfile = simulationProfile,
                DestProfile = displayProfile,
                Intent = settings.RenderingIntent,
                BlackPointCompensation = settings.BlackPointCompensation
            },
            DisplayTransform = null,
            Settings = settings,
            TVIStage = tviStage,
            InkLimitStage = inkLimitStage
        };
        
        return proofTransform;
    }
    
    public async Task<BitmapData> ApplyTransformAsync(
        ColorTransform transform,
        BitmapData source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (transform.Handle == IntPtr.Zero)
            throw new InvalidOperationException("The colour transform is not initialised.");
        if (transform.SourceProfile is null || transform.DestProfile is null)
            throw new InvalidOperationException(
                "The colour transform is missing its source or destination profile.");

        var bytesPerSourcePixel = BytesPerPixel(source.Format);
        if (bytesPerSourcePixel == 0)
            throw new NotSupportedException($"Unsupported source pixel format {source.Format}.");

        var requiredSourceBytes = (long)source.Width * source.Height * bytesPerSourcePixel;
        if (source.Pixels.LongLength < requiredSourceBytes)
            throw new ArgumentException(
                $"Source buffer is {source.Pixels.LongLength} bytes but " +
                $"{source.Width}x{source.Height} {source.Format} needs {requiredSourceBytes}.",
                nameof(source));

        var outputFormat = GetOutputFormat(transform.DestProfile.ColorSpace);
        var dest = new BitmapData
        {
            Width = source.Width,
            Height = source.Height,
            Format = outputFormat,
            Stride = PackedStride(source.Width, outputFormat)
        };

        dest.Pixels = new byte[dest.Stride * dest.Height];

        var sourceHandle = GCHandle.Alloc(source.Pixels, GCHandleType.Pinned);
        var destHandle = GCHandle.Alloc(dest.Pixels, GCHandleType.Pinned);

        try
        {
            // lcms treats the buffers as tightly packed and returns void; failures surface as a
            // NULL transform at creation time, not here.
            var pixelCount = (uint)(source.Width * source.Height);
            LcmsNative.cmsDoTransform(
                transform.Handle,
                sourceHandle.AddrOfPinnedObject(),
                destHandle.AddrOfPinnedObject(),
                pixelCount);
        }
        finally
        {
            if (sourceHandle.IsAllocated) sourceHandle.Free();
            if (destHandle.IsAllocated) destHandle.Free();
        }

        return dest;
    }
    
    /// <summary>
    /// Applies proofing transform with TVI curves and ink limit enforcement
    /// </summary>
    public async Task<BitmapData> ApplyProofTransformAsync(
        ProofTransform proofTransform,
        BitmapData source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The proof pipeline is defined on 4-channel 16-bit plate data; anything else would make
        // lcms read past the end of this buffer.
        if (source.Format != PixelFormat.CMYK64)
            throw new ArgumentException(
                $"Proofing needs {PixelFormat.CMYK64} plate data, got {source.Format}.", nameof(source));

        var requiredBytes = (long)source.Width * source.Height * 8;
        if (source.Pixels.LongLength < requiredBytes)
            throw new ArgumentException(
                $"Source buffer is {source.Pixels.LongLength} bytes but {source.Width}x{source.Height} " +
                $"{PixelFormat.CMYK64} needs {requiredBytes}.", nameof(source));

        // Step 1: Apply TVI curves to source data
        var tviOutput = new byte[source.Pixels.Length];
        proofTransform.TVIStage?.ApplyTVI(source.Pixels, tviOutput, source.Width * source.Height);
        
        // Step 2: Apply ink limit
        var inkLimitedData = new byte[tviOutput.Length];
        Buffer.BlockCopy(tviOutput, 0, inkLimitedData, 0, tviOutput.Length);
        proofTransform.InkLimitStage?.EnforceLimit(inkLimitedData, source.Width * source.Height);
        
        // Step 3: Apply simulation transform (document -> print simulation)
        var simInput = new BitmapData
        {
            Width = source.Width,
            Height = source.Height,
            Format = PixelFormat.CMYK64,
            Stride = PackedStride(source.Width, PixelFormat.CMYK64),
            Pixels = inkLimitedData
        };
        
        var simOutput = await ApplyTransformAsync(proofTransform.SimulationTransform ?? throw new InvalidOperationException("Simulation transform not available"), simInput, cancellationToken);
        
        // Step 4: Apply display transform (simulation -> display)
        if (proofTransform.DisplayTransform != null)
        {
            return await ApplyTransformAsync(proofTransform.DisplayTransform, simOutput, cancellationToken);
        }
        
        return simOutput;
    }
    
    public DeltaEResult CalculateDeltaE(float[] lab1, float[] lab2, DeltaEFormula formula = DeltaEFormula.CIE2000)
    {
        if (lab1.Length < 3 || lab2.Length < 3)
            throw new ArgumentException("Lab arrays must have at least 3 elements");
        
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
            return len > 0
                ? (Marshal.PtrToStringAnsi(buffer, len) ?? string.Empty).TrimEnd('\0')
                : string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
    
    /// <summary>
    /// Builds the lcms2 pixel-format word for a colour space at 16-bit integer (or 32-bit
    /// float) depth. Exposed so the encoding can be asserted in tests: getting it wrong makes
    /// lcms read and write a fraction of each pixel without any error.
    /// </summary>
    public static int GetFormatter(ColorSpace colorSpace, bool isFloat = false)
    {
        var (space, channels) = colorSpace switch
        {
            ColorSpace.Gray => (LcmsNative.PT_GRAY, 1),
            ColorSpace.RGB => (LcmsNative.PT_RGB, 3),
            ColorSpace.Lab => (LcmsNative.PT_Lab, 3),
            // CMYK, MultiChannel and DeviceN are all 4-channel print data here.
            _ => (LcmsNative.PT_CMYK, 4)
        };

        var bytes = isFloat ? 4 : 2; // 16-bit integer or 32-bit float
        var formatter = LcmsNative.COLORSPACE_SH(space)
                      | LcmsNative.CHANNELS_SH(channels)
                      | LcmsNative.BYTES_SH(bytes);

        if (isFloat)
            formatter |= LcmsNative.FLOAT_SH;

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

    private static int BytesPerPixel(PixelFormat format) => format switch
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
        _ => 0
    };

    /// <summary>
    /// Packed row stride in bytes. cmsDoTransform takes no stride argument and treats both
    /// buffers as tightly packed, so a 4-byte-aligned stride would shear rows of RGB48 (6 B/px)
    /// and Gray16 (2 B/px) data.
    /// </summary>
    private static int PackedStride(int width, PixelFormat format)
    {
        var bytesPerPixel = BytesPerPixel(format);
        if (bytesPerPixel == 0)
            bytesPerPixel = 4;

        return width * bytesPerPixel;
    }
    
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Free every native object this engine created, newest first.
        lock (_handleGate)
        {
            foreach (var transform in _transforms)
                if (transform != IntPtr.Zero)
                    LcmsNative.cmsDeleteTransform(transform);
            _transforms.Clear();

            foreach (var profile in _profiles)
                if (profile != IntPtr.Zero)
                    LcmsNative.cmsCloseProfile(profile);
            _profiles.Clear();
        }

        if (_context != IntPtr.Zero)
            LcmsNative.cmsDeleteContext(_context);

        GC.SuppressFinalize(this);
    }
}