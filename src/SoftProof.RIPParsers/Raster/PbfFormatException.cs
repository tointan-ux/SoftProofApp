namespace SoftProof.RIPParsers.Raster;

/// <summary>
/// Thrown when a RIP payload is malformed, truncated, or uses a layout/compression this build
/// cannot decode. Callers surface the message to the user instead of showing a blank plate.
/// </summary>
public sealed class PbfFormatException : Exception
{
    public PbfFormatException(string message) : base(message) { }
    public PbfFormatException(string message, Exception inner) : base(message, inner) { }
}
