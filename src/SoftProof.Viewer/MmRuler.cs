using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoftProof.Viewer;

/// <summary>
/// A millimetre ruler that follows the viewer's zoom and pan. The host sets
/// <see cref="PixelsPerMm"/> (screen pixels per mm) and <see cref="Origin"/> (screen position of 0 mm).
/// </summary>
public class MmRuler : FrameworkElement
{
    private static readonly double[] Steps =
        { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000 };
    
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(MmRuler),
        new FrameworkPropertyMetadata(Orientation.Horizontal, FrameworkPropertyMetadataOptions.AffectsRender));
    
    public static readonly DependencyProperty PixelsPerMmProperty = DependencyProperty.Register(
        nameof(PixelsPerMm), typeof(double), typeof(MmRuler),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    
    public static readonly DependencyProperty OriginProperty = DependencyProperty.Register(
        nameof(Origin), typeof(double), typeof(MmRuler),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(MmRuler),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(MmRuler),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    
    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }
    
    /// <summary>Screen pixels per millimetre.</summary>
    public double PixelsPerMm
    {
        get => (double)GetValue(PixelsPerMmProperty);
        set => SetValue(PixelsPerMmProperty, value);
    }
    
    /// <summary>Position (in this control's coordinates, along its axis) of 0 mm.</summary>
    public double Origin
    {
        get => (double)GetValue(OriginProperty);
        set => SetValue(OriginProperty, value);
    }
    
    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }
    
    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }
    
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        
        var horizontal = Orientation == Orientation.Horizontal;
        var length = horizontal ? ActualWidth : ActualHeight;
        var thickness = horizontal ? ActualHeight : ActualWidth;
        
        if (Background != null)
            dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        
        var ppm = PixelsPerMm;
        if (ppm <= 0 || length <= 0) return;
        
        // Smallest "nice" step that leaves at least ~60 px between labels.
        var step = Steps[^1];
        foreach (var s in Steps)
        {
            if (s * ppm >= 60) { step = s; break; }
        }
        
        var subdivisions = step * ppm / 5 >= 5 ? 5 : 1;
        var minor = step / subdivisions;
        
        var startMm = -Origin / ppm;
        var endMm = (length - Origin) / ppm;
        var pen = new Pen(Foreground, 1);
        var typeface = new Typeface("Consolas");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        
        for (var k = (long)Math.Floor(startMm / minor); k * minor <= endMm + minor; k++)
        {
            var mm = k * minor;
            var pos = Origin + mm * ppm;
            if (pos < -1 || pos > length + 1) continue;
            
            var major = k % subdivisions == 0;
            var tick = major ? thickness * 0.55 : thickness * 0.28;
            
            if (horizontal)
                dc.DrawLine(pen, new Point(pos, thickness), new Point(pos, thickness - tick));
            else
                dc.DrawLine(pen, new Point(thickness, pos), new Point(thickness - tick, pos));
            
            if (!major) continue;
            
            var label = new FormattedText(mm.ToString("0"),
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, 10, Foreground, dpi);
            
            if (horizontal)
            {
                dc.DrawText(label, new Point(pos + 3, 1));
            }
            else
            {
                dc.PushTransform(new TranslateTransform(2, pos - 3));
                dc.PushTransform(new RotateTransform(-90));
                dc.DrawText(label, new Point(0, 0));
                dc.Pop();
                dc.Pop();
            }
        }
    }
}
