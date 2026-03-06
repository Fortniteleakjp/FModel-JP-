using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace FModel.Views.Resources.Converters;

public class AssetClassBadgeBrushConverter : IValueConverter
{
    public static readonly AssetClassBadgeBrushConverter Instance = new();

    private const string BackgroundKey = "Background";
    private const string BorderKey = "Border";
    private const string ForegroundKey = "Foreground";
    private const string CardBackgroundKey = "CardBackground";
    private const string CardBorderKey = "CardBorder";
    private const string CardSectionBackgroundKey = "CardSectionBackground";

    private static readonly SolidColorBrush DefaultBackground = FreezeBrush("#2A2E34");
    private static readonly SolidColorBrush DefaultBorder = FreezeBrush("#49515C");
    private static readonly SolidColorBrush DefaultForeground = FreezeBrush("#E9EEF5");

    private static readonly Dictionary<string, (SolidColorBrush Background, SolidColorBrush Border, SolidColorBrush Foreground)> ExactMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Texture2D"] = Palette("#1F5A8A", "#58B7FF"),
            ["TextureCube"] = Palette("#1F5A8A", "#58B7FF"),
            ["TextureRenderTarget2D"] = Palette("#1F5A8A", "#58B7FF"),
            ["StaticMesh"] = Palette("#6A4A1E", "#E1A24D"),
            ["SkeletalMesh"] = Palette("#6A4A1E", "#E1A24D"),
            ["Material"] = Palette("#6B2C7A", "#D187F0"),
            ["MaterialInstance"] = Palette("#6B2C7A", "#D187F0"),
            ["MaterialInstanceConstant"] = Palette("#6B2C7A", "#D187F0"),
            ["AnimationBlueprint"] = Palette("#2A7A28", "#56C94F"),
            ["AnimBlueprintGeneratedClass"] = Palette("#2A7A28", "#56C94F"),
            ["SoundWave"] = Palette("#7A5A14", "#F2C94C"),
            ["DataTable"] = Palette("#7B2F49", "#F28FB7"),
            ["StringTable"] = Palette("#7B2F49", "#F28FB7"),
            ["CurveTable"] = Palette("#7B2F49", "#F28FB7"),
            ["NiagaraSystem"] = Palette("#0E5E5E", "#4ED1D1"),
            ["NiagaraEmitter"] = Palette("#0E5E5E", "#4ED1D1"),
            ["Blueprint"] = Palette("#1E4A73", "#6DB9FF"),
            ["BlueprintGeneratedClass"] = Palette("#1E4A73", "#6DB9FF")
        };

    private static readonly Dictionary<string, (SolidColorBrush Background, SolidColorBrush Border, SolidColorBrush Foreground)> DynamicMap =
        new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var className = value?.ToString();
        var target = parameter?.ToString() ?? BackgroundKey;

        var palette = ResolvePalette(className);
        return target switch
        {
            CardBackgroundKey => Tint(palette.Background, 38),
            CardBorderKey => Tint(palette.Border, 96),
            CardSectionBackgroundKey => Tint(palette.Background, 62),
            BorderKey => palette.Border,
            ForegroundKey => palette.Foreground,
            _ => palette.Background
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }

    private static (SolidColorBrush Background, SolidColorBrush Border, SolidColorBrush Foreground) ResolvePalette(string className)
    {
        if (string.IsNullOrWhiteSpace(className) || className.Equals("不明", StringComparison.OrdinalIgnoreCase))
            return (DefaultBackground, DefaultBorder, DefaultForeground);

        if (ExactMap.TryGetValue(className, out var exactPalette))
            return exactPalette;

        // Category fallback rules for broad class coverage.
        if (ContainsAny(className, "Texture", "RenderTarget", "VirtualTexture"))
            return Palette("#1F5A8A", "#58B7FF");
        if (ContainsAny(className, "Anim", "Animation", "BlendSpace", "Montage", "Skeleton"))
            return Palette("#2A7A28", "#56C94F");
        if (ContainsAny(className, "Mesh", "Skeletal", "StaticMesh", "PhysicsAsset"))
            return Palette("#6A4A1E", "#E1A24D");
        if (ContainsAny(className, "Material"))
            return Palette("#6B2C7A", "#D187F0");
        if (ContainsAny(className, "Sound", "Audio", "Dialogue"))
            return Palette("#7A5A14", "#F2C94C");
        if (ContainsAny(className, "Blueprint", "WidgetBlueprint"))
            return Palette("#1E4A73", "#6DB9FF");
        if (ContainsAny(className, "DataTable", "StringTable", "CurveTable", "DataAsset", "PrimaryDataAsset"))
            return Palette("#7B2F49", "#F28FB7");
        if (ContainsAny(className, "Niagara", "VFX", "Particle"))
            return Palette("#0E5E5E", "#4ED1D1");
        if (ContainsAny(className, "World", "Level", "Map"))
            return Palette("#7A2A2A", "#E58A8A");

        lock (DynamicMap)
        {
            if (DynamicMap.TryGetValue(className, out var cached))
                return cached;

            var generated = GeneratePaletteFromHash(className);
            DynamicMap[className] = generated;
            return generated;
        }
    }

    private static bool ContainsAny(string source, params string[] fragments)
    {
        foreach (var fragment in fragments)
        {
            if (source.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static (SolidColorBrush Background, SolidColorBrush Border, SolidColorBrush Foreground) GeneratePaletteFromHash(string className)
    {
        var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(className);
        var hue = Math.Abs(hash % 360);

        var background = ColorFromHsl(hue, 0.45, 0.30);
        var border = ColorFromHsl(hue, 0.70, 0.62);

        return (FreezeBrush(background), FreezeBrush(border), DefaultForeground);
    }

    private static Color ColorFromHsl(double h, double s, double l)
    {
        h /= 360.0;

        if (s == 0)
        {
            var gray = (byte)Math.Round(l * 255);
            return Color.FromRgb(gray, gray, gray);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;

        static double HueToRgb(double pVal, double qVal, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6.0) return pVal + (qVal - pVal) * 6 * t;
            if (t < 1.0 / 2.0) return qVal;
            if (t < 2.0 / 3.0) return pVal + (qVal - pVal) * (2.0 / 3.0 - t) * 6;
            return pVal;
        }

        var r = HueToRgb(p, q, h + 1.0 / 3.0);
        var g = HueToRgb(p, q, h);
        var b = HueToRgb(p, q, h - 1.0 / 3.0);

        return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    private static (SolidColorBrush Background, SolidColorBrush Border, SolidColorBrush Foreground) Palette(string backgroundHex, string borderHex)
    {
        return (FreezeBrush(backgroundHex), FreezeBrush(borderHex), DefaultForeground);
    }

    private static SolidColorBrush Tint(SolidColorBrush source, byte alpha)
    {
        var color = source.Color;
        return FreezeBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    private static SolidColorBrush FreezeBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush FreezeBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
