using System;

namespace SoftProof.Viewer;

/// <summary>
/// Immutable view transform for a plate viewport, expressed in <b>native plate pixels</b>.
/// <para>
/// One unit of <see cref="CentreX"/>/<see cref="CentreY"/> is one pixel of the plate at its
/// native resolution (2400 dpi for a 2400 dpi job), and <see cref="NativeScale"/> is the
/// magnification: <b>DIPs per native pixel</b>. 100 % zoom is therefore
/// <c>NativeScale == 1.0</c> — exactly the existing <c>PlateViewer.NativeZoom</c> — regardless
/// of the plate's base pyramid level, the viewer, or the compare mode.
/// </para>
/// <para>
/// The viewer, the side-by-side panes and the single-canvas compare overlay are meant to share
/// this type; they only convert to their own bitmap spaces at the edges. It is deliberately
/// free of WPF types so it can be unit tested.
/// </para>
/// </summary>
public readonly struct ViewTransform : IEquatable<ViewTransform>
{
    /// <summary>Zoom floor, ~2 % of native: the whole plate is always reachable.</summary>
    public const double MinNativeScale = 0.02;

    /// <summary>Zoom ceiling: 100 % native. Proofing never magnifies past 1:1.</summary>
    public const double MaxNativeScale = 1.0;

    /// <summary>Fraction of the viewport the plate occupies when fitted.</summary>
    public const double FitFill = 0.92;

    public ViewTransform(double nativeScale, double centreX, double centreY,
                         double viewportWidth, double viewportHeight)
    {
        if (viewportWidth <= 0) throw new ArgumentOutOfRangeException(nameof(viewportWidth));
        if (viewportHeight <= 0) throw new ArgumentOutOfRangeException(nameof(viewportHeight));

        // Deliberately not clamped: this type also describes the current view for readout,
        // and rounding that display to the zoom limits would misreport the real scale.
        NativeScale = double.IsNaN(nativeScale) ? MinNativeScale : nativeScale;
        CentreX = centreX;
        CentreY = centreY;
        ViewportWidth = viewportWidth;
        ViewportHeight = viewportHeight;
    }

    /// <summary>DIPs per native plate pixel (1.0 = 100 %).</summary>
    public double NativeScale { get; }

    /// <summary>Centre of the viewport in native plate pixels.</summary>
    public double CentreX { get; }

    public double CentreY { get; }

    public double ViewportWidth { get; }

    public double ViewportHeight { get; }

    /// <summary>Zoom percentage as the UI shows it.</summary>
    public double Percent => NativeScale * 100.0;

    /// <summary>The zoom readout every view shares, e.g. <c>100%</c>.</summary>
    public string ZoomText => $"{Percent:0}%";

    public static double ClampScale(double scale) =>
        double.IsNaN(scale) ? MinNativeScale : Math.Clamp(scale, MinNativeScale, MaxNativeScale);

    /// <summary>Fit the whole plate inside the viewport, centred, with a small breathing margin.</summary>
    public static ViewTransform Fit(double plateWidth, double plateHeight,
                                    double viewportWidth, double viewportHeight,
                                    double fill = FitFill)
    {
        if (plateWidth <= 0) throw new ArgumentOutOfRangeException(nameof(plateWidth));
        if (plateHeight <= 0) throw new ArgumentOutOfRangeException(nameof(plateHeight));

        var scale = ClampScale(Math.Min(viewportWidth / plateWidth, viewportHeight / plateHeight) * fill);
        return new ViewTransform(scale, plateWidth / 2.0, plateHeight / 2.0, viewportWidth, viewportHeight);
    }

    /// <summary>Viewport position of a native plate pixel.</summary>
    public (double X, double Y) NativeToDip(double nativeX, double nativeY) =>
        (ViewportWidth / 2.0 + (nativeX - CentreX) * NativeScale,
         ViewportHeight / 2.0 + (nativeY - CentreY) * NativeScale);

    /// <summary>Native plate pixel under a viewport position.</summary>
    public (double X, double Y) DipToNative(double dipX, double dipY) =>
        (CentreX + (dipX - ViewportWidth / 2.0) / NativeScale,
         CentreY + (dipY - ViewportHeight / 2.0) / NativeScale);

    /// <summary>Zoom by <paramref name="factor"/> keeping the native point under the anchor fixed.</summary>
    public ViewTransform ZoomedAt(double factor, double anchorDipX, double anchorDipY)
    {
        var (anchorX, anchorY) = DipToNative(anchorDipX, anchorDipY);
        var scale = ClampScale(NativeScale * factor);

        return new ViewTransform(
            scale,
            anchorX - (anchorDipX - ViewportWidth / 2.0) / scale,
            anchorY - (anchorDipY - ViewportHeight / 2.0) / scale,
            ViewportWidth,
            ViewportHeight);
    }

    /// <summary>Zoom about the viewport centre.</summary>
    public ViewTransform ZoomedBy(double factor) =>
        ZoomedAt(factor, ViewportWidth / 2.0, ViewportHeight / 2.0);

    /// <summary>Pan by a viewport delta in DIP (drag distance is positive to the right/down).</summary>
    public ViewTransform PannedBy(double deltaDipX, double deltaDipY) =>
        new(NativeScale, CentreX - deltaDipX / NativeScale, CentreY - deltaDipY / NativeScale,
            ViewportWidth, ViewportHeight);

    /// <summary>Centre the viewport on a native plate pixel.</summary>
    public ViewTransform CentredOn(double nativeX, double nativeY) =>
        new(NativeScale, nativeX, nativeY, ViewportWidth, ViewportHeight);

    /// <summary>Keep the zoom and centre while the control resizes.</summary>
    public ViewTransform Resized(double viewportWidth, double viewportHeight) =>
        new(NativeScale, CentreX, CentreY, viewportWidth, viewportHeight);

    /// <summary>Same centre, new zoom (clamped to the zoom limits).</summary>
    public ViewTransform WithNativeScale(double nativeScale) =>
        new(ClampScale(nativeScale), CentreX, CentreY, ViewportWidth, ViewportHeight);

    /// <summary>Region of the plate currently visible, in native plate pixels.</summary>
    public (double X, double Y, double Width, double Height) VisiblePlateRect(double plateWidth, double plateHeight)
    {
        var (x0, y0) = DipToNative(0, 0);
        var (x1, y1) = DipToNative(ViewportWidth, ViewportHeight);

        x0 = Math.Clamp(x0, 0, plateWidth);
        y0 = Math.Clamp(y0, 0, plateHeight);
        x1 = Math.Clamp(x1, 0, plateWidth);
        y1 = Math.Clamp(y1, 0, plateHeight);

        return (x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    /// <summary>Clamp the centre so the plate cannot be dragged completely out of view.</summary>
    public ViewTransform ClampedTo(double plateWidth, double plateHeight)
    {
        var halfW = ViewportWidth / 2.0 / NativeScale;
        var halfH = ViewportHeight / 2.0 / NativeScale;

        var cx = plateWidth <= halfW * 2 ? plateWidth / 2.0 : Math.Clamp(CentreX, halfW, plateWidth - halfW);
        var cy = plateHeight <= halfH * 2 ? plateHeight / 2.0 : Math.Clamp(CentreY, halfH, plateHeight - halfH);

        return new ViewTransform(NativeScale, cx, cy, ViewportWidth, ViewportHeight);
    }

    public bool Equals(ViewTransform other) =>
        NativeScale.Equals(other.NativeScale) && CentreX.Equals(other.CentreX) &&
        CentreY.Equals(other.CentreY) && ViewportWidth.Equals(other.ViewportWidth) &&
        ViewportHeight.Equals(other.ViewportHeight);

    public override bool Equals(object? obj) => obj is ViewTransform other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(NativeScale, CentreX, CentreY, ViewportWidth, ViewportHeight);

    public static bool operator ==(ViewTransform left, ViewTransform right) => left.Equals(right);

    public static bool operator !=(ViewTransform left, ViewTransform right) => !left.Equals(right);

    public override string ToString() =>
        $"{Percent:0.#}% @ ({CentreX:0.#},{CentreY:0.#}) in {ViewportWidth:0.#}x{ViewportHeight:0.#}";
}
