using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace FModel.Framework;

/// <summary>
/// 依存ライブラリを増やさずに、簡易 Markdown を WPF の FlowDocument へ変換する軽量レンダラ。
/// 見出し(#,##,###)・箇条書き(-,*,+)・番号付き(1.)・水平線(---)・段落、
/// インラインの **太字**・*斜体*・`コード`・[リンク](url) に対応。
/// </summary>
public static class MarkdownRenderer
{
    private static readonly Regex Inline = new(
        @"\[(?<ltext>[^\]]+)\]\((?<lurl>[^)\s]+)\)" + // [text](url)
        @"|\*\*(?<bold>.+?)\*\*" +                      // **bold**
        @"|__(?<bold2>.+?)__" +                         // __bold__
        @"|`(?<code>[^`]+)`" +                          // `code`
        @"|\*(?<it>.+?)\*" +                            // *italic*
        @"|_(?<it2>.+?)_",                              // _italic_
        RegexOptions.Compiled);

    private static readonly Regex OrderedRegex = new(@"^\d+\.\s+", RegexOptions.Compiled);

    public static FlowDocument ToFlowDocument(string markdown)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(10), FontSize = 13 };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, AdonisUI.Brushes.ForegroundBrush);

        if (string.IsNullOrEmpty(markdown)) return doc;

        var lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i].TrimEnd();

            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            if (line.StartsWith("### ")) { doc.Blocks.Add(Heading(line[4..], 15)); i++; continue; }
            if (line.StartsWith("## ")) { doc.Blocks.Add(Heading(line[3..], 19)); i++; continue; }
            if (line.StartsWith("# ")) { doc.Blocks.Add(Heading(line[2..], 25)); i++; continue; }

            if (IsRule(line)) { doc.Blocks.Add(Rule()); i++; continue; }

            if (IsBullet(line))
            {
                var list = NewList(TextMarkerStyle.Disc);
                while (i < lines.Length && IsBullet(lines[i].TrimEnd()))
                {
                    list.ListItems.Add(ListItemFrom(StripBullet(lines[i].TrimEnd())));
                    i++;
                }
                doc.Blocks.Add(list);
                continue;
            }

            if (IsOrdered(line))
            {
                var list = NewList(TextMarkerStyle.Decimal);
                while (i < lines.Length && IsOrdered(lines[i].TrimEnd()))
                {
                    list.ListItems.Add(ListItemFrom(StripOrdered(lines[i].TrimEnd())));
                    i++;
                }
                doc.Blocks.Add(list);
                continue;
            }

            // 段落: 連続する通常行を1つの段落にまとめる
            var sb = new StringBuilder();
            while (i < lines.Length)
            {
                var l = lines[i].TrimEnd();
                if (string.IsNullOrWhiteSpace(l) || IsBlockStart(l)) break;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(l.Trim());
                i++;
            }

            var p = new Paragraph { Margin = new Thickness(0, 2, 0, 8) };
            AddInlines(p.Inlines, sb.ToString());
            doc.Blocks.Add(p);
        }

        return doc;
    }

    private static bool IsBlockStart(string l) =>
        l.StartsWith("# ") || l.StartsWith("## ") || l.StartsWith("### ") || IsRule(l) || IsBullet(l) || IsOrdered(l);

    private static bool IsRule(string l) => l is "---" or "***" or "___";
    private static bool IsBullet(string l) => l.StartsWith("- ") || l.StartsWith("* ") || l.StartsWith("+ ");
    private static string StripBullet(string l) => l[2..].Trim();
    private static bool IsOrdered(string l) => OrderedRegex.IsMatch(l);
    private static string StripOrdered(string l) => OrderedRegex.Replace(l, string.Empty);

    private static Paragraph Heading(string text, double size)
    {
        var p = new Paragraph { FontSize = size, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 6) };
        AddInlines(p.Inlines, text.Trim());
        return p;
    }

    private static List NewList(TextMarkerStyle marker) => new()
    {
        MarkerStyle = marker,
        Margin = new Thickness(18, 2, 0, 8),
        Padding = new Thickness(0)
    };

    private static ListItem ListItemFrom(string text)
    {
        var p = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
        AddInlines(p.Inlines, text);
        return new ListItem(p);
    }

    private static Block Rule()
    {
        var rect = new Rectangle
        {
            Height = 1, Fill = Brushes.Gray, Opacity = 0.5,
            Margin = new Thickness(0, 6, 0, 6), HorizontalAlignment = HorizontalAlignment.Stretch
        };
        return new BlockUIContainer(rect) { Margin = new Thickness(0) };
    }

    private static void AddInlines(InlineCollection inlines, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var last = 0;
        foreach (Match m in Inline.Matches(text))
        {
            if (m.Index > last) inlines.Add(new Run(text[last..m.Index]));

            if (m.Groups["ltext"].Success) inlines.Add(Link(m.Groups["ltext"].Value, m.Groups["lurl"].Value));
            else if (m.Groups["bold"].Success) inlines.Add(new Bold(new Run(m.Groups["bold"].Value)));
            else if (m.Groups["bold2"].Success) inlines.Add(new Bold(new Run(m.Groups["bold2"].Value)));
            else if (m.Groups["code"].Success) inlines.Add(Code(m.Groups["code"].Value));
            else if (m.Groups["it"].Success) inlines.Add(new Italic(new Run(m.Groups["it"].Value)));
            else if (m.Groups["it2"].Success) inlines.Add(new Italic(new Run(m.Groups["it2"].Value)));

            last = m.Index + m.Length;
        }

        if (last < text.Length) inlines.Add(new Run(text[last..]));
    }

    private static Inline Code(string text) => new Run(text)
    {
        FontFamily = new FontFamily("Consolas, Courier New, monospace"),
        Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128))
    };

    private static Inline Link(string text, string url)
    {
        var run = new Run(text);
        Hyperlink link;
        try { link = new Hyperlink(run) { NavigateUri = new Uri(url, UriKind.RelativeOrAbsolute) }; }
        catch { return run; }
        link.RequestNavigate += OnRequestNavigate;
        return link;
    }

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* リンクを開けない場合は無視 */ }
        e.Handled = true;
    }
}
