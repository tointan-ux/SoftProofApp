using System.Windows;
using SoftProof.Viewer;
using Xunit;

namespace SoftProof.Tests.Rendering;

/// <summary>
/// Canvas rotation is a display transform only: plate pixels keep their identity, the zoom stays
/// a pure scale, and a quarter turn swaps the sheet's width and height.
/// </summary>
public class ViewRotationTests
{
    [Fact]
    public void Default_IsIdentity()
    {
        var rotation = new ViewRotation();

        Assert.True(rotation.IsIdentity);
        Assert.Equal(0, rotation.Degrees);
        Assert.Equal(new Size(100, 50), rotation.OrientedSize(100, 50));
        Assert.True(rotation.GetMatrix(100, 50).IsIdentity);
    }

    [Fact]
    public void Rotate90_MapsCornersAndSwapsSize()
    {
        var rotation = new ViewRotation();
        rotation.RotateClockwise();

        Assert.Equal(90, rotation.Degrees);
        Assert.Equal(new Size(50, 100), rotation.OrientedSize(100, 50));

        var m = rotation.GetMatrix(100, 50);
        // (x,y) -> (50 - y, x): clockwise, so the left edge becomes the top edge.
        Assert.Equal(new Point(50, 0), m.Transform(new Point(0, 0)));
        Assert.Equal(new Point(0, 0), m.Transform(new Point(0, 50)));
        Assert.Equal(new Point(50, 100), m.Transform(new Point(100, 0)));
        Assert.Equal(new Point(0, 100), m.Transform(new Point(100, 50)));
    }

    [Fact]
    public void Rotate180_MapsCornersToOppositeCorners()
    {
        var rotation = new ViewRotation();
        rotation.Set(180);

        var m = rotation.GetMatrix(100, 50);
        Assert.Equal(new Point(100, 50), m.Transform(new Point(0, 0)));
        Assert.Equal(new Point(0, 0), m.Transform(new Point(100, 50)));
        Assert.Equal(new Size(100, 50), rotation.OrientedSize(100, 50));
    }

    [Fact]
    public void Set_SnapsToQuarterTurns()
    {
        var rotation = new ViewRotation();

        rotation.Set(450);
        Assert.Equal(90, rotation.Degrees);   // 450 wraps to 90

        rotation.Set(-90);
        Assert.Equal(270, rotation.Degrees);

        rotation.Set(37);
        Assert.Equal(0, rotation.Degrees);    // not a right angle: reset rather than tilt
    }

    [Fact]
    public void InverseMatrix_RoundTripsEveryCorner()
    {
        for (var degrees = 0; degrees < 360; degrees += 90)
        {
            var rotation = new ViewRotation();
            rotation.Set(degrees);

            var forward = rotation.GetMatrix(120, 80);
            var inverse = rotation.InverseMatrix(120, 80);

            foreach (var corner in new[]
                     {
                         new Point(0, 0), new Point(120, 0),
                         new Point(0, 80), new Point(120, 80)
                     })
            {
                var back = inverse.Transform(forward.Transform(corner));
                Assert.Equal(corner.X, back.X, precision: 6);
                Assert.Equal(corner.Y, back.Y, precision: 6);
            }
        }
    }

    [Fact]
    public void Reset_ReturnsToZero()
    {
        var rotation = new ViewRotation();
        rotation.Set(270);

        rotation.Reset();

        Assert.True(rotation.IsIdentity);
        Assert.Equal("0°", rotation.Label());
    }
}
