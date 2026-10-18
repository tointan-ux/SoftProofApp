using SoftProof.Core.Models;

namespace SoftProof.Core.Interfaces;

/// <summary>
/// Loads the per-plate raster (ink coverage) for a parsed <see cref="PrintJob"/>.
/// Implementations turn RIP payloads (TIFF separations, Harlequin page buffers, ...)
/// into <see cref="PlateLayer"/> instances the viewer can composite.
/// </summary>
public interface IPlateLoader
{
    /// <summary>True when this loader has a decoder for <paramref name="job"/>'s format.</summary>
    bool CanLoad(PrintJob job);

    /// <summary>
    /// Decodes every separation of <paramref name="job"/> into a plate layer.
    /// Returns an empty list when the job carries no decodable raster; individual
    /// unreadable plates are skipped (never guessed) and reported through
    /// <paramref name="warnings"/>.
    /// </summary>
    Task<IReadOnlyList<PlateLayer>> LoadAsync(
        PrintJob job,
        ICollection<string>? warnings = null,
        IProgress<PlateLoadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
