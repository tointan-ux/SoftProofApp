using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SoftProof.RIPParsers.Raster;

/// <summary>
/// Turns a rasterised reference (PNG/TIFF/JPEG) — or a PDF page, rasterised locally with
/// Ghostscript when it is installed — into an ink mask at the plate's pixel size, ready for
/// <see cref="SoftProof.Core.Analysis.RipReferenceComparer"/>.
/// </summary>
public static class ReferenceRaster
{
    private const int PdfRasterDpi = 300;

    /// <summary>
    /// Largest comparison the reference path will build. The mask and the resized reference
    /// both cost one buffer of this size (5 bytes per pixel combined), so 32 Mpx keeps the
    /// peak around 160 MB instead of letting a full plate request gigabytes.
    /// </summary>
    public const long MaxComparePixels = 32_000_000;

    /// <summary>Load a reference and threshold it: 1 = ink, 0 = paper.</summary>
    public static byte[] LoadInkMask(string path, int width, int height, byte threshold = 128)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));

        if (!File.Exists(path))
            throw new FileNotFoundException("Reference file not found.", path);

        var targetPixels = (long)width * height;
        if (targetPixels > MaxComparePixels)
            throw new InvalidOperationException(
                $"Reference comparison is limited to {MaxComparePixels:N0} pixels; the plate is " +
                $"{width}x{height} ({targetPixels:N0}). Compare at an overview resolution instead.");

        var isPdf = string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);
        var imagePath = isPdf ? RasterisePdf(path) : path;

        try
        {
            // GDI+ decodes lazily enough to reject an oversized source before drawing it.
            using var source = LoadBitmap(imagePath);
            var sourcePixels = (long)source.Width * source.Height;
            if (sourcePixels > MaxComparePixels)
                throw new InvalidOperationException(
                    $"Reference image is {source.Width}x{source.Height} ({sourcePixels:N0} pixels); " +
                    $"the limit is {MaxComparePixels:N0}. Export a smaller reference.");

            using var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(scaled))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, width, height));
            }

            return ThresholdToMask(scaled, width, height, threshold);
        }
        finally
        {
            if (isPdf && File.Exists(imagePath))
            {
                try { File.Delete(imagePath); } catch { /* temp file */ }
            }
        }
    }

    /// <summary>True when a PDF can be rasterised (Ghostscript found).</summary>
    public static bool CanRasterisePdf => FindGhostscript() is not null;

    /// <summary>
    /// Loads a raster reference with GDI+. Images read from a file keep the file locked for the
    /// bitmap's lifetime, so the bytes are copied into memory first and the stream released.
    /// </summary>
    private static Bitmap LoadBitmap(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes, writable: false);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or ExternalException)
        {
            throw new InvalidOperationException(
                $"Cannot read the reference image: {ex.Message}", ex);
        }
    }

    /// <summary>Thresholds a 24-bit bitmap into the 1-byte-per-pixel ink mask.</summary>
    private static unsafe byte[] ThresholdToMask(Bitmap bitmap, int width, int height, byte threshold)
    {
        var mask = new byte[(long)width * height];
        var bounds = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        try
        {
            for (var y = 0; y < height; y++)
            {
                var row = (byte*)data.Scan0 + y * data.Stride;
                var offset = (long)y * width;

                for (var x = 0; x < width; x++)
                {
                    // 24bpp is BGR in memory.
                    var blue = row[x * 3];
                    var green = row[x * 3 + 1];
                    var red = row[x * 3 + 2];
                    var luminance = (red * 299 + green * 587 + blue * 114) / 1000;
                    mask[offset + x] = luminance < threshold ? (byte)1 : (byte)0;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return mask;
    }

    private static string RasterisePdf(string pdfPath)
    {
        var gs = FindGhostscript()
                 ?? throw new InvalidOperationException(
                     "No PDF rasteriser found. Install Ghostscript, or export the reference page as TIFF/PNG and compare that.");

        var output = Path.Combine(Path.GetTempPath(), $"softproof-ref-{Guid.NewGuid():N}.png");

        var start = new ProcessStartInfo(gs)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        start.ArgumentList.Add("-dSAFER");
        start.ArgumentList.Add("-dBATCH");
        start.ArgumentList.Add("-dNOPAUSE");
        start.ArgumentList.Add("-sDEVICE=pnggray");
        start.ArgumentList.Add($"-r{PdfRasterDpi}");
        start.ArgumentList.Add("-dFirstPage=1");
        start.ArgumentList.Add("-dLastPage=1");
        start.ArgumentList.Add($"-sOutputFile={output}");
        start.ArgumentList.Add(pdfPath);

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                "Ghostscript was not found. Install it, or export the reference as TIFF/PNG.", ex);
        }

        if (process is null)
            throw new InvalidOperationException("Could not start Ghostscript.");

        using var _ = process;

        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(true); } catch { }
            throw new InvalidOperationException("Ghostscript timed out rasterising the PDF.");
        }

        if (process.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException(
                "Ghostscript could not rasterise the PDF: " + FirstLine(stderr));

        return output;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? "(no detail)" : line.Trim();
    }

    private static string? FindGhostscript()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var root = Path.Combine(programFiles, "gs");

        if (Directory.Exists(root))
        {
            foreach (var directory in Directory.GetDirectories(root)
                                                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var name in new[] { "gswin64c.exe", "gswin32c.exe" })
                {
                    var candidate = Path.Combine(directory, "bin", name);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }

        // Fall back to PATH; Process.Start resolves it there.
        return "gswin64c";
    }
}
