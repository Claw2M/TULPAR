using System.Globalization;
using System.Windows.Data;

namespace Tulpar.App.Themes;

/// <summary>
/// Converts string button content to uppercase using <see cref="CultureInfo.CurrentCulture"/> while passing non-string content through unchanged.
/// </summary>
public sealed class UppercaseContentConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string text)
        {
            return text.ToUpper(CultureInfo.CurrentCulture);
        }

        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
