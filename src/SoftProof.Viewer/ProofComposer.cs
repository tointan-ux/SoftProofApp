using SoftProof.Core.Interfaces;
using SoftProof.Core.Models;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CorePixelFormat = SoftProof.Core.Interfaces.PixelFormat;

namespace SoftProof.Viewer;

/// <summary>
/// Bridges the plate composite and the ICC proofing transform.
/// <para>
/// A soft proof needs the press-space data, not the screen approximation the preview composite
/// uses: here each plate becomes one CMYK channel at full resolution of the preview level, lcms
/// renders it through the proof transform, and the RGB result is handed back as a WPF bitmap.
/// </para>
/// </summary>
internal static class ProofComposer
{
    /// <summary>Channel index for a process ink, or -1 when the ink has no CMYK slot.</summary>
    internal static int ChannelOf(SeparationColor colour) => colour switch
    {
        SeparationColor.ProcessCyan => 0,
        SeparationColor.ProcessMagenta => 1,
        SeparationColor.ProcessYellow => 2,
        SeparationColor.ProcessBlack => 3,
        _ => -1
    };

    /// <summary>
    /// Packs the visible plates into 8-bit CMYK. Spots, varnish and white ink are skipped: lcms
    /// would need a device link or an n-channel profile for those, and pretending they are CMYK
    /// would proof them as the wrong ink.
    /// </summary>
    internal static BitmapData BuildCmyk(
        byte[][] coverage,
        SeparationColor[] colours,
        bool[] inverted,
        double[] opacity,
        int width,
        int height)
    {
        var stride = width * 4;
        var pixels = new byte[checked((int)((long)stride * height))];
        var pixelCount = (long)width * height;

        for (var i = 0; i < coverage.Length; i++)
        {
            var channel = ChannelOf(colours[i]);
            if (channel < 0)
                continue;

            var source = coverage[i];
            var flip = inverted[i];
            var alpha = Math.Clamp(opacity[i], 0.0, 1.0);

            for (long index = 0; index < pixelCount; index++)
            {
                var value = source[index];

                if (flip)
                    value = (byte)(255 - value);

                if (alpha < 1.0)
                    value = (byte)(value * alpha);

                // Two plates mapped to the same ink: keep the stronger one rather than double it.
                var slot = index * 4 + channel;
                if (value > pixels[slot])
                    pixels[slot] = value;
            }
        }

        return new BitmapData
        {
            Width = width,
            Height = height,
            Stride = stride,
            Format = CorePixelFormat.CMYK32,
            Pixels = pixels
        };
    }

    /// <summary>How many plates would contribute a CMYK channel. Zero means the proof would be paper.</summary>
    internal static int ProcessChannelCount(SeparationColor[] colours)
    {
        var seen = new bool[4];

        foreach (var colour in colours)
        {
            var channel = ChannelOf(colour);
            if (channel >= 0)
                seen[channel] = true;
        }

        var count = 0;
        foreach (var used in seen)
            if (used) count++;

        return count;
    }

    /// <summary>RGB24 (lcms output) to a frozen BGRA32 bitmap WPF can draw.</summary>
    internal static BitmapSource ToBgra(BitmapData rgb)
    {
        var width = rgb.Width;
        var height = rgb.Height;
        var stride = width * 4;
        var bgra = new byte[checked((int)((long)stride * height))];

        for (var y = 0; y < height; y++)
        {
            var source = y * rgb.Stride;
            var target = y * stride;

            for (var x = 0; x < width; x++)
            {
                var s = source + x * 3;
                var t = target + x * 4;

                bgra[t] = rgb.Pixels[s + 2];
                bgra[t + 1] = rgb.Pixels[s + 1];
                bgra[t + 2] = rgb.Pixels[s];
                bgra[t + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, stride);
        bitmap.Freeze();
        return bitmap;
    }
}
