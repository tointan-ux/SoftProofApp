using System;
using System.Collections.Generic;

namespace SoftProof.Core.Models;

/// <summary>
/// Tone Value Increase (TVI) / Dot Gain curve for a single separation/plate.
/// Maps input tone values (0-100%) to printed tone values (0-100%).
/// </summary>
public sealed class TVICurve
{
    /// <summary>Separation this curve applies to.</summary>
    public SeparationColor Separation { get; set; }

    /// <summary>Name/label for this curve (e.g., "Standard Offset", "Custom").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Input/output tone pairs (0-100%). Must be sorted by input tone ascending.</summary>
    /// <remarks>
    /// At minimum 2 points (0% and 100%). Standard TVI curves have 10-20 points.
    /// Linear interpolation is used between points.
    /// </remarks>
    public List<TVIPoint> Points { get; set; } = new();

    /// <summary>Creates a standard offset TVI curve (mid-tone gain ~16-18%).</summary>
    public static TVICurve CreateStandardOffset(SeparationColor separation)
    {
        return new TVICurve
        {
            Separation = separation,
            Name = "Standard Offset",
            Points = new List<TVIPoint>
            {
                new(0, 0),
                new(5, 6.5),
                new(10, 12),
                new(15, 17.5),
                new(20, 23),
                new(25, 28.5),
                new(30, 34),
                new(40, 45),
                new(50, 56),   // ~16% mid-tone gain
                new(60, 66),
                new(70, 75.5),
                new(80, 84),
                new(90, 92),
                new(95, 96.5),
                new(100, 100)
            }
        };
    }

    /// <summary>Creates a newsprint TVI curve (higher gain ~25-30%).</summary>
    public static TVICurve CreateNewsprint(SeparationColor separation)
    {
        return new TVICurve
        {
            Separation = separation,
            Name = "Newsprint",
            Points = new List<TVIPoint>
            {
                new(0, 0),
                new(5, 8),
                new(10, 15),
                new(15, 21),
                new(20, 27),
                new(25, 33),
                new(30, 39),
                new(40, 49.5),
                new(50, 61),   // ~22% mid-tone gain
                new(60, 71.5),
                new(70, 80.5),
                new(80, 88.5),
                new(90, 95),
                new(95, 97.5),
                new(100, 100)
            }
        };
    }

    /// <summary>Creates a linear/identity curve (no dot gain).</summary>
    public static TVICurve CreateLinear(SeparationColor separation)
    {
        return new TVICurve
        {
            Separation = separation,
            Name = "Linear (No Gain)",
            Points = new List<TVIPoint>
            {
                new(0, 0),
                new(100, 100)
            }
        };
    }

    /// <summary>Applies TVI curve to a tone value (0-100%).</summary>
    public double Apply(double inputTone)
    {
        if (Points.Count < 2)
            return inputTone; // No curve defined, return linear

        // Clamp input
        inputTone = Math.Clamp(inputTone, 0, 100);

        // Find surrounding points
        for (var i = 0; i < Points.Count - 1; i++)
        {
            var p1 = Points[i];
            var p2 = Points[i + 1];

            if (inputTone >= p1.Input && inputTone <= p2.Input)
            {
                // Linear interpolation
                var t = (inputTone - p1.Input) / (p2.Input - p1.Input);
                return p1.Output + t * (p2.Output - p1.Output);
            }
        }

        // Beyond last point (should not happen with clamping)
        return Points[^1].Output;
    }

    /// <summary>Validates the curve (monotonic, proper bounds).</summary>
    public bool IsValid(out string error)
    {
        if (Points.Count < 2)
        {
            error = "TVI curve must have at least 2 points";
            return false;
        }

        if (Points[0].Input != 0 || Points[0].Output != 0)
        {
            error = "First point must be (0, 0)";
            return false;
        }

        if (Points[^1].Input != 100 || Points[^1].Output != 100)
        {
            error = "Last point must be (100, 100)";
            return false;
        }

        for (var i = 0; i < Points.Count - 1; i++)
        {
            if (Points[i].Input >= Points[i + 1].Input)
            {
                error = $"Input values must be strictly increasing (point {i})";
                return false;
            }
            if (Points[i].Output > Points[i + 1].Output)
            {
                error = $"Output values must be non-decreasing (point {i})";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }
}

/// <summary>
/// Single point on a TVI curve.
/// </summary>
public sealed record TVIPoint
{
    /// <summary>Input tone percentage (0-100%).</summary>
    public double Input { get; init; }

    /// <summary>Output/printed tone percentage (0-100%).</summary>
    public double Output { get; init; }

    public TVIPoint() { }

    public TVIPoint(double input, double output)
    {
        Input = input;
        Output = output;
    }
}