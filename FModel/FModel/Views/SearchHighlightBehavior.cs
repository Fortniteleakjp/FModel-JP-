using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace FModel.Views.Resources.Behaviors
{
    public static class SearchHighlightBehavior
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.RegisterAttached("Text", typeof(string), typeof(SearchHighlightBehavior), new PropertyMetadata(string.Empty, OnPropertyChanged));

        public static string GetText(DependencyObject obj) => (string)obj.GetValue(TextProperty);
        public static void SetText(DependencyObject obj, string value) => obj.SetValue(TextProperty, value);

        public static readonly DependencyProperty FilterTextProperty =
            DependencyProperty.RegisterAttached("FilterText", typeof(string), typeof(SearchHighlightBehavior), new PropertyMetadata(string.Empty, OnPropertyChanged));

        public static string GetFilterText(DependencyObject obj) => (string)obj.GetValue(FilterTextProperty);
        public static void SetFilterText(DependencyObject obj, string value) => obj.SetValue(FilterTextProperty, value);

        public static readonly DependencyProperty IsRegexProperty =
            DependencyProperty.RegisterAttached("IsRegex", typeof(bool), typeof(SearchHighlightBehavior), new PropertyMetadata(false, OnPropertyChanged));

        public static bool GetIsRegex(DependencyObject obj) => (bool)obj.GetValue(IsRegexProperty);
        public static void SetIsRegex(DependencyObject obj, bool value) => obj.SetValue(IsRegexProperty, value);

        public static readonly DependencyProperty MatchCaseProperty =
            DependencyProperty.RegisterAttached("MatchCase", typeof(bool), typeof(SearchHighlightBehavior), new PropertyMetadata(false, OnPropertyChanged));

        public static bool GetMatchCase(DependencyObject obj) => (bool)obj.GetValue(MatchCaseProperty);
        public static void SetMatchCase(DependencyObject obj, bool value) => obj.SetValue(MatchCaseProperty, value);

        public static readonly DependencyProperty HighlightBrushProperty =
            DependencyProperty.RegisterAttached("HighlightBrush", typeof(Brush), typeof(SearchHighlightBehavior), new PropertyMetadata(Brushes.Purple, OnPropertyChanged));

        public static Brush GetHighlightBrush(DependencyObject obj) => (Brush)obj.GetValue(HighlightBrushProperty);
        public static void SetHighlightBrush(DependencyObject obj, Brush value) => obj.SetValue(HighlightBrushProperty, value);

        private static void OnPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBlock textBlock)
            {
                UpdateTextBlock(textBlock);
            }
        }

        private static void UpdateTextBlock(TextBlock textBlock)
        {
            var text = GetText(textBlock) ?? string.Empty;
            var filter = GetFilterText(textBlock) ?? string.Empty;
            var isRegex = GetIsRegex(textBlock);
            var matchCase = GetMatchCase(textBlock);
            var highlightBrush = GetHighlightBrush(textBlock);

            try
            {
                textBlock.Inlines.Clear();

                if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(filter))
                {
                    textBlock.Text = text;
                    return;
                }

                if (isRegex)
                {
                    var regexOptions = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                    var matches = Regex.Matches(text, filter, regexOptions);
                    
                    if (matches.Count == 0)
                    {
                        textBlock.Text = text;
                        return;
                    }

                    int lastIndex = 0;
                    foreach (Match match in matches)
                    {
                        if (match.Index > lastIndex) textBlock.Inlines.Add(new Run(text.Substring(lastIndex, match.Index - lastIndex)));
                        textBlock.Inlines.Add(new Run(text.Substring(match.Index, match.Length)) { Foreground = highlightBrush, FontWeight = FontWeights.Bold });
                        lastIndex = match.Index + match.Length;
                    }
                    if (lastIndex < text.Length) textBlock.Inlines.Add(new Run(text.Substring(lastIndex)));
                }
                else
                {
                    var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                    int index = text.IndexOf(filter, comparison);
                    if (index < 0)
                    {
                        textBlock.Text = text;
                        return;
                    }

                    int lastIndex = 0;
                    while (index >= 0)
                    {
                        if (index > lastIndex) textBlock.Inlines.Add(new Run(text.Substring(lastIndex, index - lastIndex)));
                        textBlock.Inlines.Add(new Run(text.Substring(index, filter.Length)) { Foreground = highlightBrush, FontWeight = FontWeights.Bold });
                        lastIndex = index + filter.Length;
                        index = text.IndexOf(filter, lastIndex, comparison);
                    }
                    if (lastIndex < text.Length) textBlock.Inlines.Add(new Run(text.Substring(lastIndex)));
                }
            }
            catch
            {
                try
                {
                    textBlock.Text = text;
                }
                catch
                {
                    // ignored
                }
            }
        }
    }
}