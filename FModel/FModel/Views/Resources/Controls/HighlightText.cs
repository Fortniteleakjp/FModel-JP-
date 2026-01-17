using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace FModel.Views.Resources.Controls
{
    public static class HighlightText
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(HighlightText), new PropertyMetadata(string.Empty, OnTextChanged));

        public static string GetText(DependencyObject obj) => (string)obj.GetValue(TextProperty);
        public static void SetText(DependencyObject obj, string value) => obj.SetValue(TextProperty, value);

        public static readonly DependencyProperty HighlightProperty = DependencyProperty.RegisterAttached(
            "Highlight", typeof(string), typeof(HighlightText), new PropertyMetadata(string.Empty, OnTextChanged));

        public static string GetHighlight(DependencyObject obj) => (string)obj.GetValue(HighlightProperty);
        public static void SetHighlight(DependencyObject obj, string value) => obj.SetValue(HighlightProperty, value);

        public static readonly DependencyProperty HighlightBrushProperty = DependencyProperty.RegisterAttached(
            "HighlightBrush", typeof(Brush), typeof(HighlightText), new PropertyMetadata(Brushes.Yellow));

        public static Brush GetHighlightBrush(DependencyObject obj) => (Brush)obj.GetValue(HighlightBrushProperty);
        public static void SetHighlightBrush(DependencyObject obj, Brush value) => obj.SetValue(HighlightBrushProperty, value);

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBlock textBlock)
            {
                var text = GetText(textBlock);
                var highlight = GetHighlight(textBlock);
                var highlightBrush = GetHighlightBrush(textBlock);

                textBlock.Inlines.Clear();

                if (string.IsNullOrEmpty(text))
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(highlight))
                {
                    textBlock.Inlines.Add(new Run(text));
                    return;
                }

                var keywords = highlight.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (keywords.Length == 0)
                {
                    textBlock.Inlines.Add(new Run(text));
                    return;
                }

                var matches = new List<(int Start, int Length)>();

                foreach (var keyword in keywords)
                {
                    int index = 0;
                    while ((index = text.IndexOf(keyword, index, StringComparison.OrdinalIgnoreCase)) != -1)
                    {
                        matches.Add((index, keyword.Length));
                        index += keyword.Length;
                    }
                }

                if (matches.Count == 0)
                {
                    textBlock.Inlines.Add(new Run(text));
                    return;
                }

                // マッチした位置をソートして結合（重複対策）
                matches.Sort((a, b) => a.Start.CompareTo(b.Start));

                var mergedMatches = new List<(int Start, int Length)>();
                var current = matches[0];

                for (int i = 1; i < matches.Count; i++)
                {
                    var next = matches[i];
                    if (next.Start < current.Start + current.Length)
                    {
                        int newEnd = Math.Max(current.Start + current.Length, next.Start + next.Length);
                        current = (current.Start, newEnd - current.Start);
                    }
                    else
                    {
                        mergedMatches.Add(current);
                        current = next;
                    }
                }
                mergedMatches.Add(current);

                int lastIndex = 0;
                foreach (var match in mergedMatches)
                {
                    if (match.Start > lastIndex)
                    {
                        textBlock.Inlines.Add(new Run(text.Substring(lastIndex, match.Start - lastIndex)));
                    }

                    var highlightRun = new Run(text.Substring(match.Start, match.Length))
                    {
                        Foreground = highlightBrush,
                        FontWeight = FontWeights.Bold
                    };
                    textBlock.Inlines.Add(highlightRun);

                    lastIndex = match.Start + match.Length;
                }

                if (lastIndex < text.Length)
                {
                    textBlock.Inlines.Add(new Run(text.Substring(lastIndex)));
                }
            }
        }
    }
}
