using System;
using System.Windows.Media;
using FluentAssertions;
using SoftProof.Viewer;
using Xunit;

namespace SoftProof.Tests.Rendering;

/// <summary>
/// The camera/matrix mapping is what the whole viewer hangs on: if it drifts, 100 % stops
/// being 1:1 and the compare modes disagree with the canvas. These tests pin the conversion
/// the step-2 refactor introduced, including equivalence with the old matrix-only maths.
/// </summary>
public class PlateViewerCameraTests
{
    private const double ViewportWidth = 1_600;
    private const double ViewportHeight = 1_000;

    private static ViewTransform Camera(double scale, double cx, double cy) =>
        new(scale, cx, cy, ViewportWidth, ViewportHeight);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public void Camera_and_matrix_round_trip(int nativeFactor)
    {
        var camera = Camera(0.37, 4_321.5, 2_987.25);

        var matrix = PlateViewer.MatrixFromCamera(camera, nativeFactor);
        var back = PlateViewer.CameraFromMatrix(matrix, nativeFactor, ViewportWidth, ViewportHeight);

        back.Should().NotBeNull();
        back!.Value.NativeScale.Should().BeApproximately(camera.NativeScale, 1e-9);
        back.Value.CentreX.Should().BeApproximately(camera.CentreX, 1e-6);
        back.Value.CentreY.Should().BeApproximately(camera.CentreY, 1e-6);
    }

    [Fact]
    public void At_one_hundred_percent_a_native_pixel_is_one_dip()
    {
        const int factor = 8;
        var camera = Camera(1.0, 5_000, 4_000);

        var matrix = PlateViewer.MatrixFromCamera(camera, factor);

        var a = matrix.Transform(new System.Windows.Point(1_000, 1_000));
        var b = matrix.Transform(new System.Windows.Point(1_001, 1_001));

        // One base pixel is 8 native pixels = 8 DIP at 100 %.
        (b.X - a.X).Should().BeApproximately(factor, 1e-9);
        (b.Y - a.Y).Should().BeApproximately(factor, 1e-9);
    }

    [Fact]
    public void The_camera_centre_lands_in_the_middle_of_the_viewport()
    {
        var camera = Camera(0.5, 3_000, 2_000);

        var centre = PlateViewer.MatrixFromCamera(camera, 4)
            .Transform(new System.Windows.Point(camera.CentreX / 4.0, camera.CentreY / 4.0));

        centre.X.Should().BeApproximately(ViewportWidth / 2, 1e-9);
        centre.Y.Should().BeApproximately(ViewportHeight / 2, 1e-9);
    }

    [Fact]
    public void Zooming_the_matrix_is_the_same_as_zooming_the_camera()
    {
        // The refactor replaced Matrix.ScaleAt with ViewTransform.ZoomedAt; this asserts the
        // two paths agree, which is the whole "no visual change" claim.
        const int factor = 4;
        const double anchorX = 1_210;
        const double anchorY = 260;

        var camera = Camera(0.25, 4_000, 3_000);

        var matrix = PlateViewer.MatrixFromCamera(camera, factor);
        matrix.ScaleAt(1.6, 1.6, anchorX, anchorY);
        var viaMatrix = PlateViewer.CameraFromMatrix(matrix, factor, ViewportWidth, ViewportHeight)!.Value;

        var viaCamera = camera.ZoomedAt(1.6, anchorX, anchorY);

        viaMatrix.NativeScale.Should().BeApproximately(viaCamera.NativeScale, 1e-9);
        viaMatrix.CentreX.Should().BeApproximately(viaCamera.CentreX, 1e-6);
        viaMatrix.CentreY.Should().BeApproximately(viaCamera.CentreY, 1e-6);
    }

    [Fact]
    public void Panning_the_matrix_is_the_same_as_panning_the_camera()
    {
        const int factor = 2;
        var camera = Camera(0.6, 2_500, 1_800);

        var matrix = PlateViewer.MatrixFromCamera(camera, factor);
        matrix.Translate(-120, 45);
        var viaMatrix = PlateViewer.CameraFromMatrix(matrix, factor, ViewportWidth, ViewportHeight)!.Value;

        // Content dragged left by 120 DIP: the camera centre moves right in plate pixels.
        var viaCamera = camera.PannedBy(-120, 45);

        viaMatrix.CentreX.Should().BeApproximately(viaCamera.CentreX, 1e-6);
        viaMatrix.CentreY.Should().BeApproximately(viaCamera.CentreY, 1e-6);
    }

    [Fact]
    public void A_matrix_without_scale_is_rejected_instead_of_producing_infinity()
    {
        PlateViewer.CameraFromMatrix(Matrix.Identity, 1, 0, 1_000).Should().BeNull();
        PlateViewer.CameraFromMatrix(new Matrix(0, 0, 0, 0, 0, 0), 1, 1_600, 1_000).Should().BeNull();
    }
}
