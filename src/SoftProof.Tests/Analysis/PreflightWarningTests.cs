using SoftProof.Core.Analysis;
using SoftProof.Core.Models;
using Xunit;

namespace SoftProof.Tests.Analysis;

/// <summary>
/// The three "suspicious plate" advisories: marks-only content, inverted polarity and a missing
/// separation among the process plates.
/// </summary>
public class PreflightWarningTests
{
    private static Separation Sep(string name, SeparationColor color) =>
        new() { Name = name, Color = color, IsTechnical = false };

    private static PrintJob Job(int dpi, params Separation[] separations) => new()
    {
        Name = "preflight-test",
        Metadata = new JobMetadata { Resolution = dpi },
        Separations = separations.ToList()
    };

    private static PlateLayer Plate(string name, SeparationColor color, int width, int height,
                                    Func<int, int, byte> ink)
    {
        var coverage = new byte[(long)width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                coverage[y * width + x] = ink(x, y);

        var plate = new PlateLayer(Sep(name, color), width, height, coverage, 200, 40, 90);
        plate.ComputeStats();
        return plate;
    }

    private static PreflightResult Result(PrintJob job, IReadOnlyList<PlateLayer> plates, string title) =>
        PreflightAnalyzer.Run(job, plates).First(r => r.Title == title);

    // ---- marks / slug -------------------------------------------------------------------------

    [Fact]
    public void MarksOnly_CornerMarksAndColourBar_AreFlagged()
    {
        const int size = 1000;
        var plate = Plate("Cyan", SeparationColor.ProcessCyan, size, size, (x, y) =>
        {
            var corner = (x < 20 && y < 20) || (x > size - 20 && y < 20) ||
                         (x < 20 && y > size - 20) || (x > size - 20 && y > size - 20);
            var bar = y > size - 10 && x > 100 && x < size - 100;
            return corner || bar ? (byte)255 : (byte)0;
        });

        var result = Result(Job(300, plate.Separation), new[] { plate }, "Content vs Marks");

        Assert.Equal(PreflightStatus.Warning, result.Status);
        Assert.Contains("marks", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cyan", result.Detail);
    }

    [Fact]
    public void RealContentInsideTrim_Passes()
    {
        var plate = Plate("Cyan", SeparationColor.ProcessCyan, 1000, 1000,
                          (x, y) => x is > 300 and < 700 && y is > 300 and < 700 ? (byte)255 : (byte)0);

        var result = Result(Job(300, plate.Separation), new[] { plate }, "Content vs Marks");

        Assert.Equal(PreflightStatus.Pass, result.Status);
    }

    [Fact]
    public void TinyDotInTheMiddle_IsReportedAsSuspiciouslyLittle()
    {
        var plate = Plate("Magenta", SeparationColor.ProcessMagenta, 1000, 1000,
                          (x, y) => x is > 495 and < 505 && y is > 495 and < 505 ? (byte)255 : (byte)0);

        var result = Result(Job(300, plate.Separation), new[] { plate }, "Content vs Marks");

        Assert.Equal(PreflightStatus.Warning, result.Status);
        Assert.Contains("Suspiciously little", result.Detail);
        Assert.Contains("Magenta", result.Detail);
    }

    // ---- polarity ------------------------------------------------------------------------------

    [Fact]
    public void NearSolidPlate_IsAdvisedAsPossiblyInverted()
    {
        var plate = Plate("Black", SeparationColor.ProcessBlack, 200, 200, (_, _) => 255);

        var result = Result(Job(300, plate.Separation), new[] { plate }, "Plate Polarity");

        Assert.Equal(PreflightStatus.Warning, result.Status);
        Assert.Contains("polarity", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("100.00%", result.Detail);
    }

    [Fact]
    public void NormalPlate_PassesPolarityCheck()
    {
        var plate = Plate("Black", SeparationColor.ProcessBlack, 200, 200,
                          (x, _) => x < 100 ? (byte)255 : (byte)0);

        var result = Result(Job(300, plate.Separation), new[] { plate }, "Plate Polarity");

        Assert.Equal(PreflightStatus.Pass, result.Status);
    }

    // ---- sibling comparison --------------------------------------------------------------------

    private static (PrintJob Job, List<PlateLayer> Plates) FourProcessPlates(double blackPercent)
    {
        const int size = 100;

        var c = Plate("Cyan", SeparationColor.ProcessCyan, size, size, (_, y) => y < 10 ? (byte)255 : (byte)0);
        var m = Plate("Magenta", SeparationColor.ProcessMagenta, size, size, (_, y) => y < 10 ? (byte)255 : (byte)0);
        var yel = Plate("Yellow", SeparationColor.ProcessYellow, size, size, (_, y) => y < 10 ? (byte)255 : (byte)0);

        // A 2x2 dot is 0.04 % coverage: present, but nowhere near a real black plate.
        var k = Plate("Black", SeparationColor.ProcessBlack, size, size,
                      (x, y) => x is > 48 and < 51 && y is > 48 and < 51 ? (byte)255 : (byte)0);

        var job = Job(300, c.Separation, m.Separation, yel.Separation, k.Separation);
        return (job, new List<PlateLayer> { c, m, yel, k });
    }

    [Fact]
    public void SparseFourthProcessPlate_IsReportedAsMissingSeparation()
    {
        var (job, plates) = FourProcessPlates(0);

        var result = Result(job, plates, "Missing Separation");

        Assert.Equal(PreflightStatus.Warning, result.Status);
        Assert.Contains("missing separation", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Black", result.Detail);
    }

    [Fact]
    public void FourMeaningfulPlates_Pass()
    {
        const int size = 100;
        var plates = new List<PlateLayer>
        {
            Plate("Cyan", SeparationColor.ProcessCyan, size, size, (_, y) => y < 10 ? (byte)255 : (byte)0),
            Plate("Magenta", SeparationColor.ProcessMagenta, size, size, (_, y) => y < 10 ? (byte)255 : (byte)0),
            Plate("Yellow", SeparationColor.ProcessYellow, size, size, (_, y) => y < 10 ? (byte)255 : (byte)0),
            Plate("Black", SeparationColor.ProcessBlack, size, size, (_, y) => y is > 40 and < 50 ? (byte)255 : (byte)0)
        };

        var job = Job(300, plates.Select(p => p.Separation).ToArray());
        var result = Result(job, plates, "Missing Separation");

        Assert.Equal(PreflightStatus.Pass, result.Status);
    }
}
