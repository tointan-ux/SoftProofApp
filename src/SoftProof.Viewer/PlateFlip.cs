namespace SoftProof.Viewer;

/// <summary>
/// Per-plate mirroring. A flip belongs to the plate, not to the view, so each plate in a
/// composite can be mirrored independently while the canvas geometry stays unchanged.
/// </summary>
public static class PlateFlip
{
    /// <summary>Source column for display column <paramref name="x"/>.</summary>
    public static int MapX(int x, int width, bool flip) =>
        flip && width > 0 ? width - 1 - x : x;

    /// <summary>Source row for display row <paramref name="y"/>.</summary>
    public static int MapY(int y, int height, bool flip) =>
        flip && height > 0 ? height - 1 - y : y;

    /// <summary>
    /// Row index inside a buffer that holds <paramref name="rows"/> rows read in display order.
    /// A vertical flip reverses the buffer index rather than the read range, so the decode stays
    /// one contiguous read.
    /// </summary>
    public static int BufferRow(int displayRow, int rows, bool flip) =>
        flip && rows > 0 ? rows - 1 - displayRow : displayRow;
}
