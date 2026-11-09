using SoftProof.Core.Analysis;
using SoftProof.Core.Models;
using SoftProof.Tools;
using Xunit;

namespace SoftProof.Tests.Analysis;

/// <summary>
/// Measurement correctness: these numbers end up in a sign-off report, so a plausible but wrong
/// value is worse than a "not measured" one.
/// </summary>
public class MeasurementCorrectnessTests
{
    // --- barcode: px -> mm ---------------------------------------------------------------

    [Fact]
    public void PxToMm_DividesByDpi_NotMultiplies()
    {
        // A 0.33 mm EAN narrow bar is 31 px at 2400 dpi.
        Assert.Equal(0.328, BarcodeAnalyzer.PxToMm(31, 2400), precision: 3);
        Assert.Equal(25.4, BarcodeAnalyzer.PxToMm(2400, 2400), precision: 3);
        Assert.Equal(2.54, BarcodeAnalyzer.PxToMm(240, 2400), precision: 3);
    }

    [Fact]
    public void PxToMm_WithoutResolution_ReturnsZeroRatherThanInfinity()
    {
        Assert.Equal(0, BarcodeAnalyzer.PxToMm(100, 0));
    }

    // --- densitometer --------------------------------------------------------------------

    private static MeasurementRegion WholePlate(int width, int height) => new()
    {
        X = 0,
        Y = 0,
        Width = width,
        Height = height,
        Name = "all"
    };

    [Fact]
    public void MeasureDensity_ReportsDotAreaAndLeavesDotGainUncomputed()
    {
        const int size = 64;
        var data = new byte[size * size];
        // Half the plate inked: a 50% flat tint.
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (x < size / 2)
                    data[y * size + x] = 255;
            }
        }

        var tool = new DensitometerTool(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DensitometerTool>.Instance);

        var result = tool.MeasureDensity(data, size, size, WholePlate(size, size));

        Assert.Equal(50.0, result.DotArea, precision: 1);
        // The file tint is unknown, so gain is not computed — not reported as 0.
        Assert.Null(result.DotGain);
    }

    [Fact]
    public void MeasureDensity_PrintContrastUsesTheMeasuredTint()
    {
        const int size = 32;
        var solid = new byte[size * size];
        var tint = new byte[size * size];
        Array.Fill(solid, (byte)255);
        for (var i = 0; i < tint.Length; i++)
            tint[i] = i % 2 == 0 ? (byte)255 : (byte)0;   // 50% checkerboard

        var tool = new DensitometerTool(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DensitometerTool>.Instance);
        var region = WholePlate(size, size);

        var solidResult = tool.MeasureDensity(solid, size, size, region);
        var tintResult = tool.MeasureDensity(tint, size, size, region);

        // A 100% area has no contrast against solid; a 50% tint has plenty. The old code
        // returned the same ~98% for both because it never looked at the measurement.
        Assert.Equal(0, solidResult.PrintContrast, precision: 2);
        Assert.InRange(tintResult.PrintContrast, 0.5f, 0.95f);
        Assert.NotEqual(solidResult.PrintContrast, tintResult.PrintContrast);
    }

    [Fact]
    public void MeasureDotGain_IsMeasuredMinusRequested()
    {
        var tool = new DensitometerTool(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DensitometerTool>.Instance);

        Assert.Equal(6f, tool.MeasureDotGain(fileTintPercent: 50, measuredTintPercent: 56));
        Assert.Equal(-4f, tool.MeasureDotGain(fileTintPercent: 50, measuredTintPercent: 46));
    }

    // --- dieline overprint ---------------------------------------------------------------

    private static PlateLayer Plate(string name, SeparationColor color, int width, int height,
                                    Func<int, int, byte> ink)
    {
        var coverage = new byte[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                coverage[y * width + x] = ink(x, y);

        return new PlateLayer(new Separation { Name = name, Color = color }, width, height,
                              coverage, 200, 40, 90);
    }

    [Fact]
    public void Dieline_OverprintIsNotEvaluatedRatherThanPassing()
    {
        const int size = 32;

        // Dieline: a single horizontal rule. Artwork: paper everywhere under it.
        var dieline = Plate("Dieline", SeparationColor.SpotColor, size, size,
                            (x, y) => y == size / 2 ? (byte)255 : (byte)0);
        var cyan = Plate("Cyan", SeparationColor.ProcessCyan, size, size, (_, _) => 0);

        var job = new PrintJob
        {
            Format = RipFormat.OneBitTiffSeparations,
            Metadata = new JobMetadata { Width = size, Height = size, Resolution = 2400 }
        };

        var results = DielineAnalyzer.Analyze(job, new[] { cyan, dieline }, new PreflightSettings());

        var result = Assert.Single(results);
        Assert.True(result.KnockoutPass);
        // No varnish/white evaluator exists yet: the check must not claim a pass.
        Assert.Null(result.OverprintPass);
    }

    [Fact]
    public void Dieline_InkUnderTheLineFailsKnockout()
    {
        const int size = 32;

        var dieline = Plate("Dieline", SeparationColor.SpotColor, size, size,
                            (x, y) => y == size / 2 ? (byte)255 : (byte)0);
        // Solid artwork right under the rule.
        var cyan = Plate("Cyan", SeparationColor.ProcessCyan, size, size, (_, _) => (byte)255);

        var job = new PrintJob
        {
            Format = RipFormat.OneBitTiffSeparations,
            Metadata = new JobMetadata { Width = size, Height = size, Resolution = 2400 }
        };

        var results = DielineAnalyzer.Analyze(job, new[] { cyan, dieline }, new PreflightSettings());

        var result = Assert.Single(results);
        Assert.False(result.KnockoutPass);
    }
}
