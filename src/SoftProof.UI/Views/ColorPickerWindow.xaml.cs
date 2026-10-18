using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SoftProof.UI.Views;

public partial class ColorPickerWindow : Window
{
    private double _h;
    private double _s;
    private double _v;
    private bool _suppress;

    public Color SelectedColor { get; private set; }

    public ColorPickerWindow(Color initial)
    {
        InitializeComponent();
        SetColor(initial);

        Loaded += (_, _) =>
        {
            UpdateThumbs();
            HexBox.Focus();
            HexBox.SelectAll();
        };
        SizeChanged += (_, _) => UpdateThumbs();
    }

    private void SetColor(Color color)
    {
        RgbToHsv(color, out _h, out _s, out _v);
        RefreshUi();
    }

    private void RefreshUi(bool updateHex = true)
    {
        _suppress = true;
        try
        {
            var color = HsvToColor(_h, _s, _v);
            SvHueLayer.Background = new SolidColorBrush(HsvToColor(_h, 1, 1));
            Preview.Background = new SolidColorBrush(color);

            if (updateHex)
                HexBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

            RBox.Text = color.R.ToString(CultureInfo.InvariantCulture);
            GBox.Text = color.G.ToString(CultureInfo.InvariantCulture);
            BBox.Text = color.B.ToString(CultureInfo.InvariantCulture);

            HBox.Text = $"{_h:0}";
            SBox.Text = $"{_s * 100:0}";
            VBox.Text = $"{_v * 100:0}";
        }
        finally
        {
            _suppress = false;
        }

        UpdateThumbs();
    }

    private void UpdateThumbs()
    {
        if (SvSquare.ActualWidth <= 0 || HueStrip.ActualHeight <= 0)
            return;

        Canvas.SetLeft(SvThumb, _s * SvSquare.ActualWidth - SvThumb.Width / 2);
        Canvas.SetTop(SvThumb, (1 - _v) * SvSquare.ActualHeight - SvThumb.Height / 2);
        Canvas.SetTop(HueThumb, _h / 360 * HueStrip.ActualHeight - HueThumb.Height / 2);
    }

    // ---------------------------------------------------------------- square / hue interaction

    private void SvSquare_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        SvSquare.CaptureMouse();
        UpdateSv(e.GetPosition(SvSquare));
        e.Handled = true;
    }

    private void SvSquare_MouseMove(object sender, MouseEventArgs e)
    {
        if (SvSquare.IsMouseCaptured)
            UpdateSv(e.GetPosition(SvSquare));
    }

    private void SvSquare_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (SvSquare.IsMouseCaptured)
            SvSquare.ReleaseMouseCapture();
    }

    private void UpdateSv(Point p)
    {
        if (SvSquare.ActualWidth <= 0 || SvSquare.ActualHeight <= 0)
            return;

        _s = Math.Clamp(p.X / SvSquare.ActualWidth, 0, 1);
        _v = 1 - Math.Clamp(p.Y / SvSquare.ActualHeight, 0, 1);
        RefreshUi();
    }

    private void HueStrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        HueStrip.CaptureMouse();
        UpdateHue(e.GetPosition(HueStrip));
        e.Handled = true;
    }

    private void HueStrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (HueStrip.IsMouseCaptured)
            UpdateHue(e.GetPosition(HueStrip));
    }

    private void HueStrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (HueStrip.IsMouseCaptured)
            HueStrip.ReleaseMouseCapture();
    }

    private void UpdateHue(Point p)
    {
        if (HueStrip.ActualHeight <= 0)
            return;

        _h = Math.Clamp(p.Y / HueStrip.ActualHeight, 0, 0.9999) * 360;
        RefreshUi();
    }

    // ---------------------------------------------------------------- text fields

    private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress)
            return;

        if (!TryParseHex(HexBox.Text, out var color))
            return;

        RgbToHsv(color, out _h, out _s, out _v);
        RefreshUi(updateHex: false);
    }

    private void RgbBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress)
            return;

        if (!TryByte(RBox.Text, out var r) || !TryByte(GBox.Text, out var g) || !TryByte(BBox.Text, out var b))
            return;

        RgbToHsv(Color.FromRgb(r, g, b), out _h, out _s, out _v);
        RefreshUi();
    }

    private void HsvBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress)
            return;

        if (!double.TryParse(HBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ||
            !double.TryParse(SBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ||
            !double.TryParse(VBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return;

        _h = Math.Clamp(h, 0, 359.999);
        _s = Math.Clamp(s / 100.0, 0, 1);
        _v = Math.Clamp(v / 100.0, 0, 1);
        RefreshUi();
    }

    // ---------------------------------------------------------------- actions

    private void Eyedropper_Click(object sender, RoutedEventArgs e)
    {
        var overlay = new ScreenEyedropperWindow { Owner = this };
        if (overlay.ShowDialog() == true && overlay.PickedColor is { } picked)
            SetColor(picked);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        SelectedColor = HsvToColor(_h, _s, _v);
        DialogResult = true;
    }

    private static bool TryParseHex(string? text, out Color color)
    {
        color = Colors.Gray;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim().TrimStart('#');
        if (trimmed.Length != 6 || !int.TryParse(trimmed, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var value))
            return false;

        color = Color.FromRgb((byte)((value >> 16) & 0xFF),
                              (byte)((value >> 8) & 0xFF),
                              (byte)(value & 0xFF));
        return true;
    }

    private static bool TryByte(string? text, out byte value) =>
        byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    // ---------------------------------------------------------------- colour maths

    private static Color HsvToColor(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);

        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;

        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return Color.FromRgb(Channel(r + m), Channel(g + m), Channel(b + m));
    }

    private static void RgbToHsv(Color color, out double h, out double s, out double v)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        v = max;
        s = max <= 0 ? 0 : delta / max;

        if (delta <= 0.00001)
        {
            h = 0;
            return;
        }

        if (Math.Abs(max - r) < 0.00001)
            h = 60 * (((g - b) / delta) % 6);
        else if (Math.Abs(max - g) < 0.00001)
            h = 60 * ((b - r) / delta + 2);
        else
            h = 60 * ((r - g) / delta + 4);

        if (h < 0)
            h += 360;
    }

    private static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
}
