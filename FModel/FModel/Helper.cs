using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Documents;
using System.Text.RegularExpressions;
using System.Windows.Navigation;

namespace FModel;

public static class Helper
{
    public static string FixKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            return string.Empty;

        var keySpan = key.AsSpan().Trim();
        if (keySpan.Length > sizeof(char) * (2 /* 0x */ + 32 /* FAES = 256 bit */)) // maybe strictly check for length?
            return string.Empty; // bullshit key

        Span<char> resultSpan = stackalloc char[keySpan.Length + 2 /* pad for 0x */];
        keySpan.ToUpperInvariant(resultSpan[2..]);

        if (resultSpan[2..].StartsWith("0X"))
            resultSpan = resultSpan[2..];
        else
            resultSpan[0] = '0';

        resultSpan[1] = 'x';

        return new string(resultSpan);
    }

    public static string GenerateFormattedFileName(string format, string baseFileName)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return baseFileName;
        }

        var now = DateTime.Now;
        var result = format.Replace("{FileName}", baseFileName, StringComparison.OrdinalIgnoreCase)
                         .Replace("{yyyy}", now.ToString("yyyy"), StringComparison.OrdinalIgnoreCase)
                         .Replace("{yy}", now.ToString("yy"), StringComparison.OrdinalIgnoreCase)
                         .Replace("{MM}", now.ToString("MM"), StringComparison.OrdinalIgnoreCase)
                         .Replace("{dd}", now.ToString("dd"), StringComparison.OrdinalIgnoreCase)
                         .Replace("{HH}", now.ToString("HH"), StringComparison.OrdinalIgnoreCase)
                         .Replace("{mm}", now.ToString("mm"), StringComparison.OrdinalIgnoreCase)
                         .Replace("{ss}", now.ToString("ss"), StringComparison.OrdinalIgnoreCase);

        // ファイル名として無効な文字を '_' に置換
        return string.Join("_", result.Split(Path.GetInvalidFileNameChars()));
    }

    public static void OpenWindow<T>(string windowName, Action action) where T : Window
    {
        if (!IsWindowOpen<T>(windowName))
        {
            action();
        }
        else
        {
            var w = GetOpenedWindow<T>(windowName);
            if (windowName == "検索ウィンドウ") w.WindowState = WindowState.Normal;
            w.Focus();
        }
    }

    public static T GetWindow<T>(string windowName, Func<T> createInstance) where T : Window
    {
        T ret;
        if (!IsWindowOpen<T>(windowName))
        {
            ret = createInstance(); // ここでインスタンスを生成
            ret.Show(); // ここでウィンドウを表示
        }
        else
        {
            ret = (T) GetOpenedWindow<T>(windowName);
        }

        ret.Focus();
        ret.Activate();
        return ret;
    }

    public static void CloseWindow<T>(string windowName) where T : Window
    {
        if (!IsWindowOpen<T>(windowName)) return;
        GetOpenedWindow<T>(windowName).Close();
    }

    private static bool IsWindowOpen<T>(string name = "") where T : Window
    {
        return string.IsNullOrEmpty(name)
            ? Application.Current.Windows.OfType<T>().Any()
            : Application.Current.Windows.OfType<T>().Any(w => w.Title.Equals(name));
    }

    private static Window GetOpenedWindow<T>(string name) where T : Window
    {
        return Application.Current.Windows.OfType<T>().FirstOrDefault(w => w.Title.Equals(name));
    }

    public static bool IsNaN(double value)
    {
        var ulongValue = Unsafe.As<double, ulong>(ref value);
        var exp = ulongValue & 0xfff0000000000000;
        var man = ulongValue & 0x000fffffffffffff;
        return exp is 0x7ff0000000000000 or 0xfff0000000000000 && man != 0;
    }

    public static bool AreVirtuallyEqual(double d1, double d2)
    {
        if (double.IsPositiveInfinity(d1))
            return double.IsPositiveInfinity(d2);

        if (double.IsNegativeInfinity(d1))
            return double.IsNegativeInfinity(d2);

        if (IsNaN(d1))
            return IsNaN(d2);

        var n = d1 - d2;
        var d = (Math.Abs(d1) + Math.Abs(d2) + 10) * 1.0e-15;
        return -d < n && d > n;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DegreesToRadians(float degrees)
    {
        const float ratio = MathF.PI / 180f;
        return ratio * degrees;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float RadiansToDegrees(float radians)
    {
        const float ratio = 180f / MathF.PI;
        return radians * ratio;
    }

    /// <summary>
    /// Converts a markdown-like string to a FlowDocument for RichTextBox.
    /// Supports: **bold**, *italic*, _italic_, __underline__, ~~strikethrough~~, and [link](url).
    /// </summary>
    /// <param name="markdownText">The markdown text to convert.</param>
    /// <returns>A FlowDocument with formatted text.</returns>
    public static FlowDocument CreateFlowDocumentFromMarkdown(string markdownText)
    {
        var flowDocument = new FlowDocument();
        var paragraph = new Paragraph();

        // Regex to find markdown patterns: bold, italic, underline, strikethrough, and links
        // This regex uses named capture groups to identify the type of formatting.
        var regex = new Regex(
            @"(\*\*|__)(?=\S)(.+?)(?<=\S)\1" + // Bold/Underline (__ or **)
            @"|(\*|_)(?=\S)(.+?)(?<=\S)\1" +    // Italic (_ or *)
            @"|~~(?=\S)(.+?)(?<=\S)~~" +       // Strikethrough
            @"|\[(.+?)\]\((.+?)\)",            // Link [text](url)
            RegexOptions.Compiled);

        var lastIndex = 0;
        foreach (Match match in regex.Matches(markdownText))
        {
            // Add the plain text before the match
            if (match.Index > lastIndex)
            {
                paragraph.Inlines.Add(new Run(markdownText.Substring(lastIndex, match.Index - lastIndex)));
            }

            Inline newInline = null;
            if (match.Value.StartsWith("**"))
                newInline = new Bold(new Run(match.Groups[2].Value));
            else if (match.Value.StartsWith("__"))
                newInline = new Underline(new Run(match.Groups[2].Value));
            else if (match.Value.StartsWith("*") || match.Value.StartsWith("_"))
                newInline = new Italic(new Run(match.Groups[4].Value));
            else if (match.Value.StartsWith("~~"))
                newInline = new Run(match.Groups[5].Value) { TextDecorations = TextDecorations.Strikethrough };
            else if (match.Value.StartsWith("["))
            {
                var link = new Hyperlink(new Run(match.Groups[6].Value));
                try
                {
                    link.NavigateUri = new Uri(match.Groups[7].Value);
                    link.RequestNavigate += (sender, e) => {
                        // For security, only allow navigation to http/https schemes.
                        if (e.Uri.Scheme == "http" || e.Uri.Scheme == "https")
                        {
                            // Use Process.Start for external links to open in the default browser.
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                            e.Handled = true;
                        }
                    };
                }
                catch { /* Ignore invalid URIs */ }
                newInline = link;
            }

            if (newInline != null) paragraph.Inlines.Add(newInline);
            lastIndex = match.Index + match.Length;
        }

        // Add any remaining plain text
        if (lastIndex < markdownText.Length)
        {
            paragraph.Inlines.Add(new Run(markdownText.Substring(lastIndex)));
        }

        flowDocument.Blocks.Add(paragraph);
        return flowDocument;
    }
}
