using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace IncidentCommand.App;

/// true -> Collapsed, false -> Visible. For "empty state" hints.
public sealed class InvertBoolVis : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && v != Visibility.Visible;
}
