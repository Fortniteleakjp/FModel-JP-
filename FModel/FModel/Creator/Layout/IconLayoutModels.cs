using System;
using FModel.Framework;
using Newtonsoft.Json;

namespace FModel.Creator.Layout;

/// <summary>生成アイコンのカテゴリ。名前欄にはそのアイテムの名前（武器なら武器名、スキンならスキン名）が入る。</summary>
public enum EIconLayoutCategory
{
    Weapon,   // 武器
    Cosmetic, // スキン・コスメティック
    Item      // その他アイテム
}

/// <summary>背景の描き方。</summary>
public enum EIconLayoutBackground
{
    Rarity,     // レア度グラデーション（従来）
    SolidColor, // 単色
    Image       // 任意の背景画像
}

public enum EIconLayoutAlign
{
    Left,
    Center,
    Right
}

/// <summary>位置とサイズを持つ要素の基底。座標・サイズはキャンバス(px)基準の左上原点。</summary>
public abstract class IconElementBase : ViewModel
{
    private bool _visible = true;
    [JsonProperty] public bool Visible { get => _visible; set => SetProperty(ref _visible, value); }

    private double _x;
    [JsonProperty] public double X { get => _x; set => SetProperty(ref _x, value); }

    private double _y;
    [JsonProperty] public double Y { get => _y; set => SetProperty(ref _y, value); }
}

/// <summary>プレビュー画像など、矩形に描画される要素。</summary>
public class IconImageElement : IconElementBase
{
    private double _width = 512;
    [JsonProperty] public double Width { get => _width; set => SetProperty(ref _width, Math.Max(1, value)); }

    private double _height = 512;
    [JsonProperty] public double Height { get => _height; set => SetProperty(ref _height, Math.Max(1, value)); }

    private bool _keepAspect = true;
    [JsonProperty] public bool KeepAspect { get => _keepAspect; set => SetProperty(ref _keepAspect, value); }
}

/// <summary>名前や説明などのテキスト要素。X,Y は左上、Width は整列/折り返しの幅。</summary>
public class IconTextElement : IconElementBase
{
    private double _width = 508;
    [JsonProperty] public double Width { get => _width; set => SetProperty(ref _width, Math.Max(1, value)); }

    private double _fontSize = 45;
    [JsonProperty] public double FontSize { get => _fontSize; set => SetProperty(ref _fontSize, Math.Max(1, value)); }

    private string _color = "#FFFFFFFF";
    [JsonProperty] public string Color { get => _color; set => SetProperty(ref _color, value); }

    private string _outlineColor = "#FF000000";
    [JsonProperty] public string OutlineColor { get => _outlineColor; set => SetProperty(ref _outlineColor, value); }

    private double _outlineThickness; // 0 = 縁取り無し
    [JsonProperty] public double OutlineThickness { get => _outlineThickness; set => SetProperty(ref _outlineThickness, Math.Max(0, value)); }

    private EIconLayoutAlign _align = EIconLayoutAlign.Center;
    [JsonProperty] public EIconLayoutAlign Align { get => _align; set => SetProperty(ref _align, value); }

    private int _maxLines = 4; // 説明の最大行数（名前は1行）
    [JsonProperty] public int MaxLines { get => _maxLines; set => SetProperty(ref _maxLines, Math.Max(1, value)); }
}

/// <summary>1カテゴリ分のレイアウト定義。</summary>
public class IconLayoutTemplate : ViewModel
{
    private bool _enabled;
    /// <summary>true のとき、このカテゴリのアイコンはカスタムレイアウトで描画される。</summary>
    [JsonProperty] public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }

    private int _width = 512;
    [JsonProperty] public int Width { get => _width; set => SetProperty(ref _width, Math.Clamp(value, 16, 4096)); }

    private int _height = 512;
    [JsonProperty] public int Height { get => _height; set => SetProperty(ref _height, Math.Clamp(value, 16, 4096)); }

    private EIconLayoutBackground _backgroundMode = EIconLayoutBackground.Rarity;
    [JsonProperty] public EIconLayoutBackground BackgroundMode { get => _backgroundMode; set => SetProperty(ref _backgroundMode, value); }

    private string _backgroundColor = "#FF101018";
    [JsonProperty] public string BackgroundColor { get => _backgroundColor; set => SetProperty(ref _backgroundColor, value); }

    private string _backgroundImagePath;
    /// <summary>背景画像モード時に使用するローカル画像ファイルのパス。</summary>
    [JsonProperty] public string BackgroundImagePath { get => _backgroundImagePath; set => SetProperty(ref _backgroundImagePath, value); }

    [JsonProperty] public IconImageElement Preview { get; set; } = new();
    [JsonProperty] public IconTextElement Name { get; set; } = new();
    [JsonProperty] public IconTextElement Description { get; set; } = new();

    /// <summary>null だった子要素を補完（部分的なJSON/古い設定対策）。</summary>
    public IconLayoutTemplate EnsureComplete()
    {
        Preview ??= new IconImageElement();
        Name ??= new IconTextElement();
        Description ??= new IconTextElement();
        return this;
    }

    public static IconLayoutTemplate DefaultFor(EIconLayoutCategory category) => category switch
    {
        EIconLayoutCategory.Weapon => new IconLayoutTemplate
        {
            Width = 1024, Height = 512,
            Preview = new IconImageElement { X = 24, Y = 24, Width = 464, Height = 464, KeepAspect = true },
            Name = new IconTextElement { X = 512, Y = 60, Width = 488, FontSize = 60, Align = EIconLayoutAlign.Left, OutlineColor = "#FF000000", OutlineThickness = 0 },
            Description = new IconTextElement { X = 512, Y = 150, Width = 488, FontSize = 24, Align = EIconLayoutAlign.Left, MaxLines = 8 }
        },
        _ => new IconLayoutTemplate
        {
            Width = 512, Height = 512,
            Preview = new IconImageElement { X = 2, Y = 2, Width = 508, Height = 508, KeepAspect = false },
            Name = new IconTextElement { X = 6, Y = 380, Width = 500, FontSize = 45, Align = EIconLayoutAlign.Center },
            Description = new IconTextElement { X = 20, Y = 432, Width = 472, FontSize = 14, Align = EIconLayoutAlign.Center, MaxLines = 4 }
        }
    };
}

/// <summary>全カテゴリのレイアウト設定（UserSettings に永続化）。</summary>
public class IconLayoutSettings : ViewModel
{
    [JsonProperty] public IconLayoutTemplate Weapon { get; set; } = IconLayoutTemplate.DefaultFor(EIconLayoutCategory.Weapon);
    [JsonProperty] public IconLayoutTemplate Cosmetic { get; set; } = IconLayoutTemplate.DefaultFor(EIconLayoutCategory.Cosmetic);
    [JsonProperty] public IconLayoutTemplate Item { get; set; } = IconLayoutTemplate.DefaultFor(EIconLayoutCategory.Item);

    public IconLayoutTemplate Get(EIconLayoutCategory category) => (category switch
    {
        EIconLayoutCategory.Weapon => Weapon ??= IconLayoutTemplate.DefaultFor(EIconLayoutCategory.Weapon),
        EIconLayoutCategory.Cosmetic => Cosmetic ??= IconLayoutTemplate.DefaultFor(EIconLayoutCategory.Cosmetic),
        _ => Item ??= IconLayoutTemplate.DefaultFor(EIconLayoutCategory.Item)
    }).EnsureComplete();

    public void ResetToDefault(EIconLayoutCategory category)
    {
        switch (category)
        {
            case EIconLayoutCategory.Weapon: Weapon = IconLayoutTemplate.DefaultFor(category); break;
            case EIconLayoutCategory.Cosmetic: Cosmetic = IconLayoutTemplate.DefaultFor(category); break;
            default: Item = IconLayoutTemplate.DefaultFor(category); break;
        }
    }
}

/// <summary>ExportType からカテゴリを判定する。</summary>
public static class IconLayoutCategoryMap
{
    public static EIconLayoutCategory From(string exportType)
    {
        if (string.IsNullOrEmpty(exportType)) return EIconLayoutCategory.Item;

        // 武器・ステータス系（BaseIconStats 相当）
        if (exportType.Contains("Weapon", StringComparison.OrdinalIgnoreCase) ||
            exportType.Contains("Trap", StringComparison.OrdinalIgnoreCase) ||
            exportType.Equals("FortAccoladeItemDefinition", StringComparison.OrdinalIgnoreCase) ||
            exportType.Equals("FortSpyTechItemDefinition", StringComparison.OrdinalIgnoreCase))
            return EIconLayoutCategory.Weapon;

        // スキン・コスメティック
        if (exportType.StartsWith("Athena", StringComparison.OrdinalIgnoreCase) ||
            exportType.StartsWith("Cosmetic", StringComparison.OrdinalIgnoreCase) ||
            exportType.Contains("CharacterCosmetic", StringComparison.OrdinalIgnoreCase) ||
            exportType.StartsWith("Sparks", StringComparison.OrdinalIgnoreCase))
            return EIconLayoutCategory.Cosmetic;

        return EIconLayoutCategory.Item;
    }
}
