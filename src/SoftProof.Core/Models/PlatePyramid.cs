namespace SoftProof.Core.Models;

/// <summary>One resolution level: 8-bit ink coverage at a decimation factor relative to the source.</summary>
public sealed class PyramidLevel
{
    public PyramidLevel(int width, int height, int factor, byte[] coverage)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Pyramid level size must be positive.");
        if (coverage.Length != (long)width * height)
            throw new ArgumentException("Coverage length must equal width * height.", nameof(coverage));

        Width = width;
        Height = height;
        Factor = factor;
        Coverage = coverage;
    }

    public int Width { get; }
    public int Height { get; }
    /// <summary>Source pixels per output pixel at this level (1 = full, 2 = half, ...).</summary>
    public int Factor { get; }
    public byte[] Coverage { get; }

    /// <summary>Average coverage in percent (area-weighted), useful for status/reporting.</summary>
    public double InkedPercent()
    {
        long sum = 0;
        foreach (var value in Coverage)
            sum += value;
        return sum * 100.0 / (255.0 * Coverage.Length);
    }
}

/// <summary>
/// A preview pyramid: the decoded base level plus successively halved levels, all area-averaged
/// so tone is preserved. The viewer consumes the base; coarser levels are what a zoom-aware
/// renderer draws when the plate is zoomed out.
/// </summary>
public sealed class PlatePyramid
{
    public const int DefaultMaxLevels = 7;

    private PlatePyramid(IReadOnlyList<PyramidLevel> levels) => Levels = levels;

    public IReadOnlyList<PyramidLevel> Levels { get; }

    public PyramidLevel Base => Levels[0];
    public int Width => Base.Width;
    public int Height => Base.Height;
    public int Factor => Base.Factor;

    /// <summary>Coarsest level whose width is still at least <paramref name="minimumWidth"/>.</summary>
    public PyramidLevel LevelFor(double scale)
    {
        if (scale >= 1)
            return Levels[0];

        var target = Math.Max(1, (int)Math.Round(Width * scale));

        PyramidLevel? best = null;
        foreach (var level in Levels)
        {
            if (level.Width <= target && (best is null || level.Width > best.Width))
                best = level;
        }

        return best ?? Levels[^1]; // coarser than every stored level: use the smallest
    }

    /// <summary>
    /// Level index to draw at a given zoom (screen px per base-level px). Uses the largest
    /// level whose pixels still cover at least one screen pixel, i.e. no needless upscaling.
    /// </summary>
    public int IndexForZoom(double zoom)
    {
        if (zoom >= 1 || Levels.Count == 1)
            return 0;

        var index = (int)Math.Floor(Math.Log2(1.0 / Math.Max(zoom, 1e-6)));
        return Math.Clamp(index, 0, Levels.Count - 1);
    }

    /// <summary>Builds levels by repeated 2×2 box averaging from an already decimated base.</summary>
    public static PlatePyramid FromCoverage(byte[] coverage, int width, int height, int factor,
                                            int maxLevels = DefaultMaxLevels)
    {
        var levels = new List<PyramidLevel> { new(width, height, factor, coverage) };

        var current = levels[0];
        for (var i = 1; i < maxLevels; i++)
        {
            if (current.Width <= 64 || current.Height <= 64)
                break;

            var next = Halve(current);
            levels.Add(next);
            current = next;
        }

        return new PlatePyramid(levels);
    }

    public static PlatePyramid FromLevels(IReadOnlyList<PyramidLevel> levels)
    {
        if (levels.Count == 0)
            throw new ArgumentException("A pyramid needs at least one level.", nameof(levels));

        return new PlatePyramid(levels);
    }

    private static PyramidLevel Halve(PyramidLevel source)
    {
        var width = Math.Max(1, source.Width / 2);
        var height = Math.Max(1, source.Height / 2);
        var coverage = new byte[(long)width * height];

        for (var y = 0; y < height; y++)
        {
            var row0 = (long)(y * 2) * source.Width;
            var row1 = (long)(y * 2 + 1) * source.Width;
            var target = (long)y * width;

            for (var x = 0; x < width; x++)
            {
                var x0 = x * 2;
                var x1 = Math.Min(x0 + 1, source.Width - 1);
                var sum = source.Coverage[row0 + x0] + source.Coverage[row0 + x1];

                if (y * 2 + 1 < source.Height)
                    sum += source.Coverage[row1 + x0] + source.Coverage[row1 + x1];

                var count = y * 2 + 1 < source.Height ? 4 : 2;
                coverage[target + x] = (byte)(sum / count);
            }
        }

        return new PyramidLevel(width, height, source.Factor * 2, coverage);
    }
}
