using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ToolBox.Views;

/// <summary>将 bool 转换为 Visibility 的通用值转换器（true→Visible, false→Collapsed）。</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility v && v == Visibility.Visible;
    }
}