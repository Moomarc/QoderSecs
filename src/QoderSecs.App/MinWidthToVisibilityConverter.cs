using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace QoderSecs.App;

/// <summary>
/// 容器窄到放不下时把标题收起来，免得只剩半个字贴在按钮旁边；
/// 阈值写在 ConverterParameter 上，用时自己标数。
/// </summary>
public sealed class MinWidthToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double width
            || parameter is not string text
            || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold))
        {
            return Visibility.Visible;
        }

        return width >= threshold ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
