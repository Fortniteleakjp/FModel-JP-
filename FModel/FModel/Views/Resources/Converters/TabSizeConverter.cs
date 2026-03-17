using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace FModel.Views.Resources.Converters;

public class TabSizeConverter : IMultiValueConverter
{
    public static readonly TabSizeConverter Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values[0] is not TabControl tabControl)
            return 0;

        var hasDivider = parameter is string;
        var divisor = hasDivider ? double.Parse(parameter?.ToString() ?? "6") : Math.Max(tabControl.Items.Count, 1);
        var availableWidth = Math.Max(0, tabControl.ActualWidth - tabControl.Padding.Left - tabControl.Padding.Right - 4);
        var width = availableWidth / divisor;
        return width <= 1 ? 0 : Math.Max(0, width - (hasDivider ? 8 : 2));
    }

    public object[] ConvertBack(object value, Type[] targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}