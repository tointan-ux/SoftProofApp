using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SoftProof.Core.Models;
using SoftProof.UI.ViewModels;

namespace SoftProof.UI.Converters;

/// <summary>
/// Converts bool to Brush with configurable true/false values
/// </summary>
public class BoolToBrushConverter : IValueConverter
{
    public Brush TrueValue { get; set; } = Brushes.Green;
    public Brush FalseValue { get; set; } = Brushes.Red;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && b ? TrueValue : FalseValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts null to Collapsed, non-null to Visible
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value != null ? Visibility.Visible : Visibility.Collapsed;
    }
    
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts bool to custom text
/// </summary>
public class BoolToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && parameter is string text)
        {
            return b ? text : string.Empty;
        }
        return string.Empty;
    }
    
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts quality score (0-100) to color brush
/// </summary>
public class ScoreToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is float score)
        {
            if (score >= 80) return new SolidColorBrush(Color.FromRgb(76, 175, 80));   // Green
            if (score >= 60) return new SolidColorBrush(Color.FromRgb(255, 152, 0));   // Orange
            return new SolidColorBrush(Color.FromRgb(244, 67, 54));                     // Red
        }
        return new SolidColorBrush(Color.FromRgb(176, 176, 176));
    }
    
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts JobStatus to color brush
/// </summary>
public class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is JobStatus status)
        {
            return status switch
            {
                JobStatus.Pending => new SolidColorBrush(Color.FromRgb(176, 176, 176)),     // Gray
                JobStatus.Loading => new SolidColorBrush(Color.FromRgb(33, 150, 243)),      // Blue
                JobStatus.Ready => new SolidColorBrush(Color.FromRgb(76, 175, 80)),         // Green
                JobStatus.Proofing => new SolidColorBrush(Color.FromRgb(255, 152, 0)),      // Orange
                JobStatus.Approved => new SolidColorBrush(Color.FromRgb(76, 175, 80)),      // Green
                JobStatus.Rejected => new SolidColorBrush(Color.FromRgb(244, 67, 54)),      // Red
                JobStatus.Error => new SolidColorBrush(Color.FromRgb(244, 67, 54)),         // Red
                _ => new SolidColorBrush(Color.FromRgb(176, 176, 176))
            };
        }
        return new SolidColorBrush(Color.FromRgb(176, 176, 176));
    }
    
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts PrintJob to tree view items
/// </summary>
public class JobToTreeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is PrintJob job)
        {
            return new[] { job };
        }
        return Array.Empty<PrintJob>();
    }
    
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts enum to description string
/// </summary>
public class EnumDescriptionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Enum enumValue)
        {
            var field = enumValue.GetType().GetField(enumValue.ToString());
            var attr = field?.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                .FirstOrDefault() as System.ComponentModel.DescriptionAttribute;
            return attr?.Description ?? enumValue.ToString();
        }
        return value?.ToString() ?? string.Empty;
    }
    
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts bytes to human-readable string
/// </summary>
public class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            double size = bytes;
            while (size >= 1024 && i < suffixes.Length - 1)
            {
                size /= 1024;
                i++;
            }
            return $"{size:F1} {suffixes[i]}";
        }
        return "0 B";
    }
    
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Inverts boolean value
/// </summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
            return !b;
        return true;
    }
    
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
            return !b;
        return false;
    }
}

/// <summary>
/// Converts separation color to brush
/// </summary>
public class SeparationColorToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is SeparationColor color)
        {
            return color switch
            {
                SeparationColor.ProcessCyan => Brushes.Cyan,
                SeparationColor.ProcessMagenta => Brushes.Magenta,
                SeparationColor.ProcessYellow => Brushes.Yellow,
                SeparationColor.ProcessBlack => Brushes.Black,
                SeparationColor.Orange => Brushes.Orange,
                SeparationColor.Green => Brushes.Green,
                SeparationColor.Violet => Brushes.Violet,
                SeparationColor.Red => Brushes.Red,
                SeparationColor.Blue => Brushes.Blue,
                SeparationColor.White => Brushes.White,
                SeparationColor.Varnish => Brushes.LightGray,
                SeparationColor.Primer => Brushes.Gray,
                _ => Brushes.HotPink
            };
        }
        return Brushes.Transparent;
    }
    
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts bool to Visibility
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }
    
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility v)
            return v == Visibility.Visible;
        return false;
    }
}

/// <summary>
/// Converts SeparationColor to Color
/// </summary>
public class SeparationToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is SeparationColor color)
        {
            return color switch
            {
                SeparationColor.ProcessCyan => Colors.Cyan,
                SeparationColor.ProcessMagenta => Colors.Magenta,
                SeparationColor.ProcessYellow => Colors.Yellow,
                SeparationColor.ProcessBlack => Colors.Black,
                SeparationColor.Orange => Colors.Orange,
                SeparationColor.Green => Colors.Green,
                SeparationColor.Violet => Colors.Violet,
                SeparationColor.Red => Colors.Red,
                SeparationColor.Blue => Colors.Blue,
                SeparationColor.White => Colors.White,
                SeparationColor.Varnish => Colors.LightGray,
                SeparationColor.Primer => Colors.Gray,
                _ => Colors.HotPink
            };
        }
        return Colors.Transparent;
    }
    
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}