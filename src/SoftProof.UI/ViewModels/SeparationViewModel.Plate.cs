using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SoftProof.Core.Analysis;
using SoftProof.Core.Models;

namespace SoftProof.UI.ViewModels;

/// <summary>
/// Plate-panel state for a separation: raster layer (if any), solo / invert toggles, screening info.
/// </summary>
public partial class SeparationViewModel
{
    public SeparationViewModel(Separation separation, PlateLayer? layer) : this(separation)
    {
        Layer = layer;
        if (layer != null)
        {
            InkedAreaPercent = layer.InkedAreaPercent;
            DisplayBrush = FrozenBrush(layer.R, layer.G, layer.B);
        }
        else
        {
            var c = DisplayColor;
            DisplayBrush = FrozenBrush(c.R, c.G, c.B);
        }
    }
    
    /// <summary>Raster data for this plate, or null when the job has no pixel data.</summary>
    public PlateLayer? Layer { get; }
    
    public bool HasLayer => Layer != null;
    public bool IsTechnical => Separation.IsTechnical;
    
    /// <summary>Set by the owning view model; called when visible / solo / invert changed.</summary>
    internal Action? FlagsChanged { get; set; }
    
    public Brush DisplayBrush { get; }
    
    public double InkedAreaPercent { get; }
    public string InkedAreaText => $"{InkedAreaPercent:0.0}%";
    
    public string InfoLine => Separation.ScreenLpi > 0
        ? $"{Separation.ScreenAngle:0}° · {Separation.ScreenLpi:0} lpi · {Separation.DotShape}"
        : "Solid / line art";
    
    [ObservableProperty]
    private bool _isSolo;
    
    [ObservableProperty]
    private bool _isInverted;
    
    partial void OnIsVisibleChanged(bool value)
    {
        if (Layer != null) Layer.Visible = value;
        FlagsChanged?.Invoke();
    }
    
    partial void OnIsSoloChanged(bool value)
    {
        if (Layer != null) Layer.Solo = value;
        FlagsChanged?.Invoke();
    }
    
    partial void OnIsInvertedChanged(bool value)
    {
        if (Layer != null) Layer.Inverted = value;
        FlagsChanged?.Invoke();
    }
    
    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>One tile of the densitometer / dot meter panel.</summary>
public partial class DensityReading : ObservableObject
{
    public DensityReading(string name, Brush brush)
    {
        Name = name;
        DisplayBrush = brush;
    }
    
    public string Name { get; }
    public Brush DisplayBrush { get; }
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    private double _percent;
    
    public string PercentText => $"{Percent:0.0}%";
}

/// <summary>One row in the preflight list.</summary>
public sealed class PreflightItem
{
    public PreflightItem(string title, string detail, PreflightStatus status)
    {
        Title = title;
        Detail = detail;
        Status = status;
    }
    
    public string Title { get; }
    public string Detail { get; }
    public PreflightStatus Status { get; }
}

public sealed class PlateSetEventArgs : EventArgs
{
    public PlateSetEventArgs(IReadOnlyList<PlateLayer> plates, int width, int height, double pixelsPerMm)
    {
        Plates = plates;
        Width = width;
        Height = height;
        PixelsPerMm = pixelsPerMm;
    }
    
    public IReadOnlyList<PlateLayer> Plates { get; }
    public int Width { get; }
    public int Height { get; }
    public double PixelsPerMm { get; }
}
