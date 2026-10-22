using SoftProof.Core.Interfaces;
using SoftProof.Core.Models;
using SoftProof.RIPParsers.Raster.Pbf;
using SoftProof.RIPParsers.Raster.Pyramid;
using SoftProof.RIPParsers.Raster.Tiff;

namespace SoftProof.RIPParsers.Raster;

/// <summary>
/// Decodes RIP payloads into <see cref="PlateLayer"/>s: ink coverage (0 = paper, 255 = solid)
/// at one byte per pixel, which is what <c>PlateViewer</c> and the densitometer consume.
/// </summary>
public sealed class PlateLoader : IPlateLoader
{
    private readonly PyramidCache? _cache;

    public PlateLoader(PyramidCache? cache = null) => _cache = cache;

    /// <summary>
    /// Upper bound on decoded pixels per plate. Larger plates are decimated (and the job's
    /// resolution is scaled by the same factor, so physical measurements stay correct).
    /// A 2400 dpi B1 plate is ~6.5 Gpx; decoding that eagerly would exhaust memory.
    /// </summary>
    public int MaxPixelsPerPlate { get; init; } = 120_000_000;

    public bool CanLoad(PrintJob job) => job.Format is
        RipFormat.HarlequinPageBuffer or RipFormat.OneBitTiffSeparations or RipFormat.MultiChannelTiff;

    public Task<IReadOnlyList<PlateLayer>> LoadAsync(
        PrintJob job,
        ICollection<string>? warnings = null,
        IProgress<PlateLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Load(job, warnings, progress, cancellationToken), cancellationToken);
    }

    private IReadOnlyList<PlateLayer> Load(PrintJob job, ICollection<string>? warnings,
                                           IProgress<PlateLoadProgress>? progress,
                                           CancellationToken cancellationToken)
    {
        var plates = new List<PlateLayer>(job.Separations.Count);

        switch (job.Format)
        {
            case RipFormat.HarlequinPageBuffer:
                LoadPageBuffer(job, plates, warnings, cancellationToken);
                break;

            case RipFormat.OneBitTiffSeparations:
                LoadOneBitTiff(job, plates, warnings, progress, cancellationToken);
                break;

            case RipFormat.MultiChannelTiff:
                LoadMultiChannelTiff(job, plates, warnings, cancellationToken);
                break;

            default:
                warnings?.Add($"{job.Format} has no plate decoder in this build.");
                return plates;
        }

        foreach (var plate in plates)
            plate.ComputeStats();

        return plates;
    }

    private void LoadPageBuffer(PrintJob job, List<PlateLayer> plates, ICollection<string>? warnings,
                                CancellationToken ct)
    {
        var path = !string.IsNullOrEmpty(job.FilePath)
            ? job.FilePath
            : job.Separations.FirstOrDefault(s => !string.IsNullOrEmpty(s.FilePath))?.FilePath;

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            warnings?.Add("Page buffer file is missing; nothing to rasterise.");
            return;
        }

        PbfHeader header;
        IReadOnlyList<PbfSeparation> entries;

        try
        {
            (header, entries) = PbfReader.ReadDocument(path);
        }
        catch (PbfFormatException ex)
        {
            // Surface the real reason (it is shown in the status bar) instead of a blank page.
            warnings?.Add($"Page buffer could not be read: {ex.Message}");
            return;
        }

        var width = header.Width;
        var height = header.Height;

        for (var i = 0; i < job.Separations.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var separation = job.Separations[i];

            byte[]? coverage = null;

            // Parsers may have already decoded the payload onto the Separation.
            if (separation.Data is { Length: > 0 } cached && cached.Length == (long)width * height)
            {
                coverage = cached;
            }
            else if (i < entries.Count)
            {
                try
                {
                    coverage = PbfReader.ReadPlate(path, header, entries[i]);
                }
                catch (PbfFormatException ex)
                {
                    warnings?.Add($"Plate '{separation.Name}' skipped: {ex.Message}");
                }
            }

            if (coverage is null)
                continue;

            AddPlate(plates, separation, coverage, width, height, header.Resolution, job, warnings);
        }
    }

    private void LoadOneBitTiff(PrintJob job, List<PlateLayer> plates, ICollection<string>? warnings,
                                IProgress<PlateLoadProgress>? progress, CancellationToken ct)
    {
        var plateCount = job.Separations.Count;
        var index = 0;

        foreach (var separation in job.Separations)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(separation.FilePath) || !File.Exists(separation.FilePath))
            {
                warnings?.Add($"Plate '{separation.Name}' has no readable file.");
                index++;
                continue;
            }

            try
            {
                var info = TiffReader.GetInfo(separation.FilePath, separation.PageIndex);

                if (info.BitsPerSample == 1 && info.SamplesPerPixel == 1 &&
                    info.Compression is 1 or 4 or 5 or 32773 &&
                    (long)info.Width * info.Height > MaxPixelsPerPlate)
                {
                    var width = 0;
                    var height = 0;
                    var factor = 1;
                    PlatePyramid? pyramid = null;
                    var fromCache = false;

                    if (_cache is not null &&
                        _cache.TryGet(separation.FilePath, separation.PageIndex, MaxPixelsPerPlate,
                                      out pyramid) && pyramid is not null)
                    {
                        fromCache = true;
                        width = pyramid.Width;
                        height = pyramid.Height;
                        factor = pyramid.Factor;
                        progress?.Report(new PlateLoadProgress(index, plateCount, separation.Name,
                                                               "cached", 100, true));
                    }
                    else
                    {
                        // Stream strips and reduce while decoding; report progress per block.
                        var plateProgress = new CallbackProgress<double>(p =>
                            progress?.Report(new PlateLoadProgress(
                                index, plateCount, separation.Name, "decoding", p, false)));

                        var coverage = TiffReader.ReadBilevelCoverage(
                            separation.FilePath, separation.PageIndex, MaxPixelsPerPlate,
                            out width, out height, out factor, plateProgress, ct);

                        pyramid = PlatePyramid.FromCoverage(coverage, width, height, factor);

                        // Persist for instant reopen; a cache failure must not fail the load.
                        try
                        {
                            _cache?.Put(separation.FilePath, separation.PageIndex,
                                        MaxPixelsPerPlate, pyramid);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            warnings?.Add($"Pyramid cache not written: {ex.Message}");
                        }

                        progress?.Report(new PlateLoadProgress(index, plateCount, separation.Name,
                                                               "decoded", 100, false));
                    }

                    // Keep the job at its native resolution: the decoded raster is smaller, but
                    // the plate is still a 2400 dpi plate. The viewer derives the view DPI from
                    // the pyramid factor, so nothing has to rewrite the job metadata per plate.
                    var resolution = Math.Max(1, job.Metadata.Resolution / factor);

                    warnings?.Add(
                        $"Plate '{separation.Name}' is {info.Width}x{info.Height} px; decoded " +
                        $"{factor}x smaller to {width}x{height} px at {resolution} dpi" +
                        (fromCache ? " (pyramid cache)" : string.Empty) + ".");

                    AddPlate(plates, separation, pyramid!.Base.Coverage, width, height,
                             resolution, job, warnings, pyramid);
                }
                else
                {
                    var image = TiffReader.Read(separation.FilePath, separation.PageIndex);
                    var coverage = image.InkCoverage(0);
                    AddPlate(plates, separation, coverage, image.Width, image.Height,
                             job.Metadata.Resolution, job, warnings);
                    progress?.Report(new PlateLoadProgress(index, plateCount, separation.Name,
                                                           "decoded", 100, false));
                }
            }
            catch (OperationCanceledException)
            {
                throw; // cancellation is not a plate failure
            }
            catch (Exception ex) when (ex is PbfFormatException or IOException or UnauthorizedAccessException)
            {
                warnings?.Add($"Plate '{separation.Name}' skipped: {ex.Message}");
            }

            index++;
        }
    }

    private void LoadMultiChannelTiff(PrintJob job, List<PlateLayer> plates, ICollection<string>? warnings,
                                      CancellationToken ct)
    {
        var path = !string.IsNullOrEmpty(job.FilePath)
            ? job.FilePath
            : job.Separations.FirstOrDefault()?.FilePath;

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            warnings?.Add("Multi-channel TIFF file is missing.");
            return;
        }

        TiffImage image;
        try
        {
            image = TiffReader.Read(path);
        }
        catch (PbfFormatException ex)
        {
            warnings?.Add($"Multi-channel TIFF could not be read: {ex.Message}");
            return;
        }

        foreach (var separation in job.Separations)
        {
            ct.ThrowIfCancellationRequested();

            var index = separation.Index;
            if (index < 0 || index >= image.SamplesPerPixel)
            {
                warnings?.Add(
                    $"Plate '{separation.Name}' references channel {index}, " +
                    $"but the TIFF has {image.SamplesPerPixel} samples.");
                continue;
            }

            var coverage = image.InkCoverage(index);
            AddPlate(plates, separation, coverage, image.Width, image.Height,
                     job.Metadata.Resolution, job, warnings);
        }
    }

    private void AddPlate(List<PlateLayer> plates, Separation separation, byte[] coverage,
                          int width, int height, int resolution, PrintJob job, ICollection<string>? warnings,
                          PlatePyramid? pyramid = null)
    {
        if (width <= 0 || height <= 0 || coverage.Length != (long)width * height)
        {
            warnings?.Add(
                $"Plate '{separation.Name}' has inconsistent raster size " +
                $"({width}x{height}, {coverage.Length} samples).");
            return;
        }

        var factor = 1;
        while ((long)(width / factor) * (height / factor) > MaxPixelsPerPlate)
            factor <<= 1;

        if (factor > 1)
        {
            coverage = Downsample(coverage, width, height, factor);
            width /= factor;
            height /= factor;

            // Keep mm measurements honest: fewer pixels per mm, same physical page size.
            if (resolution > 0)
                resolution = Math.Max(1, resolution / factor);

            warnings?.Add(
                $"Plate '{separation.Name}': overview at 1/{factor} ({width}x{height} px, {resolution} dpi) " +
                $"to stay within the {MaxPixelsPerPlate:N0} pixel budget. Native pixels are streamed " +
                "from the plate strips at 100% and beyond.");
        }

        var (r, g, b) = Palette(separation);
        plates.Add(new PlateLayer(separation, width, height, coverage, r, g, b, pyramid));
    }

    private static byte[] Downsample(byte[] source, int width, int height, int factor)
    {
        var newWidth = width / factor;
        var newHeight = height / factor;
        var result = new byte[(long)newWidth * newHeight];

        for (var y = 0; y < newHeight; y++)
        {
            var srcRow = (long)y * factor * width;
            var dstRow = (long)y * newWidth;
            for (var x = 0; x < newWidth; x++)
            {
                long sum = 0;
                for (var dy = 0; dy < factor; dy++)
                {
                    var row = srcRow + (long)dy * width;
                    for (var dx = 0; dx < factor; dx++)
                        sum += source[row + x * factor + dx];
                }
                result[dstRow + x] = (byte)(sum / (factor * factor));
            }
        }

        return result;
    }

    /// <summary>Display ink colour per separation (sRGB), matching the demo job's palette.</summary>
    public static (byte R, byte G, byte B) Palette(Separation separation) => separation.Color switch
    {
        SeparationColor.ProcessCyan => (0, 174, 239),
        SeparationColor.ProcessMagenta => (236, 0, 140),
        SeparationColor.ProcessYellow => (255, 242, 0),
        SeparationColor.ProcessBlack => (35, 31, 32),
        SeparationColor.Orange => (245, 130, 32),
        SeparationColor.Green => (0, 166, 81),
        SeparationColor.Violet => (128, 55, 155),
        SeparationColor.Red => (235, 64, 82),
        SeparationColor.Blue => (51, 102, 170),
        SeparationColor.White => (255, 255, 255),
        SeparationColor.Varnish => (160, 90, 255),
        SeparationColor.Primer => (200, 200, 180),
        _ => (200, 160, 40) // spot colour without an alternate-space colour
    };
}

/// <summary>Synchronous <see cref="IProgress{T}"/> adapter: reports on the calling thread.</summary>
internal sealed class CallbackProgress<T> : IProgress<T>
{
    private readonly Action<T> _callback;

    public CallbackProgress(Action<T> callback) => _callback = callback;

    public void Report(T value) => _callback(value);
}
