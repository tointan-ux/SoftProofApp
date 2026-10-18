using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SoftProof.Core.Models;

namespace SoftProof.UI.Views;

/// <summary>
/// Edits the per-press preflight thresholds. Kept deliberately plain: these are numbers a
/// prepress operator types once per press, not something to hide behind a wizard.
/// </summary>
public partial class PreferencesWindow : Window
{
    private readonly PreflightSettings _settings;

    public PreferencesWindow(PreflightSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        PressNameBox.Text = settings.PressName;
        SlugBox.Text = Format(settings.SlugBandMm);
        TrapGapBox.Text = Format(settings.TrapGapMm);
        TrapMinBox.Text = Format(settings.TrapGapMinPixels);
        NearSolidBox.Text = Format(settings.NearSolidPercent);
        TinyPixelsBox.Text = Format(settings.TinyInkedPixelPercent);
        TinyCoverageBox.Text = Format(settings.TinyCoveragePercent);
        MeaningfulBox.Text = Format(settings.MeaningfulCoveragePercent);
        MissingBox.Text = Format(settings.MissingSeparationPercent);
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryRead(SlugBox, 0, 25, "Slug band (0–25 mm)", out var slug) ||
            !TryRead(TrapGapBox, 0.01, 2, "Trap gap (0.01–2 mm)", out var trapGap) ||
            !TryRead(TrapMinBox, 1, 1_000_000, "Trap gap minimum (≥ 1 px)", out var trapMin) ||
            !TryRead(NearSolidBox, 90, 100, "Near-solid threshold (90–100%)", out var nearSolid) ||
            !TryRead(TinyPixelsBox, 0, 100, "Tiny inked pixels (0–100%)", out var tinyPixels) ||
            !TryRead(TinyCoverageBox, 0, 100, "Tiny coverage (0–100%)", out var tinyCoverage) ||
            !TryRead(MeaningfulBox, 0, 100, "Meaningful coverage (0–100%)", out var meaningful) ||
            !TryRead(MissingBox, 0, 100, "Missing separation (0–100%)", out var missing))
        {
            return;
        }

        if (missing > meaningful)
        {
            MessageBox.Show(this,
                "Missing-separation threshold cannot exceed the meaningful-coverage threshold.",
                "Preferences", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.PressName = PressNameBox.Text.Trim();
        _settings.SlugBandMm = slug;
        _settings.TrapGapMm = trapGap;
        _settings.TrapGapMinPixels = trapMin;
        _settings.NearSolidPercent = nearSolid;
        _settings.TinyInkedPixelPercent = tinyPixels;
        _settings.TinyCoveragePercent = tinyCoverage;
        _settings.MeaningfulCoveragePercent = meaningful;
        _settings.MissingSeparationPercent = missing;

        DialogResult = true;
    }

    private bool TryRead(TextBox box, double min, double max, string label, out double value)
    {
        if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
            value >= min && value <= max)
        {
            return true;
        }

        MessageBox.Show(this, $"{label} is not valid.", "Preferences",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
        box.Focus();
        box.SelectAll();
        return false;
    }
}
