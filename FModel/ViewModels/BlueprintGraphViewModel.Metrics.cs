using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace FModel.ViewModels;

/// <summary>
/// Widths a node card needs, measured with the fonts and margins of the templates in BlueprintGraphWindow.xaml:
/// the header, and on every pin row the input (glyph, name, typed-in value) and the output facing it.
/// A card narrower than that draws the texts of a row over each other.
/// </summary>
public static class BlueprintNodeMetrics
{
    // BlueprintGraphWindow.xaml: MaxWidth of the pin name and value texts, past which they are trimmed
    public const double PIN_NAME_MAX = 180;
    public const double PIN_VALUE_MAX = 180;

    private const double _MAX_WIDTH = 560;
    private const double _CARD_BORDER = BlueprintGraphNode.BORDER * 2;
    private const double _HEADER_PADDING = 16;
    private const double _GLYPH = 12;
    private const double _PIN_GAP = 3;
    private const double _VALUE_CHROME = 4 + 6; // margin and padding of the value box
    private const double _ROW_GAP = 18; // between an input and the output on the same row
    private const double _COMPACT_GAP = 10;
    private const double _SLACK = 4; // rounding and ClearType

    private static readonly Lock _lock = new();
    private static Typeface _regular, _semiBold, _bold, _code;

    /// <summary>Widens <paramref name="node"/> to fit its texts, never narrows it.</summary>
    public static void Fit(BlueprintGraphNode node)
    {
        try
        {
            node.Width = Math.Max(node.Width, Math.Min(_MAX_WIDTH, Math.Ceiling(Required(node))));
        }
        catch (Exception)
        {
            // no font to measure with, the card keeps its default width
        }
    }

    private static double Required(BlueprintGraphNode node)
    {
        lock (_lock)
        {
            EnsureTypefaces();

            var inputs = node.Inputs.Select(InputWidth).ToList();
            var outputs = node.Outputs.Select(OutputWidth).ToList();
            double required;

            if (node.IsCompact)
            {
                // inputs on the left, the result on the right, the operator symbol centered between them
                var symbol = Measure(node.Title, _bold, 18);
                var side = Math.Max(inputs.DefaultIfEmpty(0).Max(), outputs.DefaultIfEmpty(0).Max());
                required = _CARD_BORDER + 2 * (side + _COMPACT_GAP) + symbol;
            }
            else
            {
                required = 0;
                for (var row = 0; row < Math.Max(inputs.Count, outputs.Count); row++)
                {
                    var input = row < inputs.Count ? inputs[row] : 0;
                    var output = row < outputs.Count ? outputs[row] : 0;
                    required = Math.Max(required, _CARD_BORDER + input + (input > 0 && output > 0 ? _ROW_GAP : 0) + output);
                }

                if (node.HeaderHeight > 0)
                {
                    var header = Math.Max(Measure(node.Title, _semiBold, 12), Measure(node.Subtitle, _regular, 10));
                    required = Math.Max(required, _CARD_BORDER + _HEADER_PADDING + header);
                }
            }

            return required + _SLACK;
        }
    }

    // InputPin: StackPanel Margin="-6 0 0 0", glyph, name (Margin 3 0 0 0), value box (Margin 4 1 0 1, Padding 3 0)
    private static double InputWidth(BlueprintPin pin)
    {
        var width = -6 + _GLYPH + _PIN_GAP + Math.Min(PIN_NAME_MAX, Measure(pin.Name, _regular, 11));
        if (pin.HasValue) width += _VALUE_CHROME + Math.Min(PIN_VALUE_MAX, Measure(pin.Value, _code, 10));
        return width;
    }

    // OutputPin: StackPanel Margin="0 0 -6 0", name (Margin 0 0 3 0), glyph
    private static double OutputWidth(BlueprintPin pin) =>
        Math.Min(PIN_NAME_MAX, Measure(pin.Name, _regular, 11)) + _PIN_GAP + _GLYPH - 6;

    private static double Measure(string text, Typeface typeface, double size)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, Brushes.White, 1.0);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    private static void EnsureTypefaces()
    {
        if (_regular != null) return;

        var family = SystemFonts.MessageFontFamily;
        _regular = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _semiBold = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        _bold = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        _code = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    }
}
