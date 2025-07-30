using System;
using System.Globalization;
using System.Windows.Data;

namespace FModel.Views.Resources.Converters;

public class RelativeDateTimeConverter : IValueConverter
{
    public static readonly RelativeDateTimeConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DateTime dateTime)
        {
            var timeSpan = DateTime.Now - dateTime.ToLocalTime();

            int time;
            string unit;
            if (timeSpan.TotalSeconds < 30)
                return "たった今";

            if (timeSpan.TotalMinutes < 1)
            {
                time = timeSpan.Seconds;
                unit = "秒";
            }
            else if (timeSpan.TotalHours < 1)
            {
                time = timeSpan.Minutes;
                unit = "分";
            }
            else switch (timeSpan.TotalDays)
            {
                case < 1:
                    time = timeSpan.Hours;
                    unit = "時間";
                    break;
                case < 7:
                    time = timeSpan.Days;
                    unit = "日";
                    break;
                case < 30:
                    time = timeSpan.Days / 7;
                    unit = "週";
                    break;
                case < 365:
                    time = timeSpan.Days / 30;
                    unit = "月";
                    break;
                default:
                    time = timeSpan.Days / 365;
                    unit = "年";
                    break;
            }

            return $"{time} {unit} 前";
        }
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
