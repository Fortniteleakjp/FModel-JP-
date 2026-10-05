using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using FModel.Services;

namespace FModel.Views.Resources.Controls;

public static class SearchPathHighlight
{
    private static readonly SolidColorBrush MatchBrush = CreateMatchBrush();

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(SearchPathHighlight), new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty PatternProperty = DependencyProperty.RegisterAttached(
        "Pattern", typeof(SearchHighlightPattern), typeof(SearchPathHighlight), new PropertyMetadata(null, OnChanged));

    public static string GetText(DependencyObject element) => (string) element.GetValue(TextProperty);
    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);
    public static SearchHighlightPattern GetPattern(DependencyObject element) => (SearchHighlightPattern) element.GetValue(PatternProperty);
    public static void SetPattern(DependencyObject element, SearchHighlightPattern value) => element.SetValue(PatternProperty, value);

    private static SolidColorBrush CreateMatchBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xE8, 0x79, 0xF9));
        brush.Freeze();
        return brush;
    }

    private static void OnChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock textBlock)
            return;

        var text = GetText(element) ?? string.Empty;
        var ranges = GetPattern(element)?.GetRanges(text);
        textBlock.Inlines.Clear();

        if (ranges == null || ranges.Count == 0)
        {
            textBlock.Text = text;
            return;
        }

        var position = 0;
        foreach (var (start, length) in ranges)
        {
            if (start > position)
                textBlock.Inlines.Add(new Run(text[position..start]));

            textBlock.Inlines.Add(new Run(text.Substring(start, length)) { Foreground = MatchBrush });
            position = start + length;
        }

        if (position < text.Length)
            textBlock.Inlines.Add(new Run(text[position..]));
    }
}
