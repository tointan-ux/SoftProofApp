namespace SoftProof.Core.Models;

/// <summary>Progress of a plate raster load, suitable for a status bar or progress dialog.</summary>
/// <param name="PlateIndex">Zero-based index of the plate being decoded.</param>
/// <param name="PlateCount">Total plates in the job.</param>
/// <param name="PlateName">Separation name, for the status text.</param>
/// <param name="Stage">Human-readable stage ("decoding", "cached", ...).</param>
/// <param name="Percent">Overall completion, 0..100.</param>
/// <param name="FromCache">True when the plate was served from the pyramid cache.</param>
public readonly record struct PlateLoadProgress(
    int PlateIndex,
    int PlateCount,
    string PlateName,
    string Stage,
    double Percent,
    bool FromCache);
