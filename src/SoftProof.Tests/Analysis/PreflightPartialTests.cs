using SoftProof.Core.Analysis;
using SoftProof.Core.Models;
using Xunit;

namespace SoftProof.Tests.Analysis;

/// <summary>
/// A result computed from half the plates and a decimated preview must not present itself as a
/// finished pass: it names the partial set, marks itself estimated and reports amber.
/// </summary>
public class PreflightPartialTests
{
    private static PlateLayer Plate(string name, SeparationColor color, int width, int height,
                                    int factor, byte ink)
    {
        var coverage = new byte[(long)width * height];
        Array.Fill(coverage, ink);

        var pyramid = PlatePyramid.FromCoverage(coverage, width, height, factor);
        var separation = new Separation { Name = name, Color = color };
        var plate = new PlateLayer(separation, width, height, coverage, 200, 40, 90, pyramid);
        plate.ComputeStats();
        return plate;
    }

    [Fact]
    public void PartialPlateSet_MarksTacAndHighlightAsPartialAndEstimated()
    {
        var cyan = Plate("Cyan", SeparationColor.ProcessCyan, 100, 100, 16, 255);
        var magenta = Plate("Magenta", SeparationColor.ProcessMagenta, 100, 100, 16, 255);
        var yellow = new Separation { Name = "Yellow", Color = SeparationColor.ProcessYellow };
        var black = new Separation { Name = "Black", Color = SeparationColor.ProcessBlack };

        var job = new PrintJob
        {
            Metadata = new JobMetadata { Resolution = 2400, Width = 1600, Height = 1600 },
            Separations = new List<Separation> { cyan.Separation, magenta.Separation, yellow, black },
            ProofSettings = new ProofSettings { InkLimit = 300 }
        };

        var results = PreflightAnalyzer.Run(job, new[] { cyan, magenta });

        var tac = results.First(r => r.Title == "Total Ink Coverage");
        Assert.Equal(PreflightStatus.Warning, tac.Status);
        Assert.Contains("Partial: 2 of 4 separations", tac.Detail);
        Assert.Contains("estimated", tac.Detail);

        var highlight = results.First(r => r.Title == "Highlight Dot Check");
        Assert.Equal(PreflightStatus.Warning, highlight.Status);
        Assert.Contains("Partial: 2 of 4 separations", highlight.Detail);
        Assert.Contains("estimated", highlight.Detail);

        var missing = results.First(r => r.Title == "Missing Separation");
        Assert.Contains("Missing: Yellow, Black", missing.Detail);
        Assert.Contains("Cyan", missing.Detail);
        Assert.Contains("Magenta", missing.Detail);
    }
}
