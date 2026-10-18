using System.Windows;
using SoftProof.Viewer;
using Xunit;

namespace SoftProof.Tests.Analysis;

/// <summary>
/// Rotation/flip is a display transform only: image pixels keep their identity, zoom stays a
/// pure scale, and the inverse mapping must round-trip exactly (hit-testing depends on it).
/// </summary>
public class PlateOrientationTests
{
    [Fact]
    public void Rotate90_MapsCornersAndSwapsSize()
    {
        var orientation = new PlateOrientation();
        orientation.RotateClockwise();

        var size = orientation.OrientedSize(100, 50);
        Assert.Equal(50, size.Width);
        Assert.Equal(100, size.Height);

        var m = orientation.GetMatrix(100, 50);
        Assert.Equal(new Point(50, 0), m.Transform(new Point(0, 0)));
        Assert.Equal(new Point(50, 100), m.Transform(new Point(100, 0)));
        Assert.Equal(new Point(0, 0), m.Transform(new Point(0, 50)));
    }

    [Fact]
    public void Rotate180_MapsCornersToOppositeCorners()
    {
        var orientation = new PlateOrientation();
        orientation.RotateClockwise();
        orientation.RotateClockwise();

        var m = orientation.GetMatrix(100, 50);
        Assert.Equal(new Point(100, 50), m.Transform(new Point(0, 0)));
        Assert.Equal(new Point(0, 0), m.Transform(new Point(100, 50)));
    }

    [Fact]
    public void Flips_MirrorTheOrientedPlate()
    {
        var horizontal = new PlateOrientation();
        horizontal.ToggleFlipHorizontal();
        Assert.Equal(new Point(100, 0), horizontal.GetMatrix(100, 50).Transform(new Point(0, 0)));

        var vertical = new PlateOrientation();
        vertical.ToggleFlipVertical();
        Assert.Equal(new Point(0, 50), vertical.GetMatrix(100, 50).Transform(new Point(0, 0)));
    }

    [Fact]
    public void RotationPlusFlip_IsAppliedInOrientedSpace()
    {
        var orientation = new PlateOrientation();
        orientation.RotateClockwise();          // 100x50 -> 50x100
        orientation.ToggleFlipHorizontal();     // mirror within 50 px width

        var m = orientation.GetMatrix(100, 50);
        Assert.Equal(new Point(0, 0), m.Transform(new Point(0, 0)));      // rot90 gives (50,0), flip -> (0,0)
        Assert.Equal(new Point(50, 0), m.Transform(new Point(0, 50)));    // rot90 gives (0,0), flip -> (50,0)
    }

    [Theory]
    [InlineData(90, false, false)]
    [InlineData(180, true, false)]
    [InlineData(270, false, true)]
    [InlineData(0, true, true)]
    public void InverseMatrix_RoundTripsEveryCorner(int rotation, bool flipH, bool flipV)
    {
        var orientation = new PlateOrientation();
        for (var i = 0; i < rotation / 90; i++)
            orientation.RotateClockwise();
        if (flipH) orientation.ToggleFlipHorizontal();
        if (flipV) orientation.ToggleFlipVertical();

        var forward = orientation.GetMatrix(120, 80);
        var inverse = orientation.InverseMatrix(120, 80);

        foreach (var point in new[] { new Point(0, 0), new Point(120, 0), new Point(0, 80), new Point(120, 80), new Point(37, 19) })
        {
            var roundTrip = inverse.Transform(forward.Transform(point));
            Assert.Equal(point.X, roundTrip.X, 6);
            Assert.Equal(point.Y, roundTrip.Y, 6);
        }
    }
}
