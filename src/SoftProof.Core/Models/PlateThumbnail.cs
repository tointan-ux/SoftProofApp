using SoftProof.Core.Models;

namespace SoftProof.Core.Models;

/// <summary>
/// Builds small preview bitmaps from plate coverage without WPF. Output is BGRA32, ink drawn in
/// the plate's display colour over paper white, so a separation is recognisable at a glance.
/// </summary>
public static class PlateThumbnail
{
    /// <summary>
    /// Area-averaged thumbnail of <paramref name="coverage"/> at <paramref name="size"/> px.
    /// Returns BGRA32 bytes (size * size * 4).
    /// </summary>
    public static byte[] Build(byte[] coverage, int width, int height, int size,
                               byte r, byte g, byte b)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Plate size must be positive.");
        if (coverage.Length != (long)width * height)
            throw new ArgumentException("Coverage length must equal width * height.", nameof(coverage));
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));

        var pixels = new byte[size * size * 4];

        for (var ty = 0; ty < size; ty++)
        {
            var y0 = (int)((long)ty * height / size);
            var y1 = Math.Max(y0 + 1, (int)((long)(ty + 1) * height / size));

            for (var tx = 0; tx < size; tx++)
            {
                var x0 = (int)((long)tx * width / size);
                var x1 = Math.Max(x0 + 1, (int)((long)(tx + 1) * width / size));

                long sum = 0;
                var count = 0;
                for (var y = y0; y < y1; y++)
                {
                    var row = (long)y * width;
                    for (var x = x0; x < x1; x++)
                    {
                        sum += coverage[row + x];
                        count++;
                    }
                }

                var ink = count == 0 ? 0f : sum / (count * 255f);
                var o = (ty * size + tx) * 4;
                pixels[o] = (byte)(255 - ink * (255 - b));      // B
                pixels[o + 1] = (byte)(255 - ink * (255 - g));  // G
                pixels[o + 2] = (byte)(255 - ink * (255 - r));  // R
                pixels[o + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>Thumbnail for a plate layer, using its own dimensions and ink colour.</summary>
    public static byte[] Build(PlateLayer layer, int size) =>
        Build(layer.Coverage, layer.Width, layer.Height, size, layer.R, layer.G, layer.B);

    /// <summary>
    /// Composite several plates into one thumbnail (multiply, like the viewer) so the navigator
    /// shows what the page actually looks like.
    /// </summary>
    public static byte[] BuildComposite(IReadOnlyList<PlateLayer> layers, int width, int height,
                                        int size)
    {
        var result = new byte[size * size * 4];
        for (var i = 0; i < result.Length; i += 4)
        {
            result[i] = 255;
            result[i + 1] = 255;
            result[i + 2] = 255;
            result[i + 3] = 255;
        }

        foreach (var layer in layers)
        {
            var thumb = Build(layer, size);
            for (var i = 0; i < result.Length; i += 4)
            {
                result[i] = (byte)(result[i] * thumb[i] / 255);
                result[i + 1] = (byte)(result[i + 1] * thumb[i + 1] / 255);
                result[i + 2] = (byte)(result[i + 2] * thumb[i + 2] / 255);
            }
        }

        return result;
    }
}
