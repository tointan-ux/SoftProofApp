using System.Globalization;
using System.Windows.Data;

namespace SoftProof.UI.Converters;

/// <summary>
/// Two-way converter for radio-style bindings to an enum property:
/// IsChecked="{Binding Mode, Converter={StaticResource EnumEqualsConverter}, ConverterParameter=Pan}".
/// </summary>
public class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value != null && parameter != null &&
               string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);
    }
    
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter != null)
            return Enum.Parse(targetType, parameter.ToString()!);
        
        return Binding.DoNothing;
    }
}

/// <summary>Same idea for an int property (e.g. densitometer aperture 1 / 3 / 5).</summary>
public class IntEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is int i && int.TryParse(parameter?.ToString(), out var p) && i == p;
    }
    
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && int.TryParse(parameter?.ToString(), out var p))
            return p;
        
        return Binding.DoNothing;
    }
}
