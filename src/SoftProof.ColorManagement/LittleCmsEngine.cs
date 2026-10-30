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
        cmsSetLogErrorHandler(ErrorSink);
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
        IntPtr proofingProfile,
        int intent, int proofingIntent, int flags);
    
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern int cmsDeleteTransform(IntPtr hTransform);
    
    // Transform execution
    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern void cmsDoTransform(IntPtr hTransform, IntPtr input, IntPtr output, UIntPtr size);
    
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
    
    // Flags - values from lcms2.h. The previous table was shifted by four bits, so
    // cmsFLAGS_SOFTPROOFING (0x4000) was sent as 0x40000 and cmsCreateProofingTransform
    // returned a null handle every time.
    public const int cmsFLAGS_BLACKPOINTCOMPENSATION = 0x00002000;
    public const int cmsFLAGS_SOFTPROOFING = 0x00004000;
    public const int cmsFLAGS_GAMUTCHECK = 0x00001000;
    public const int cmsFLAGS_NOWHITEONWHITEFIXUP = 0x00000004;
    public const int cmsFLAGS_HIGHRESPRECALC = 0x00000400;
    public const int cmsFLAGS_LOWRESPRECALC = 0x00000800;
    public const int cmsFLAGS_NOCACHE = 0x00000040;
    public const int cmsFLAGS_NOOPTIMIZE = 0x00000100;
    
    // Formatter bits, straight from lcms2.h:
    //   CHANNELS_SH(c) = c << 3, BYTES_SH(b) = b (1, 2 or 4), COLORSPACE_SH(t) = t << 16,
    //   OPTIMIZED_SH = bit 21, FLOAT_SH = bit 22.
    // The old table had CHANNELS at bit 8 and BYTES as "2 = 16-bit", so every formatter was
    // malformed: transforms were created and then failed in cmsDoTransform.
    public static int COLORSPACE_SH(int x) => x << 16;
    public static int CHANNELS_SH(int x) => x << 3;
    public static int BYTES_SH(int x) => x;
    public const int OPTIMIZED_SH = 1 << 21;
    public const int FLOAT_SH = 1 << 22;

    // PT_* colour space codes
    public const int PT_GRAY = 3;
    public const int PT_RGB = 4;
    public const int PT_CMYK = 6;
    public const int PT_Lab = 10;
    
    // Error handling
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void LogErrorHandler(IntPtr contextId, uint errorCode, [MarshalAs(UnmanagedType.LPStr)] string text);

    [DllImport("lcms2", CallingConvention = CallingConvention.Cdecl)]
    public static extern void cmsSetLogErrorHandler(LogErrorHandler handler);

    /// <summary>Last message LittleCMS reported, surfaced in our exceptions.</summary>
    public static string? LastError { get; private set; }

    private static readonly LogErrorHandler ErrorSink = (_, code, text) =>
        LastError = $"lcms {code}: {text}".Trim();

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
/// LittleCMS-based color engine implementation with TVI/ink limit support
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
            throw new InvalidOperationException(
                $"Failed to create colour transform {sourceProfile.ColorSpace} -> {destProfile.ColorSpace}" +
                (LcmsNative.LastError is { } error ? $" ({error})" : string.Empty));
        
        return new ColorTransform
        {
            Handle = transformHandle,
            SourceProfile = sourceProfile,
            DestProfile = destProfile,
            Intent = intent,
            BlackPointCompensation = blackPointCompensation
        };
    }
    
    public Task<ProofTransform> CreateProofTransformAsync(
        IccProfile displayProfile,
        IccProfile simulationProfile,
        ProofSettings settings,
        CancellationToken cancellationToken = default) =>
        CreateProofTransformAsync(displayProfile, simulationProfile, settings, documentProfile: null, cancellationToken);

    public async Task<ProofTransform> CreateProofTransformAsync(
        IccProfile displayProfile,
        IccProfile simulationProfile,
        ProofSettings settings,
        IccProfile? documentProfile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ArgumentNullException.ThrowIfNull(displayProfile);
        ArgumentNullException.ThrowIfNull(simulationProfile);
        ArgumentNullException.ThrowIfNull(settings);

        // Plate data arrives in press space, so the press condition is the document side of the
        // transform unless the caller names a different document profile.
        var document = documentProfile ?? simulationProfile;

        EnsureProfile(document, documentProfile is null ? nameof(simulationProfile) : nameof(documentProfile));
        EnsureProfile(simulationProfile, nameof(simulationProfile));
        EnsureProfile(displayProfile, nameof(displayProfile));

        if (displayProfile.ColorSpace is not (ColorSpace.RGB or ColorSpace.Gray))
        {
            throw new InvalidOperationException(
                $"The display profile must be an RGB or Gray profile; '{displayProfile.Name}' is {displayProfile.ColorSpace}. " +
                "A soft proof ends on the monitor, so the output side cannot be a press space.");
        }

        // Created once and shared by the returned object — the old code built them twice and
        // threw the first pair away.
        var tviStage = new TVIPipelineStage(settings.TVICurves ?? new Dictionary<SeparationColor, TVICurve>());
        var inkLimitStage = new InkLimitStage(settings.InkLimit);

        // Leg 1: document -> simulation. A real CMYK video (CMYK->CMYK), used by the two-pass path.
        var simulationTransform = await CreateTransformAsync(
            document, simulationProfile, settings.RenderingIntent, settings.BlackPointCompensation, cancellationToken)
            .ConfigureAwait(false);

        // Leg 2: simulation -> display.
        var displayTransform = await CreateTransformAsync(
            simulationProfile, displayProfile, settings.RenderingIntent, settings.BlackPointCompensation, cancellationToken)
            .ConfigureAwait(false);

        // Composite: document -> display, proofed through the simulation profile.
        // Signature is (input, inFormat, output, outFormat, proofing, proofingFormat, intent,
        // proofingIntent, flags). The old call passed the simulation profile as *output* and the
        // display profile as the proofing profile, so it transformed back into press space.
        var flags = LcmsNative.cmsFLAGS_SOFTPROOFING;
        if (settings.BlackPointCompensation)
            flags |= LcmsNative.cmsFLAGS_BLACKPOINTCOMPENSATION;

        // Paper-white simulation is the proofing leg rendered with the absolute intent.
        var proofingIntent = settings.SimulatePaperColor
            ? RenderingIntent.AbsoluteColorimetric
            : settings.RenderingIntent;

        var proofingHandle = LcmsNative.cmsCreateProofingTransform(
            document.Handle, GetFormatter(document.ColorSpace, false),
            displayProfile.Handle, GetFormatter(displayProfile.ColorSpace, false),
            simulationProfile.Handle,
            (int)settings.RenderingIntent,
            (int)proofingIntent,
            flags);

        if (proofingHandle == IntPtr.Zero)
        {
            simulationTransform.Dispose();
            displayTransform.Dispose();

            throw new InvalidOperationException(
                $"LittleCMS rejected the proofing transform: {document.Name} ({document.ColorSpace}) -> " +
                $"{displayProfile.Name} ({displayProfile.ColorSpace}) proofed through " +
                $"{simulationProfile.Name} ({simulationProfile.ColorSpace}). " +
                "Check that all three profiles are valid and that the simulation profile matches the plate space." +
                (LcmsNative.LastError is { } error ? $" LittleCMS said: {error}" : string.Empty));
        }

        return new ProofTransform
        {
            SimulationTransform = simulationTransform,
            DisplayTransform = displayTransform,
            ProofingTransform = new ColorTransform
            {
                Handle = proofingHandle,
                SourceProfile = document,
                DestProfile = displayProfile,
                Intent = settings.RenderingIntent,
                BlackPointCompensation = settings.BlackPointCompensation
            },
            Settings = settings,
            TVIStage = tviStage,
            InkLimitStage = inkLimitStage
        };
    }

    private static void EnsureProfile(IccProfile profile, string parameterName)
    {
        if (profile.Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"{parameterName} is not loaded: its native ICC handle is null, so no transform can be built. " +
                "Load the profile with LoadProfileAsync first, and do not reuse a disposed profile.");
        }
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
        
        var sourceHandle = GCHandle.Alloc(source.Pixels, GCHandleType.Pinned);
        var destHandle = GCHandle.Alloc(dest.Pixels, GCHandleType.Pinned);
        
        try
        {
            var pixelCount = (UIntPtr)(uint)(source.Width * source.Height);
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
        
        // Step 1: Apply TVI curves to source data
        var tviOutput = new byte[source.Pixels.Length];
        proofTransform.TVIStage?.ApplyTVI(source.Pixels, tviOutput, source.Width * source.Height);
        
        // Step 2: Apply ink limit
        var inkLimitedData = new byte[tviOutput.Length];
        Buffer.BlockCopy(tviOutput, 0, inkLimitedData, 0, tviOutput.Length);
        proofTransform.InkLimitStage?.EnforceLimit(inkLimitedData, source.Width * source.Height);
        
        // Step 3: one pass through the proofing transform when it exists — it already carries the
        // simulation profile and the proofing intent, so chaining the two legs would proof twice.
        if (proofTransform.ProofingTransform is { } composite)
        {
            var proofInput = new BitmapData
            {
                Width = source.Width,
                Height = source.Height,
                Format = PixelFormat.CMYK32,
                Stride = CalculateStride(source.Width, PixelFormat.CMYK32),
                Pixels = inkLimitedData
            };

            return await ApplyTransformAsync(composite, proofInput, cancellationToken).ConfigureAwait(false);
        }

        // Fallback: two legs, for transforms built before the composite was available.
        var simInput = new BitmapData
        {
            Width = source.Width,
            Height = source.Height,
            Format = PixelFormat.CMYK32,
            Stride = CalculateStride(source.Width, PixelFormat.CMYK32),
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
            return len > 0 ? Marshal.PtrToStringAnsi(buffer, len) ?? string.Empty : string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
    
    private int GetFormatter(ColorSpace colorSpace, bool isFloat)
    {
        var (channels, code) = colorSpace switch
        {
            ColorSpace.Gray => (1, LcmsNative.PT_GRAY),
            ColorSpace.RGB => (3, LcmsNative.PT_RGB),
            ColorSpace.Lab => (3, LcmsNative.PT_Lab),
            _ => (4, LcmsNative.PT_CMYK)
        };

        var formatter = LcmsNative.COLORSPACE_SH(code)
                        | LcmsNative.CHANNELS_SH(channels)
                        | LcmsNative.BYTES_SH(isFloat ? 4 : 1);

        if (isFloat)
            formatter |= LcmsNative.FLOAT_SH;

        return formatter;
    }
    
    private PixelFormat GetOutputFormat(ColorSpace colorSpace)
    {
        return colorSpace switch
        {
            ColorSpace.Gray => PixelFormat.Gray8,
            ColorSpace.RGB => PixelFormat.RGB24,
            ColorSpace.CMYK => PixelFormat.CMYK32,
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