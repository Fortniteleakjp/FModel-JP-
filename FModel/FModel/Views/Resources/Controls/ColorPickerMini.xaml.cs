using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FModel.Views.Resources.Controls;

/// <summary>"#AARRGGBB" 文字列を A/R/G/B スライダー＋スウォッチ＋HEX入力で編集する小さなカラーピッカー。</summary>
public partial class ColorPickerMini : UserControl
{
    public static readonly DependencyProperty HexProperty = DependencyProperty.Register(
        nameof(Hex), typeof(string), typeof(ColorPickerMini),
        new FrameworkPropertyMetadata("#FFFFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHexPropertyChanged));

    public string Hex
    {
        get => (string) GetValue(HexProperty);
        set => SetValue(HexProperty, value);
    }

    private bool _suppress;

    public ColorPickerMini()
    {
        InitializeComponent();
        ApplyHexToUi(Hex);
    }

    private static void OnHexPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ColorPickerMini c && !c._suppress)
            c.ApplyHexToUi(e.NewValue as string);
    }

    private void ApplyHexToUi(string hex)
    {
        var (a, r, g, b) = ParseHex(hex);
        _suppress = true;
        SliderA.Value = a; SliderR.Value = r; SliderG.Value = g; SliderB.Value = b;
        ValA.Text = a.ToString(); ValR.Text = r.ToString(); ValG.Text = g.ToString(); ValB.Text = b.ToString();
        HexBox.Text = Compose(a, r, g, b);
        Swatch.Background = new SolidColorBrush(Color.FromArgb((byte) a, (byte) r, (byte) g, (byte) b));
        _suppress = false;
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress) return;

        var a = (int) SliderA.Value;
        var r = (int) SliderR.Value;
        var g = (int) SliderG.Value;
        var b = (int) SliderB.Value;

        ValA.Text = a.ToString(); ValR.Text = r.ToString(); ValG.Text = g.ToString(); ValB.Text = b.ToString();
        Swatch.Background = new SolidColorBrush(Color.FromArgb((byte) a, (byte) r, (byte) g, (byte) b));

        _suppress = true;
        HexBox.Text = Compose(a, r, g, b);
        Hex = Compose(a, r, g, b);
        _suppress = false;
    }

    private void OnHexLostFocus(object sender, RoutedEventArgs e) => CommitHexBox();

    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitHexBox();
    }

    private void CommitHexBox()
    {
        var (a, r, g, b) = ParseHex(HexBox.Text);
        Hex = Compose(a, r, g, b); // -> OnHexPropertyChanged -> ApplyHexToUi で各UIへ反映
    }

    private static (int a, int r, int g, int b) ParseHex(string hex)
    {
        try
        {
            var s = (hex ?? string.Empty).Trim().TrimStart('#');
            if (s.Length == 8)
                return (Convert.ToInt32(s.Substring(0, 2), 16), Convert.ToInt32(s.Substring(2, 2), 16),
                        Convert.ToInt32(s.Substring(4, 2), 16), Convert.ToInt32(s.Substring(6, 2), 16));
            if (s.Length == 6)
                return (255, Convert.ToInt32(s.Substring(0, 2), 16),
                        Convert.ToInt32(s.Substring(2, 2), 16), Convert.ToInt32(s.Substring(4, 2), 16));
        }
        catch { /* 不正入力は白にフォールバック */ }
        return (255, 255, 255, 255);
    }

    private static string Compose(int a, int r, int g, int b) => $"#{a:X2}{r:X2}{g:X2}{b:X2}";
}
