using System.IO;
using Microsoft.Extensions.Logging;
using SoftProof.Core.Interfaces;
using SoftProof.Core.Models;
using SoftProof.RIPParsers.Raster;
using SoftProof.RIPParsers.Raster.Pbf;

namespace SoftProof.RIPParsers;

/// <summary>
/// Parser for Harlequin page buffers (.pbuf, .hqn, .hpb).
/// <para>
/// Reads the page size, resolution, separation table and the per-separation payload directory,
/// then decodes each plate's packed raster (1-bit or 8-bit) into 8-bit ink coverage. Payloads
/// may be stored raw, PackBits-RLE or LZW compressed, and each plate is addressed by its own
/// offset/size, so plates no longer need to be contiguous in the file.
/// </para>
/// <para>
/// Harlequin's native page buffer layout is proprietary and undocumented. This parser accepts
/// the versioned SoftProof profile described on <see cref="PbfReader"/> (and the legacy
/// header + name table + sequential raw planes layout) and rejects anything else with a
/// diagnostic message instead of producing a blank page.
/// </para>
/// </summary>
public class HarlequinPageBufferParser : IRipParser
{
    private readonly ILogger<HarlequinPageBufferParser> _logger;

    /// <summary>
    /// Plates up to this many pixels are decoded eagerly and cached on
    /// <see cref="Separation.Data"/>. Larger plates stay on disk and are decoded by the plate
    /// loader, which can decimate them to a preview size.
    /// </summary>
    public long MaxInlinePixels { get; init; } = 60_000_000;

    public RipFormat SupportedFormat => RipFormat.HarlequinPageBuffer;
    public string[] SupportedExtensions => new[] { ".pbuf", ".hqn", ".hpb" };

    public HarlequinPageBufferParser(ILogger<HarlequinPageBufferParser> logger)
    {
        _logger = logger;
    }

    public async Task<bool> CanParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var head = new byte[(int)Math.Min(16, fs.Length)];
            var read = await fs.ReadAsync(head.AsMemory(), cancellationToken);
            return read >= 3 && PbfReader.HasPbfSignature(head.AsSpan(0, read));
        }
        catch
        {
            return false;
        }
    }

    public Task<PrintJob> ParseAsync(string filePath, CancellationToken cancellationToken = default) =>
        ParseMultipleAsync(new[] { filePath }, cancellationToken);

    public async Task<PrintJob> ParseMultipleAsync(IEnumerable<string> filePaths,
                                                   CancellationToken cancellationToken = default)
    {
        var files = filePaths.ToList();
        if (files.Count == 0)
            throw new ArgumentException("No files supplied.", nameof(filePaths));

        var job = new PrintJob
        {
            Name = Path.GetFileNameWithoutExtension(files[0]),
            FilePath = files[0],
            Format = RipFormat.HarlequinPageBuffer
        };

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await AppendFileAsync(job, file, cancellationToken);
        }

        job.Status = job.Separations.Count > 0 ? JobStatus.Ready : JobStatus.Error;

        _logger.LogInformation(
            "Parsed page buffer {Name}: {Count} separations, {Width}x{Height} @ {Resolution} dpi",
            job.Name, job.Separations.Count, job.Metadata.Width, job.Metadata.Height,
            job.Metadata.Resolution);

        return job;
    }

    private async Task AppendFileAsync(PrintJob job, string filePath, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Parsing Harlequin page buffer: {File}", filePath);

        // Header + directory are tiny; decoding the plates is the expensive part.
        var (header, entries) = await Task.Run(() => PbfReader.ReadDocument(filePath), cancellationToken);

        if (job.Metadata.Width == 0)
        {
            job.Metadata.Width = header.Width;
            job.Metadata.Height = header.Height;
            job.Metadata.Resolution = header.Resolution;
            job.Metadata.BitDepth = header.BitDepth;
            job.Metadata.ColorSpace = MapColorSpace(header.ColorSpace, header.SeparationCount);
            job.Metadata.RipName = "Harlequin RIP";
            job.Metadata.RipVersion = $"page buffer v{header.Version}";
        }
        else if (job.Metadata.Width != header.Width || job.Metadata.Height != header.Height)
        {
            throw new PbfFormatException(
                $"Page buffers in one job must share a page size: " +
                $"{job.Metadata.Width}x{job.Metadata.Height} vs {header.Width}x{header.Height} " +
                $"in '{Path.GetFileName(filePath)}'.");
        }

        var decodeInline = (long)header.Width * header.Height <= MaxInlinePixels;
        var baseIndex = job.Separations.Count;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = string.IsNullOrWhiteSpace(entry.Name)
                ? $"Separation_{entry.Index + 1}"
                : entry.Name;

            var separation = new Separation
            {
                Name = name,
                Index = baseIndex + entry.Index,
                FilePath = filePath,
                DataOffset = checked((int)entry.DataOffset),
                DataSize = entry.CompressedSize,
                Color = SeparationColorResolver.FromName(name),
                IsSpotColor = SeparationColorResolver.IsSpot(name)
            };

            if (separation.IsSpotColor)
                separation.SpotColorName = name;

            if (decodeInline)
            {
                try
                {
                    separation.Data = await Task.Run(
                        () => PbfReader.ReadPlate(filePath, header, entry), cancellationToken);
                }
                catch (PbfFormatException ex)
                {
                    // Keep the separation listed with its metadata; the plate loader retries and
                    // the status bar reports why a plate could not be rasterised.
                    _logger.LogWarning(ex, "Could not decode plate {Name} in {File}", name, filePath);
                    separation.Data = null;
                }
            }

            job.Separations.Add(separation);
            job.Metadata.SeparationNames.Add(separation.Name);
        }
    }

    private static ColorSpace MapColorSpace(int raw, int separationCount) => raw switch
    {
        0 => ColorSpace.Gray,
        1 => ColorSpace.RGB,
        2 => ColorSpace.CMYK,
        3 => ColorSpace.MultiChannel,
        4 => ColorSpace.Lab,
        5 => ColorSpace.DeviceN,
        _ => separationCount switch
        {
            1 => ColorSpace.Gray,
            3 => ColorSpace.RGB,
            4 => ColorSpace.CMYK,
            _ => ColorSpace.MultiChannel
        }
    };
}

/// <summary>Maps plate names to <see cref="SeparationColor"/> (process inks plus common spots).</summary>
internal static class SeparationColorResolver
{
    private static readonly string[] ProcessKeys =
    {
        "cyan", "magenta", "yellow", "black", "key",
        "_c", "-c", "_m", "-m", "_y", "-y", "_k", "-k"
    };

    public static SeparationColor FromName(string name)
    {
        var lower = (name ?? string.Empty).ToLowerInvariant();

        if (lower.Contains("cyan") || lower.EndsWith("_c") || lower.EndsWith("-c")) return SeparationColor.ProcessCyan;
        if (lower.Contains("magenta") || lower.EndsWith("_m") || lower.EndsWith("-m")) return SeparationColor.ProcessMagenta;
        if (lower.Contains("yellow") || lower.EndsWith("_y") || lower.EndsWith("-y")) return SeparationColor.ProcessYellow;
        if (lower.Contains("black") || lower.Contains("key") || lower.EndsWith("_k") || lower.EndsWith("-k")) return SeparationColor.ProcessBlack;
        if (lower.Contains("orange")) return SeparationColor.Orange;
        if (lower.Contains("green")) return SeparationColor.Green;
        if (lower.Contains("violet") || lower.Contains("purple")) return SeparationColor.Violet;
        if (lower.Contains("red")) return SeparationColor.Red;
        if (lower.Contains("blue")) return SeparationColor.Blue;
        if (lower.Contains("white")) return SeparationColor.White;
        if (lower.Contains("varnish") || lower.Contains("coating")) return SeparationColor.Varnish;
        if (lower.Contains("primer")) return SeparationColor.Primer;
        return SeparationColor.SpotColor;
    }

    public static bool IsSpot(string name)
    {
        var lower = (name ?? string.Empty).ToLowerInvariant();
        return !ProcessKeys.Any(lower.Contains);
    }
}
