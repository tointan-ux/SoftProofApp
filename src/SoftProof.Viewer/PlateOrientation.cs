using System.Windows;
using System.Windows.Media;

namespace SoftProof.Viewer;

/// <summary>
/// Display orientation: rotation in 90° steps plus axis flips, expressed as an image-space
/// transform. Kept separate from the zoom/pan matrix so zoom stays a pure scale and level
/// selection keeps working.
/// </summary>
public sealed class PlateOrientation
{
    public int Rotation { get; private set; }          // 0, 90, 180, 270 clockwise
    public bool FlipHorizontal { get; private set; }
    public bool FlipVertical { get; private set; }

    public bool IsIdentity => Rotation == 0 && !FlipHorizontal && !FlipVertical;

    public void RotateClockwise() => Rotation = (Rotation + 90) % 360;

    /// <summary>Snap to one of the four right angles (a rotation field, not a free slider).</summary>
    public void SetRotation(int degrees)
    {
        var normalized = ((degrees % 360) + 360) % 360;
        Rotation = normalized is 0 or 90 or 180 or 270 ? normalized : 0;
    }

    public void ToggleFlipHorizontal() => FlipHorizontal = !FlipHorizontal;

    public void ToggleFlipVertical() => FlipVertical = !FlipVertical;

    public void Reset()
    {
        Rotation = 0;
        FlipHorizontal = false;
        FlipVertical = false;
    }

    /// <summary>Size of the plate after rotation (flips do not change it).</summary>
    public Size OrientedSize(double width, double height) =>
        Rotation is 90 or 270 ? new Size(height, width) : new Size(width, height);

    /// <summary>Transform mapping image pixels to oriented pixels.</summary>
    public Matrix GetMatrix(double width, double height)
    {
        Matrix matrix = Rotation switch
        {
            // (x,y) -> (H - y, x)
            90 => new Matrix(0, 1, -1, 0, height, 0),
            // (x,y) -> (W - x, H - y)
            180 => new Matrix(-1, 0, 0, -1, width, height),
            // (x,y) -> (y, W - x)
            270 => new Matrix(0, -1, 1, 0, 0, width),
            _ => Matrix.Identity
        };

        var oriented = OrientedSize(width, height);

        if (FlipHorizontal)
        {
            var flip = new Matrix(-1, 0, 0, 1, oriented.Width, 0);
            matrix.Append(flip);
        }

        if (FlipVertical)
        {
            var flip = new Matrix(1, 0, 0, -1, 0, oriented.Height);
            matrix.Append(flip);
        }

        return matrix;
    }

    public Matrix InverseMatrix(double width, double height)
    {
        var matrix = GetMatrix(width, height);
        matrix.Invert();
        return matrix;
    }

    public string Label()
    {
        var rotation = Rotation == 0 ? "0°" : $"{Rotation}°";
        var flips = (FlipHorizontal ? " flipped H" : string.Empty) + (FlipVertical ? " flipped V" : string.Empty);
        return rotation + flips;
    }
}
