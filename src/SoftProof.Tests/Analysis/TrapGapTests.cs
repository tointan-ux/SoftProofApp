using SoftProof.Core.Analysis;
using SoftProof.Core.Models;
using Xunit;

namespace SoftProof.Tests.Analysis;

/// <summary>
/// Trap-gap detection: a paper hairline between two different inks is a trapping failure;
/// the same gap inside one plate is not. Warnings carry a focus region for click-to-zoom.
/// </summary>
public class TrapGapTests
{
    private static PlateLayer Plate(string name, SeparationColor color, int width, int height,
                                    Func<int, int, bool> ink)
    {
        var coverage = new byte[(long)width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (ink(x, y))
                    coverage[y * width + x] = 255;

        var plate = new PlateLayer(new Separation { Name = name, Color = color }, width, height,
                                   coverage, 0, 160, 220);
        plate.ComputeStats();
        return plate;
    }

    private static PreflightResult Trap(PrintJob job, params PlateLayer[] plates) =>
        PreflightAnalyzer.Run(job, plates).First(r => r.Title == "Trap Gaps");

    [Fact]
    public void HairlineGapBetweenTwoInks_IsFlaggedWithFocus()
    {
        const int width = 200, height = 50;
        // 2 px of paper at x = 99..100, i.e. 0.17 mm at 300 dpi — inside the 0.15 mm + rounding.
        var cyan = Plate("Cyan", SeparationColor.ProcessCyan, width, height, (x, _) => x <= 98);
        var magenta = Plate("Magenta", SeparationColor.ProcessMagenta, width, height, (x, _) => x >= 101);

        var job = new PrintJob
        {
            Metadata = new JobMetadata { Resolution = 300 },
            Separations = new List<Separation> { cyan.Separation, magenta.Separation }
        };

        var result = Trap(job, cyan, magenta);

        Assert.Equal(PreflightStatus.Warning, result.Status);
        Assert.Contains("hairline", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.Focus);
        Assert.True(result.Focus!.Width > 0 && result.Focus.Height > 0);
    }

    [Fact]
    public void TouchingInks_Pass()
    {
        const int width = 200, height = 50;
        var cyan = Plate("Cyan", SeparationColor.ProcessCyan, width, height, (x, _) => x <= 99);
        var magenta = Plate("Magenta", SeparationColor.ProcessMagenta, width, height, (x, _) => x >= 100);

        var job = new PrintJob
        {
            Metadata = new JobMetadata { Resolution = 300 },
            Separations = new List<Separation> { cyan.Separation, magenta.Separation }
        };

        var result = Trap(job, cyan, magenta);

        Assert.Equal(PreflightStatus.Pass, result.Status);
        Assert.Null(result.Focus);
    }

    [Fact]
    public void WidePaperGap_IsNotATrapGap()
    {
        const int width = 200, height = 50;
        // 10 px of paper (~0.85 mm): a design gap, not a trapping hairline.
        var cyan = Plate("Cyan", SeparationColor.ProcessCyan, width, height, (x, _) => x <= 90);
        var magenta = Plate("Magenta", SeparationColor.ProcessMagenta, width, height, (x, _) => x >= 101);

        var job = new PrintJob
        {
            Metadata = new JobMetadata { Resolution = 300 },
            Separations = new List<Separation> { cyan.Separation, magenta.Separation }
        };

        Assert.Equal(PreflightStatus.Pass, Trap(job, cyan, magenta).Status);
    }

    [Fact]
    public void GapInsideOnePlate_IsNotReportedAsTrap()
    {
        const int width = 200, height = 50;
        var cyan = Plate("Cyan", SeparationColor.ProcessCyan, width, height,
                         (x, _) => x <= 98 || x >= 101);
        var magenta = Plate("Magenta", SeparationColor.ProcessMagenta, width, height, (x, _) => x == 150);

        var job = new PrintJob
        {
            Metadata = new JobMetadata { Resolution = 300 },
            Separations = new List<Separation> { cyan.Separation, magenta.Separation }
        };

        // The C-C gap is one ink; the single M column is far away.
        Assert.Equal(PreflightStatus.Pass, Trap(job, cyan, magenta).Status);
    }

    [Fact]
    public void MarksOnlyWarning_CarriesFocusOnThePlate()
    {
        const int size = 400;
        var plate = Plate("Cyan", SeparationColor.ProcessCyan, size, size,
                          (x, y) => x is > 300 and < 310 && y is > 300 and < 310);

        var job = new PrintJob
        {
            Metadata = new JobMetadata { Resolution = 300 },
            Separations = new List<Separation> { plate.Separation }
        };

        var result = PreflightAnalyzer.Run(job, new[] { plate })
                                     .First(r => r.Title == "Content vs Marks");

        Assert.NotNull(result.Focus);
        Assert.Equal("Cyan", result.Focus!.PlateName);
        Assert.InRange(result.Focus.X, 280, 320);
    }
}
