using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoftProof.Viewer;

/// <summary>
/// Renders a plate bitmap into a device-pixel-sized buffer, choosing the resampling
/// rule from the real scale (source pixels per device pixel):
///
///   scale &gt;= 1, integer    → nearest neighbour (each plate pixel is an exact N×N block)
///   scale &gt;= 1, fractional → integer prescale + bilinear remainder ("sharp bilinear")
///   0.5 .. 1                → exact box coverage per device pixel
///   &lt; 0.5                  → area average (box) from the coarsest available source
///
/// The result is shown 1:1, so WPF never resamples it a second time.
/// </summary>
public static class ViewportResampler
{
    /// <summary>Convert any bitmap to a tightly packed Bgra32 copy.</summary>
    public static BitmapSource ToBgra32(BitmapSource source)
    {
        if (source.Format == PixelFormats.Bgra32)
            return source;

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    /// <summary>
    /// Draw <paramref name="source"/> (placed at <paramref name="sourceRect"/>, in image units)
    /// into <paramref name="dest"/> for the given view matrix and DPI scale.
    /// </summary>
    public static void Draw(
        BitmapSource source,
        Rect sourceRect,
        Matrix view,
        double dpiScaleX,
        double dpiScaleY,
        int width,
        int height,
        byte[] dest,
        double opacity = 1.0)
    {
        if (width <= 0 || height <= 0 || sourceRect.Width <= 0 || sourceRect.Height <= 0)
            return;

        var bmp = ToBgra32(source);
        var sw = bmp.PixelWidth;
        var sh = bmp.PixelHeight;
        var stride = sw * 4;
        var src = new byte[stride * sh];
        bmp.CopyPixels(src, stride, 0);

        DrawRaw(src, sw, sh, sourceRect, view, dpiScaleX, dpiScaleY, width, height, dest, opacity);
    }

    /// <summary>WPF-free core used by the renderer and by the golden-image tests.</summary>
    public static void DrawRaw(
        byte[] src, int sw, int sh, Rect sourceRect, Matrix view,
        double dpiScaleX, double dpiScaleY, int width, int height, byte[] dest, double opacity = 1.0)
    {
        var stride = sw * 4;
        var inv = view;
        if (!inv.HasInverse)
            return;
        inv.Invert();

        // Source pixels per device pixel.
        var sx = view.M11 * dpiScaleX * (sw / sourceRect.Width);
        var sy = view.M22 * dpiScaleY * (sh / sourceRect.Height);

        for (var py = 0; py < height; py++)
        {
            var row = py * width * 4;
            var dipY = (py + 0.5) / dpiScaleY;

            for (var px = 0; px < width; px++)
            {
                var dip = new Point((px + 0.5) / dpiScaleX, dipY);
                var img = inv.Transform(dip);

                var u = (img.X - sourceRect.X) / sourceRect.Width * sw;
                var v = (img.Y - sourceRect.Y) / sourceRect.Height * sh;

                double b, g, r, a;
                if (sx >= 1.0 && sy >= 1.0)
                {
                    if (IsInteger(sx) && IsInteger(sy))
                        SampleNearest(src, sw, sh, stride, u, v, out b, out g, out r, out a);
                    else
                        SampleSharpBilinear(src, sw, sh, stride, u, v, sx, sy, out b, out g, out r, out a);
                }
                else
                {
                    SampleBox(src, sw, sh, stride, u, v, sx, sy, out b, out g, out r, out a);
                }

                var o = row + px * 4;
                if (opacity >= 0.999 && a >= 254.5)
                {
                    dest[o + 0] = (byte)b;
                    dest[o + 1] = (byte)g;
                    dest[o + 2] = (byte)r;
                    dest[o + 3] = 255;
                }
                else
                {
                    var srcA = a * opacity / 255.0;
                    var da = dest[o + 3] / 255.0;
                    var outA = srcA + da * (1 - srcA);
                    if (outA <= 0.0001)
                    {
                        dest[o + 0] = dest[o + 1] = dest[o + 2] = dest[o + 3] = 0;
                        continue;
                    }

                    dest[o + 0] = (byte)((b * srcA + dest[o + 0] * da * (1 - srcA)) / outA);
                    dest[o + 1] = (byte)((g * srcA + dest[o + 1] * da * (1 - srcA)) / outA);
                    dest[o + 2] = (byte)((r * srcA + dest[o + 2] * da * (1 - srcA)) / outA);
                    dest[o + 3] = (byte)(outA * 255.0);
                }
            }
        }
    }

    private static bool IsInteger(double v) => Math.Abs(v - Math.Round(v)) < 1e-6;

    private static void SampleNearest(byte[] src, int sw, int sh, int stride,
        double u, double v, out double b, out double g, out double r, out double a)
    {
        var x = (int)Math.Floor(u);
        var y = (int)Math.Floor(v);
        x = Math.Clamp(x, 0, sw - 1);
        y = Math.Clamp(y, 0, sh - 1);
        var o = y * stride + x * 4;
        b = src[o]; g = src[o + 1]; r = src[o + 2]; a = src[o + 3];
    }

    /// <summary>Nearest at the integer prescale, bilinear only for the fractional remainder.</summary>
    private static void SampleSharpBilinear(byte[] src, int sw, int sh, int stride,
        double u, double v, double sx, double sy, out double b, out double g, out double r, out double a)
    {
        // Snap the sample point to the integer prescale grid, then take the bilinear
        // remainder inside that block so edges stay even.
        var stepX = Math.Max(1.0, Math.Floor(sx));
        var stepY = Math.Max(1.0, Math.Floor(sy));
        var ux = Math.Floor(u / stepX) * stepX;
        var vy = Math.Floor(v / stepY) * stepY;

        var fx = Math.Clamp((u - ux) / stepX, 0, 1);
        var fy = Math.Clamp((v - vy) / stepY, 0, 1);

        Bilinear(src, sw, sh, stride, ux, vy, fx, fy, out b, out g, out r, out a);
    }

    private static void Bilinear(byte[] src, int sw, int sh, int stride,
        double u, double v, double fx, double fy,
        out double b, out double g, out double r, out double a)
    {
        var x0 = Math.Clamp((int)Math.Floor(u), 0, sw - 1);
        var y0 = Math.Clamp((int)Math.Floor(v), 0, sh - 1);
        var x1 = Math.Min(x0 + 1, sw - 1);
        var y1 = Math.Min(y0 + 1, sh - 1);

        var o00 = y0 * stride + x0 * 4;
        var o10 = y0 * stride + x1 * 4;
        var o01 = y1 * stride + x0 * 4;
        var o11 = y1 * stride + x1 * 4;

        b = Mix(src[o00], src[o10], src[o01], src[o11], fx, fy);
        g = Mix(src[o00 + 1], src[o10 + 1], src[o01 + 1], src[o11 + 1], fx, fy);
        r = Mix(src[o00 + 2], src[o10 + 2], src[o01 + 2], src[o11 + 2], fx, fy);
        a = Mix(src[o00 + 3], src[o10 + 3], src[o01 + 3], src[o11 + 3], fx, fy);
    }

    private static double Mix(byte a00, byte a10, byte a01, byte a11, double fx, double fy)
    {
        var top = a00 + (a10 - a00) * fx;
        var bottom = a01 + (a11 - a01) * fx;
        return top + (bottom - top) * fy;
    }

    /// <summary>Exact area coverage of the source rectangle covered by one device pixel.</summary>
    private static void SampleBox(byte[] src, int sw, int sh, int stride,
        double u, double v, double sx, double sy, out double b, out double g, out double r, out double a)
    {
        var x0 = u - sx / 2;
        var x1 = u + sx / 2;
        var y0 = v - sy / 2;
        var y1 = v + sy / 2;

        var ix0 = Math.Max(0, (int)Math.Floor(x0));
        var ix1 = Math.Min(sw, (int)Math.Ceiling(x1));
        var iy0 = Math.Max(0, (int)Math.Floor(y0));
        var iy1 = Math.Min(sh, (int)Math.Ceiling(y1));

        if (ix1 <= ix0 || iy1 <= iy0)
        {
            SampleNearest(src, sw, sh, stride, u, v, out b, out g, out r, out a);
            return;
        }

        double sb = 0, sg = 0, sr = 0, sa = 0, total = 0;
        for (var y = iy0; y < iy1; y++)
        {
            var wy = Math.Min(y + 1, y1) - Math.Max(y, y0);
            if (wy <= 0) continue;
            for (var x = ix0; x < ix1; x++)
            {
                var wx = Math.Min(x + 1, x1) - Math.Max(x, x0);
                if (wx <= 0) continue;
                var w = wx * wy;
                var o = y * stride + x * 4;
                sb += src[o] * w;
                sg += src[o + 1] * w;
                sr += src[o + 2] * w;
                sa += src[o + 3] * w;
                total += w;
            }
        }

        if (total <= 0)
        {
            SampleNearest(src, sw, sh, stride, u, v, out b, out g, out r, out a);
            return;
        }

        b = sb / total; g = sg / total; r = sr / total; a = sa / total;
    }
}
