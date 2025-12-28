using System;
using System.Globalization;
using System.Windows.Data;

namespace FModel.Views.Resources.Converters
{
    public class BoolToTypeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isDirectory)
            {
                return isDirectory ? "フォルダ" : "ファイル";
            }
            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
