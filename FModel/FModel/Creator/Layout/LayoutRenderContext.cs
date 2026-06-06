using System;
using System.Windows;
using FModel.Creator.Bases;
using SkiaSharp;

namespace FModel.Creator.Layout;

/// <summary>
/// レイアウト描画に必要なアイテム由来のデータ（名前・説明・プレビュー画像・レア度色）。
/// Creator の解析結果から1度だけ作り、編集ウインドウでは使い回して高速に再描画する。
/// </summary>
public class LayoutRenderContext
{
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public SKBitmap Preview { get; set; }
    public SKColor[] Background { get; set; } = { SKColor.Parse("5BFD00"), SKColor.Parse("003700") };
    public SKColor[] Border { get; set; } = { SKColor.Parse("1E8500"), SKColor.Parse("5BFD00") };
    public EIconLayoutCategory Category { get; set; } = EIconLayoutCategory.Item;
    public string ExportType { get; set; } = string.Empty;

    public static LayoutRenderContext FromCreator(UCreator creator, string exportType)
    {
        return new LayoutRenderContext
        {
            DisplayName = creator.DisplayName ?? string.Empty,
            Description = creator.Description ?? string.Empty,
            Preview = creator.Preview ?? creator.DefaultPreview,
            Background = creator.Background,
            Border = creator.Border,
            ExportType = exportType ?? string.Empty,
            Category = IconLayoutCategoryMap.From(exportType)
        };
    }

    /// <summary>編集プレビューが現在のアセットを取得できない場合のフォールバック用サンプル。</summary>
    public static LayoutRenderContext Sample()
    {
        SKBitmap preview = null;
        try
        {
            preview = SKBitmap.Decode(Application.GetResourceStream(
                new Uri("pack://application:,,,/Resources/T_Placeholder_Item_Image.png"))?.Stream);
        }
        catch { /* リソース取得不可（デザイナ等）の場合は画像なし */ }

        return new LayoutRenderContext
        {
            DisplayName = "サンプル名 / SAMPLE",
            Description = "ここに説明文が入ります。This is a sample description used for the layout preview.",
            Preview = preview,
            Background = new[] { SKColor.Parse("5BFD00"), SKColor.Parse("003700") },
            Border = new[] { SKColor.Parse("1E8500"), SKColor.Parse("5BFD00") },
            Category = EIconLayoutCategory.Cosmetic
        };
    }
}

/// <summary>直近に生成したアイコンの描画コンテキストを保持（編集ウインドウのプレビュー用）。</summary>
public static class IconLayoutPreview
{
    public static LayoutRenderContext Current { get; private set; }

    public static void Set(LayoutRenderContext ctx)
    {
        if (ctx != null) Current = ctx;
    }
}
