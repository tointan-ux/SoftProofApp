using Microsoft.Extensions.Logging.Abstractions;
using SoftProof.Core.Models;
using SoftProof.Tools;
using Xunit;

namespace SoftProof.Tests.Analysis;

/// <summary>
/// IDG press simulation. It is an experimental visualisation, but the direction of dot gain is
/// not a matter of taste: ink must spread, and 0 %/100 % must stay anchored.
/// </summary>
public class InverseDotGainTests
{
    private static readonly IdgParameters Defaults = new()
    {
        Resolution = 2400,
        ScreenFrequency = 175,
        ScreeningType = ScreeningType.FM,
        MechanicalDotGain = 10,
        OpticalDotGainMm = 0.03f,
        PressThreshold = 0.5f
    };

    private static InverseDotGainTool Tool() =>
        new(NullLogger<InverseDotGainTool>.Instance);

    private static byte[] Solid(int width, int height, bool ink)
    {
        var data = new byte[(width * height + 7) / 8];
        if (!ink)
            return data;

        Array.Fill(data, (byte)0xFF);
        return data;
    }

    private static byte[] Checkerboard(int width, int height)
    {
        var data = new byte[(width * height + 7) / 8];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (((x + y) & 1) == 0)
                    continue;

                var idx = y * width + x;
                data[idx / 8] |= (byte)(1 << (7 - (idx % 8)));
            }
        }

        return data;
    }

    private static double InkFraction(byte[] packed, int width, int height)
    {
        var inked = 0;
        for (var i = 0; i < width * height; i++)
        {
            if (((packed[i / 8] >> (7 - (i % 8))) & 1) != 0)
                inked++;
        }

        return inked / (double)(width * height);
    }

    [Fact]
    public void Simulation_LeavesPaperAndSolidAnchored()
    {
        const int size = 32;
        var tool = Tool();

        var paper = tool.ApplyIdgSimulation(Solid(size, size, ink: false), size, size, Defaults);
        var solid = tool.ApplyIdgSimulation(Solid(size, size, ink: true), size, size, Defaults);

        Assert.Equal(0.0, InkFraction(paper, size, size), precision: 3);
        // A one-pixel border is diluted by the zero-padded PSF convolution, so a solid plate
        // simulates at ~99 % rather than exactly 100 %.
        Assert.InRange(InkFraction(solid, size, size), 0.97, 1.0);
    }

    [Fact]
    public void Simulation_AddsInkAtTheMidtone()
    {
        const int size = 64;
        var tool = Tool();

        var simulated = tool.ApplyIdgSimulation(Checkerboard(size, size), size, size, Defaults);
        var coverage = InkFraction(simulated, size, size);

        // Gain must increase coverage, and must not collapse the tint to solid. The old curve
        // mapped a 50 % tint to ~78 % and then saturated it to 100 % ink.
        Assert.True(coverage > 0.50, $"expected gain, got {coverage:P1}");
        Assert.True(coverage < 0.85, $"expected a tint, not solid ink: {coverage:P1}");
    }

    [Fact]
    public void Simulation_WithoutMechanicalGain_KeepsTheTint()
    {
        const int size = 64;
        var tool = Tool();
        var parameters = new IdgParameters
        {
            Resolution = 2400,
            ScreeningType = ScreeningType.FM,
            MechanicalDotGain = 0,
            OpticalDotGainMm = 0.0f,
            PaperScatterFactor = 1.0f,
            PressThreshold = 0.5f
        };

        var simulated = tool.ApplyIdgSimulation(Checkerboard(size, size), size, size, parameters);

        Assert.InRange(InkFraction(simulated, size, size), 0.45, 0.55);
    }
}
