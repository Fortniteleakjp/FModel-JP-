using System;
using System.Globalization;
using System.Windows.Data;

namespace FModel.Views.Resources.Converters;

/// <summary>
/// Decides how many columns the JSON editor spans in the tab content grid.
/// With an image (true): the editor stays in column 0 only, so it sits side-by-side
/// with the image (column 2) and never overlaps / shows behind it.
/// Without an image (false): the editor spans all 3 columns and uses the full width.
/// Bound directly on the element (not via a Style trigger) so the ElementName binding
/// resolves reliably inside the DataTemplate.
/// </summary>
public class HasImageToColumnSpanConverter : IValueConverter
{
    public static readonly HasImageToColumnSpanConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? 1 : 3;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
