using System;
using System.Globalization;
using System.Windows.Data;
using FModel.Creator.Layout;

namespace FModel.Views.Resources.Converters;

/// <summary>アイコンレイアウト関連の enum を日本語ラベルに変換する。</summary>
public class IconLayoutLabelConverter : IValueConverter
{
    public static readonly IconLayoutLabelConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        EIconLayoutCategory.Weapon => "武器",
        EIconLayoutCategory.Cosmetic => "スキン・コスメ",
        EIconLayoutCategory.Item => "アイテム",
        EIconLayoutBackground.Rarity => "レア度グラデーション",
        EIconLayoutBackground.SolidColor => "単色",
        EIconLayoutBackground.Image => "画像",
        EIconLayoutAlign.Left => "左揃え",
        EIconLayoutAlign.Center => "中央揃え",
        EIconLayoutAlign.Right => "右揃え",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
