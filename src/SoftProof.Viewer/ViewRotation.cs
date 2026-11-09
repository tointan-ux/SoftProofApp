using System.Windows;
using System.Windows.Media;

namespace SoftProof.Viewer;

/// <summary>
/// Right-angle rotation of the whole canvas: 0°, 90°, 180° or 270° clockwise.
/// <para>
/// This is a view transform, applied on top of the zoom/pan matrix, and it is deliberately
/// separate from per-plate mirroring (<see cref="PlateFlip"/>): rotating turns the whole sheet,
/// while a flip mirrors one ink inside the sheet.
/// </para>
/// </summary>
public sealed class ViewRotation
{
    /// <summary>Clockwise rotation in degrees; always 0, 90, 180 or 270.</summary>
    public int Degrees { get; private set; }

    /// <summary>True when the canvas is not rotated.</summary>
    public bool IsIdentity => Degrees == 0;

    /// <summary>Turns the canvas a further quarter turn clockwise.</summary>
    public void RotateClockwise() => Degrees = (Degrees + 90) % 360;

    /// <summary>Snap to one of the four right angles; anything else resets to 0°.</summary>
    public void Set(int degrees)
    {
        var normalized = ((degrees % 360) + 360) % 360;
        Degrees = normalized is 0 or 90 or 180 or 270 ? normalized : 0;
    }

    /// <summary>Returns the canvas to 0°.</summary>
    public void Reset() => Degrees = 0;

    /// <summary>Size of the sheet after rotation; quarter turns swap width and height.</summary>
    public Size OrientedSize(double width, double height) =>
        Degrees is 90 or 270 ? new Size(height, width) : new Size(width, height);

    /// <summary>Transform mapping plate pixels to rotated pixels.</summary>
    public Matrix GetMatrix(double width, double height) => Degrees switch
    {
        // (x,y) -> (H - y, x)
        90 => new Matrix(0, 1, -1, 0, height, 0),
        // (x,y) -> (W - x, H - y)
        180 => new Matrix(-1, 0, 0, -1, width, height),
        // (x,y) -> (y, W - x)
        270 => new Matrix(0, -1, 1, 0, 0, width),
        _ => Matrix.Identity
    };

    /// <summary>Inverse of <see cref="GetMatrix"/>: rotated pixels back to plate pixels.</summary>
    public Matrix InverseMatrix(double width, double height)
    {
        var matrix = GetMatrix(width, height);
        matrix.Invert();
        return matrix;
    }

    /// <summary>Short label for badges and menus.</summary>
    public string Label() => $"{Degrees}°";
}
