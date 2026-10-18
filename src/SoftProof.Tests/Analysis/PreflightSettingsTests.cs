using SoftProof.Core.Analysis;
using SoftProof.Core.Models;
using Xunit;

namespace SoftProof.Tests.Analysis;

/// <summary>
/// Thresholds live on the press profile, so the same plates can pass on one press and warn on
/// another. These tests pin that the profile is actually consulted, not just stored.
/// </summary>
public class PreflightSettingsTests
{
    private static PlateLayer Plate(string name, SeparationColor color, int width, int height,
                                    Func<int, int, byte> ink)
    {
        var coverage = new byte[(long)width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                coverage[y * width + x] = ink(x, y);

        var separation = new Separation { Name = name, Color = color };
        var plate = new PlateLayer(separation, width, height, coverage, 200, 40, 90);
        plate.ComputeStats();
        return plate;
    }

    private static PrintJob Job(int dpi, PreflightSettings? settings, params Separation[] separations) => new()
    {
        Metadata = new JobMetadata { Resolution = dpi },
        Separations = separations.ToList(),
        ProofSettings = new ProofSettings { Preflight = settings ?? new PreflightSettings() }
    };

    private static PreflightResult Result(PrintJob job, IReadOnlyList<PlateLayer> plates, string title) =>
        PreflightAnalyzer.Run(job, plates).First(r => r.Title == title);

    [Fact]
    public void LowerNearSolidThreshold_TurnsAPassIntoAWarning()
    {
        // 200x200 with a 10x10 hole: 99.75% coverage.
        var plate = Plate("Black", SeparationColor.ProcessBlack, 200, 200,
                          (x, y) => x < 10 && y < 10 ? (byte)0 : (byte)255);
        var jobDefault = Job(300, null, plate.Separation);
        var jobStrict = Job(300, new PreflightSettings { NearSolidPercent = 99.0 }, plate.Separation);

        Assert.Equal(PreflightStatus.Pass, Result(jobDefault, new[] { plate }, "Plate Polarity").Status);
        Assert.Equal(PreflightStatus.Warning, Result(jobStrict, new[] { plate }, "Plate Polarity").Status);
    }

    [Fact]
    public void WiderSlugBand_FlagsContentNearTheEdge()
    {
        // A 20-row band 80 px from the bottom edge: inside a 3 mm trim, outside a 10 mm one.
        var plate = Plate("Cyan", SeparationColor.ProcessCyan, 1000, 1000,
                          (_, y) => y is >= 900 and < 920 ? (byte)255 : (byte)0);

        var jobDefault = Job(300, null, plate.Separation);
        var jobWide = Job(300, new PreflightSettings { SlugBandMm = 10 }, plate.Separation);

        Assert.Equal(PreflightStatus.Pass, Result(jobDefault, new[] { plate }, "Content vs Marks").Status);
        Assert.Equal(PreflightStatus.Warning, Result(jobWide, new[] { plate }, "Content vs Marks").Status);
    }

    [Fact]
    public void LowerMissingSeparationThreshold_AcceptsTheSparsePlate()
    {
        const int size = 100;
        var c = Plate("Cyan", SeparationColor.ProcessCyan, size, size, (_, y) => y < 10 ? (byte)255 : (byte)0);
        var m = Plate("Magenta", SeparationColor.ProcessMagenta, size, size, (_, y) => y < 10 ? (byte)255 : (byte)0);
        var yel = Plate("Yellow", SeparationColor.ProcessYellow, size, size, (_, y) => y < 10 ? (byte)255 : (byte)0);
        var k = Plate("Black", SeparationColor.ProcessBlack, size, size,
                      (x, y) => x is > 48 and < 51 && y is > 48 and < 51 ? (byte)255 : (byte)0);

        var plates = new[] { c, m, yel, k };
        var separations = plates.Select(p => p.Separation).ToArray();

        Assert.Equal(PreflightStatus.Warning,
            Result(Job(300, null, separations), plates, "Missing Separation").Status);

        // 0.04 % is above a 0.01 % floor, so this press does not call it missing.
        Assert.Equal(PreflightStatus.Pass,
            Result(Job(300, new PreflightSettings { MissingSeparationPercent = 0.01 }, separations),
                   plates, "Missing Separation").Status);
    }

    [Fact]
    public void Normalized_ClampsBadProfileValues()
    {
        var settings = new PreflightSettings
        {
            SlugBandMm = -5,
            NearSolidPercent = 120,
            TinyInkedPixelPercent = -1,
            MeaningfulCoveragePercent = 1,
            MissingSeparationPercent = 5
        }.Normalized();

        Assert.Equal(0, settings.SlugBandMm);
        Assert.Equal(100, settings.NearSolidPercent);
        Assert.Equal(0, settings.TinyInkedPixelPercent);
        Assert.Equal(1, settings.MissingSeparationPercent); // cannot exceed "meaningful"
        Assert.True(settings.MissingSeparationPercent <= settings.MeaningfulCoveragePercent);
    }

    [Fact]
    public void PressName_IsShownInTheWarning()
    {
        var plate = Plate("Black", SeparationColor.ProcessBlack, 100, 100, (_, _) => 255);
        var job = Job(300, new PreflightSettings { PressName = "Web press A" }, plate.Separation);

        var result = Result(job, new[] { plate }, "Plate Polarity");

        Assert.Contains("[press: Web press A]", result.Detail);
    }
}
