using System.IO;
using System.Text.Json;
using SoftProof.Core.Models;

namespace SoftProof.UI.Services;

/// <summary>A named press profile: the label plus the preflight/colour settings that go with it.</summary>
public sealed class PressProfile
{
    public string Name { get; set; } = string.Empty;
    public ProofSettings Settings { get; set; } = new();
}

/// <summary>On-disk list of press profiles and the last selected one.</summary>
public sealed class PressProfileFile
{
    public string Selected { get; set; } = string.Empty;
    public List<PressProfile> Profiles { get; set; } = new();
}

public static class PressProfileStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoftProof", "press-profiles.json");

    public static PressProfileFile Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new PressProfileFile();

            return JsonSerializer.Deserialize<PressProfileFile>(File.ReadAllText(FilePath)) ?? new PressProfileFile();
        }
        catch
        {
            return new PressProfileFile();
        }
    }

    public static void Save(PressProfileFile file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // A profile that cannot be persisted must not break the session.
        }
    }

    public static ProofSettings Clone(ProofSettings source) => new()
    {
        DisplayProfilePath = source.DisplayProfilePath,
        SimulationProfilePath = source.SimulationProfilePath,
        RenderingIntent = source.RenderingIntent,
        BlackPointCompensation = source.BlackPointCompensation,
        SimulatePaperColor = source.SimulatePaperColor,
        SimulateBlackInk = source.SimulateBlackInk,
        InkLimit = source.InkLimit,
        DotGain = source.DotGain,
        ScreenFrequency = source.ScreenFrequency,
        ScreeningType = source.ScreeningType,
        DeviceLinkProfilePath = source.DeviceLinkProfilePath,
        Preflight = ClonePreflight(source.Preflight ?? new PreflightSettings())
    };

    public static void Copy(ProofSettings source, ProofSettings destination)
    {
        destination.DisplayProfilePath = source.DisplayProfilePath;
        destination.SimulationProfilePath = source.SimulationProfilePath;
        destination.RenderingIntent = source.RenderingIntent;
        destination.BlackPointCompensation = source.BlackPointCompensation;
        destination.SimulatePaperColor = source.SimulatePaperColor;
        destination.SimulateBlackInk = source.SimulateBlackInk;
        destination.InkLimit = source.InkLimit;
        destination.DotGain = source.DotGain;
        destination.ScreenFrequency = source.ScreenFrequency;
        destination.ScreeningType = source.ScreeningType;
        destination.DeviceLinkProfilePath = source.DeviceLinkProfilePath;
        destination.Preflight = ClonePreflight(source.Preflight ?? new PreflightSettings());
    }

    private static PreflightSettings ClonePreflight(PreflightSettings source) => new()
    {
        PressName = source.PressName,
        SlugBandMm = source.SlugBandMm,
        TinyInkedPixelPercent = source.TinyInkedPixelPercent,
        TinyCoveragePercent = source.TinyCoveragePercent,
        NearSolidPercent = source.NearSolidPercent,
        MeaningfulCoveragePercent = source.MeaningfulCoveragePercent,
        MissingSeparationPercent = source.MissingSeparationPercent,
        TrapGapMm = source.TrapGapMm,
        TrapGapMinPixels = source.TrapGapMinPixels
    };
}
