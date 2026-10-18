namespace SoftProof.Core.Analysis;

/// <summary>
/// Result of comparing a RIP plate against a rasterised reference. <see cref="Map"/> holds one
/// byte per pixel: 0 = match, 1 = missing ink (reference has it, RIP lost it), 2 = extra ink
/// (RIP added it).
/// </summary>
public sealed record RipComparison(
    int Width,
    int Height,
    long Missing,
    long Extra,
    long ReferenceInkPixels,
    long RipInkPixels,
    byte[] Map)
{
    public long TotalErrors => Missing + Extra;
    public bool IsClean => TotalErrors == 0;

    /// <summary>Errors as a percentage of the reference's ink pixels.</summary>
    public double ErrorPercentOfInk => ReferenceInkPixels == 0
        ? (Extra > 0 ? 100.0 : 0.0)
        : TotalErrors * 100.0 / ReferenceInkPixels;
}

/// <summary>
/// Compares a RIP coverage plate with a rasterised reference (PDF page, proof, another RIP).
/// <para>
/// A small tolerance is applied by default: a mismatch is only reported when the other image
/// has no ink within <c>tolerance</c> pixels. That absorbs the sub-pixel registration difference
/// between a PDF raster and a RIP without hiding real dot loss or spurious dots.
/// </para>
/// </summary>
public static class RipReferenceComparer
{
    public const byte InkThreshold = 32; // ignore barely-there anti-aliased coverage

    public static RipComparison Compare(
        ReadOnlySpan<byte> ripCoverage,
        ReadOnlySpan<byte> referenceMask,
        int width,
        int height,
        int tolerance = 1,
        byte inkThreshold = InkThreshold)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (ripCoverage.Length < (long)width * height)
            throw new ArgumentException("RIP coverage is smaller than width × height.", nameof(ripCoverage));
        if (referenceMask.Length < (long)width * height)
            throw new ArgumentException("Reference mask is smaller than width × height.", nameof(referenceMask));

        var map = new byte[(long)width * height];
        long missing = 0, extra = 0, referenceInk = 0, ripInk = 0;
        var radius = Math.Max(0, tolerance);

        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var index = row + x;
                var rip = ripCoverage[index] >= inkThreshold;
                var reference = referenceMask[index] != 0;

                if (rip) ripInk++;
                if (reference) referenceInk++;

                if (rip == reference)
                    continue;

                if (rip && !reference)
                {
                    // Extra ink: tolerated if the reference has ink nearby (registration).
                    if (HasInkNear(referenceMask, width, height, x, y, radius, self: false))
                        continue;

                    map[index] = 2;
                    extra++;
                }
                else
                {
                    // Missing ink: tolerated if the RIP has coverage nearby.
                    if (HasCoverageNear(ripCoverage, width, height, x, y, radius, inkThreshold))
                        continue;

                    map[index] = 1;
                    missing++;
                }
            }
        }

        return new RipComparison(width, height, missing, extra, referenceInk, ripInk, map);
    }

    private static bool HasInkNear(ReadOnlySpan<byte> mask, int width, int height,
                                   int x, int y, int radius, bool self)
    {
        var x0 = Math.Max(0, x - radius);
        var x1 = Math.Min(width - 1, x + radius);
        var y0 = Math.Max(0, y - radius);
        var y1 = Math.Min(height - 1, y + radius);

        for (var yy = y0; yy <= y1; yy++)
            for (var xx = x0; xx <= x1; xx++)
            {
                if (!self && xx == x && yy == y)
                    continue;
                if (mask[yy * width + xx] != 0)
                    return true;
            }

        return false;
    }

    private static bool HasCoverageNear(ReadOnlySpan<byte> coverage, int width, int height,
                                        int x, int y, int radius, byte threshold)
    {
        var x0 = Math.Max(0, x - radius);
        var x1 = Math.Min(width - 1, x + radius);
        var y0 = Math.Max(0, y - radius);
        var y1 = Math.Min(height - 1, y + radius);

        for (var yy = y0; yy <= y1; yy++)
            for (var xx = x0; xx <= x1; xx++)
            {
                if (xx == x && yy == y)
                    continue;
                if (coverage[yy * width + xx] >= threshold)
                    return true;
            }

        return false;
    }
}
