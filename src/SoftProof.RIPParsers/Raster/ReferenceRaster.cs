using System.Diagnostics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SoftProof.RIPParsers.Raster;

/// <summary>
/// Turns a rasterised reference (PNG/TIFF/JPEG) — or a PDF page, rasterised locally with
/// Ghostscript when it is installed — into an ink mask at the plate's pixel size, ready for
/// <see cref="SoftProof.Core.Analysis.RipReferenceComparer"/>.
/// </summary>
public static class ReferenceRaster
{
    private const int PdfRasterDpi = 300;

    /// <summary>Load a reference and threshold it: 1 = ink, 0 = paper.</summary>
    public static byte[] LoadInkMask(string path, int width, int height, byte threshold = 128)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));

        if (!File.Exists(path))
            throw new FileNotFoundException("Reference file not found.", path);

        var isPdf = string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);
        var imagePath = isPdf ? RasterisePdf(path) : path;

        try
        {
            using var image = Image.Load<Rgba32>(imagePath);
            image.Mutate(x => x.Resize(width, height));

            var mask = new byte[(long)width * height];

            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    var offset = y * width;
                    for (var x = 0; x < width; x++)
                    {
                        var p = row[x];
                        var luminance = (p.R * 299 + p.G * 587 + p.B * 114) / 1000;
                        mask[offset + x] = luminance < threshold ? (byte)1 : (byte)0;
                    }
                }
            });

            return mask;
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
