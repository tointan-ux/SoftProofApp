namespace SoftProof.Core.Models;

/// <summary>
/// Raster data for one plate (separation), stored as 8-bit ink coverage (0 = no ink, 255 = solid).
/// Also carries the per-plate display state (visible / solo / inverted) used by the viewer.
/// </summary>
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
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }
    
    public bool Visible { get; set; } = true;
    public bool Solo { get; set; }
    public bool Inverted { get; set; }
    
    /// <summary>Average ink coverage over the whole plate, in percent. Call <see cref="ComputeStats"/> first.</summary>
    public double InkedAreaPercent { get; private set; }
    
    public void ComputeStats()
    {
        long total = 0;
        var data = Coverage;
        var w = Width;
        Parallel.For(0, Height, () => 0L, (y, _, local) =>
        {
            var start = y * w;
            for (var x = 0; x < w; x++)
                local += data[start + x];
            return local;
        }, local => Interlocked.Add(ref total, local));
        
        InkedAreaPercent = total * 100.0 / (255.0 * Width * Height);
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
