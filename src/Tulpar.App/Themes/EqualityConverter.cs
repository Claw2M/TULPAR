using System.Globalization;
using System.Windows.Data;

namespace Tulpar.App.Themes;

/// <summary>
/// Compares two bound objects by reference or equality (e.g., item vs SelectedMissionItem).
/// </summary>
public sealed class ReferenceEqualityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2)
        {
            return false;
        }

        return Equals(values[0], values[1]);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
