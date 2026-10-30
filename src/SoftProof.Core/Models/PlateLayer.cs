namespace SoftProof.Core.Models;

/// <summary>
/// Raster data for one plate (separation), stored as 8-bit ink coverage (0 = no ink, 255 = solid).
/// Also carries the per-plate display state (visible / solo / inverted) used by the viewer.
/// </summary>
/// <summary>Ink bounding box in plate pixels.</summary>
public readonly record struct PixelBounds(int X, int Y, int Width, int Height)
{
    public static readonly PixelBounds Empty = new(0, 0, 0, 0);

    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long Area => (long)Width * Height;
}

public sealed class PlateLayer
{
    public PlateLayer(Separation separation, int width, int height, byte[] coverage, byte r, byte g, byte b,
                      PlatePyramid? pyramid = null)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Plate size must be positive.");
        if (coverage.LongLength != (long)width * height)
            throw new ArgumentException("Coverage length must equal width * height.", nameof(coverage));
        
        Separation = separation;
        Width = width;
        Height = height;
        Coverage = coverage;
        R = r;
        G = g;
        B = b;
        Pyramid = pyramid;
    }
    
    public Separation Separation { get; }
    public string Name => Separation.Name;
    public int Width { get; }
    public int Height { get; }

    /// <summary>
    /// Optional preview pyramid. Level 0 is <see cref="Coverage"/>; coarser levels let the
    /// viewer draw zoomed-out views from far fewer pixels.
    /// </summary>
    public PlatePyramid? Pyramid { get; }
    
    /// <summary>Row-major 8-bit coverage, <see cref="Width"/> * <see cref="Height"/> bytes.</summary>
    public byte[] Coverage { get; }
    
    /// <summary>Ink colour used to display this plate (sRGB).</summary>
    public byte R { get; private set; }
    public byte G { get; private set; }
    public byte B { get; private set; }

    /// <summary>Override the ink colour used when compositing this plate.</summary>
    public void SetDisplayColor(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
    }
    
    private static int _nextId;

    /// <summary>Stable identity for caches and cull keys. GetHashCode is not unique.</summary>
    public int Id { get; } = System.Threading.Interlocked.Increment(ref _nextId);

    public bool Visible { get; set; } = true;
    public bool Solo { get; set; }
    public bool Inverted { get; set; }

    /// <summary>Display transparency: 1.0 = fully opaque, 0.0 = invisible.</summary>
    public double Opacity { get; set; } = 1.0;
    
    /// <summary>Average ink coverage over the whole plate, in percent. Call <see cref="ComputeStats"/> first.</summary>
    public double InkedAreaPercent { get; private set; }

    /// <summary>Bounding box of all inked pixels; <see cref="PixelBounds.Empty"/> for a blank plate.</summary>
    public PixelBounds InkBounds { get; private set; } = PixelBounds.Empty;

    /// <summary>Share of pixels carrying any ink at all, in percent (0.01 = one in ten thousand).</summary>
    public double InkedPixelPercent { get; private set; }
    
    public void ComputeStats()
    {
        long total = 0;
        long inkedPixels = 0;
        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = -1;
        var maxY = -1;
        var gate = new object();

        Parallel.For(0, Height,
            () => (sum: 0L, inked: 0L, minX: int.MaxValue, minY: int.MaxValue, maxX: -1, maxY: -1),
            (y, _, local) =>
            {
                var start = y * Width;
                for (var x = 0; x < Width; x++)
                {
                    var value = Coverage[start + x];
                    local.sum += value;
                    if (value == 0)
                        continue;

                    local.inked++;
                    if (x < local.minX) local.minX = x;
                    if (x > local.maxX) local.maxX = x;
                    if (y < local.minY) local.minY = y;
                    if (y > local.maxY) local.maxY = y;
                }

                return local;
            },
            local =>
            {
                lock (gate)
                {
                    total += local.sum;
                    inkedPixels += local.inked;

                    if (local.maxX < 0)
                        return;

                    if (local.minX < minX) minX = local.minX;
                    if (local.maxX > maxX) maxX = local.maxX;
                    if (local.minY < minY) minY = local.minY;
                    if (local.maxY > maxY) maxY = local.maxY;
                }
            });

        InkedAreaPercent = total * 100.0 / (255.0 * (long)Width * Height);
        InkedPixelPercent = inkedPixels * 100.0 / ((long)Width * Height);
        InkBounds = maxX < 0 ? PixelBounds.Empty : new PixelBounds(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>
    /// Mean plate coverage (percent) in a square aperture centred on pixel (cx, cy).
    /// Pixels outside the plate are ignored; returns 0 if the aperture is entirely outside.
    /// </summary>
    public double SamplePercent(int cx, int cy, int aperture)
    {
        if (aperture < 1) aperture = 1;
        var half = aperture / 2;
        long sum = 0;
        var count = 0;
        
        for (var y = cy - half; y <= cy + half; y++)
        {
            if (y < 0 || y >= Height) continue;
            var row = y * Width;
            for (var x = cx - half; x <= cx + half; x++)
            {
                if (x < 0 || x >= Width) continue;
                sum += Coverage[row + x];
                count++;
            }
        }
        
        return count == 0 ? 0 : sum * 100.0 / (255.0 * count);
    }
}
