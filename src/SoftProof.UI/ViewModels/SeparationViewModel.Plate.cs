using System.Windows.Media;
using System.Windows.Media.Imaging;
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
            _baseR = layer.R;
            _baseG = layer.G;
            _baseB = layer.B;
            DisplayBrush = FrozenBrush(_baseR, _baseG, _baseB);
            Thumbnail = CreateThumbnail(layer);
        }
        else
        {
            var c = DisplayColor;
            _baseR = c.R;
            _baseG = c.G;
            _baseB = c.B;
            DisplayBrush = FrozenBrush(_baseR, _baseG, _baseB);
        }
    }
    
    /// <summary>Raster data for this plate, or null when the job has no pixel data.</summary>
    public PlateLayer? Layer { get; }
    
    public bool HasLayer => Layer != null;
    public bool IsTechnical => Separation.IsTechnical;
    
    /// <summary>Set by the owning view model; called when visible / solo / invert changed.</summary>
    internal Action? FlagsChanged { get; set; }
    
    /// <summary>Small preview of the plate's own artwork, shown in the plate list.</summary>
    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    private byte _baseR;
    private byte _baseG;
    private byte _baseB;

    /// <summary>Ink colour override chosen by the user, or null for the RIP/default colour.</summary>
    public System.Windows.Media.Color? CustomColor { get; private set; }

    /// <summary>Apply a custom display colour to this separation (including spot inks).</summary>
    public void SetCustomColor(System.Windows.Media.Color color)
    {
        CustomColor = color;

        if (Layer != null)
        {
            Layer.SetDisplayColor(color.R, color.G, color.B);
            Thumbnail = CreateThumbnail(Layer);
        }

        DisplayBrush = FrozenBrush(color.R, color.G, color.B);
        FlagsChanged?.Invoke();
    }

    /// <summary>Return to the colour that came from the job/RIP.</summary>
    public void ResetColor()
    {
        CustomColor = null;

        if (Layer != null)
        {
            Layer.SetDisplayColor(_baseR, _baseG, _baseB);
            Thumbnail = CreateThumbnail(Layer);
        }

        DisplayBrush = FrozenBrush(_baseR, _baseG, _baseB);
        FlagsChanged?.Invoke();
    }

    private static ImageSource CreateThumbnail(PlateLayer layer)
    {
        const int size = 48;

        // Prefer the coarsest pyramid level: same picture, a fraction of the pixels to scan.
        var coarse = layer.Pyramid?.Levels[^1];
        var pixels = coarse is null
            ? PlateThumbnail.Build(layer, size)
            : PlateThumbnail.Build(coarse.Coverage, coarse.Width, coarse.Height, size,
                                   layer.R, layer.G, layer.B);

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null,
                                         pixels, size * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private Brush _displayBrush = Brushes.Gray;
    public Brush DisplayBrush
    {
        get => _displayBrush;
        private set
        {
            _displayBrush = value;
            OnPropertyChanged();
        }
    }
    
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
    public PreflightItem(string title, string detail, PreflightStatus status,
                         FocusRegion? focus = null)
    {
        Title = title;
        Detail = detail;
        Status = status;
        Focus = focus;
    }

    public string Title { get; }
    public string Detail { get; }
    public PreflightStatus Status { get; }

    /// <summary>Where to jump when the row is clicked, when the check could locate the issue.</summary>
    public FocusRegion? Focus { get; }
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
