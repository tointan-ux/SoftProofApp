using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SoftProof.UI.Views;

/// <summary>
/// Full-screen eyedropper: a transparent overlay that samples the desktop pixel under the
/// cursor. Click picks, Esc (or right-click) cancels.
/// </summary>
public sealed class ScreenEyedropperWindow : Window
{
    private readonly Canvas _canvas = new();
    private readonly Border _swatch;
    private readonly TextBlock _readout;
    private readonly Border _tip;

    public Color? PickedColor { get; private set; }

    public ScreenEyedropperWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        // Fully transparent but still hit-testable: sampling must see the untouched desktop.
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Cursor = Cursors.Cross;
        Focusable = true;

        _swatch = new Border
        {
            Width = 48,
            Height = 48,
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };

        _readout = new TextBlock
        {
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var tipContent = new StackPanel();
        tipContent.Children.Add(_swatch);
        tipContent.Children.Add(_readout);

        _tip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 15, 21, 32)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Child = tipContent,
            IsHitTestVisible = false
        };

        _canvas.Children.Add(_tip);

        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 15, 21, 32)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 0, 0),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "Click to pick a colour  ·  Esc to cancel",
                Foreground = Brushes.White,
                FontSize = 12
            }
        };

        var root = new Grid();
        root.Children.Add(_canvas);
        root.Children.Add(hint);
        Content = root;

        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            Keyboard.Focus(this);
        };
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var p = e.GetPosition(this);
        var screen = PointToScreen(p);
        var color = SampleScreen(screen);

        _swatch.Background = new SolidColorBrush(color);
        _readout.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        var maxX = Math.Max(8, ActualWidth - _tip.ActualWidth - 12);
        var maxY = Math.Max(8, ActualHeight - _tip.ActualHeight - 12);
        Canvas.SetLeft(_tip, Math.Clamp(p.X + 18, 8, maxX));
        Canvas.SetTop(_tip, Math.Clamp(p.Y + 18, 8, maxY));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        PickedColor = SampleScreen(PointToScreen(e.GetPosition(this)));
        DialogResult = true;
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        DialogResult = false;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- screen sampling

    private const uint ClrInvalid = 0xFFFFFFFF;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    private static Color SampleScreen(Point screenPoint)
    {
        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
            return Colors.Black;

        try
        {
            var value = GetPixel(hdc, (int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y));
            if (value == ClrInvalid)
                return Colors.Black;

            return Color.FromRgb(
                (byte)(value & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)((value >> 16) & 0xFF));
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }
}
