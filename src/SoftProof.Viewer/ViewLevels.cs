using System;

namespace SoftProof.Viewer;

/// <summary>
/// Chooses the pyramid level for a view scale.
/// <para>
/// The decoded base level is picked from a pixel budget, so on a multi-gigapixel plate it can be
/// 1/64 (37 dpi) rather than the 1/8 (300 dpi) that older code assumed. The ceiling therefore
/// comes from the plate's own decimation factor: above it the base is already sharp enough and
/// no tiles are needed.
/// </para>
/// </summary>
public static class ViewLevels
{
    /// <summary>Doublings from native pixels down to the decoded base level.</summary>
    public static int MaxDetailLevel(int nativeFactor) =>
        Math.Max(0, (int)Math.Round(Math.Log2(Math.Max(1, nativeFactor))));

    /// <summary>
    /// Level whose decimation (2^level) serves <paramref name="nativeZoom"/>: 0 = native pixels,
    /// 1 = half resolution, 2 = quarter, and so on, never past the decoded base.
    /// </summary>
    public static int DetailLevel(double nativeZoom, int nativeFactor)
    {
        var level = nativeZoom >= 1.0
            ? 0
            : (int)Math.Floor(Math.Log2(1.0 / Math.Max(nativeZoom, 1e-6)));

        return Math.Clamp(level, 0, MaxDetailLevel(nativeFactor));
    }
}
