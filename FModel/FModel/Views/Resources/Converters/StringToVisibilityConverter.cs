using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FModel.Views.Resources.Converters
{
    public class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var strValue = value as string;
            var paramStr = parameter as string;
            return !string.IsNullOrEmpty(strValue) && strValue.Equals(paramStr, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}