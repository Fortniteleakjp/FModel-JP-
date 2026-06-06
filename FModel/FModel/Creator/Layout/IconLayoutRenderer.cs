using System;
using System.Collections.Generic;
using System.IO;
using FModel.Framework;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace FModel.Creator.Layout;

/// <summary>
/// テンプレート(配置/サイズ/色/縁取り) + 描画コンテキスト(名前/説明/画像/レア度色) から
/// 1枚のアイコン画像を描く。Creator の描画と編集ウインドウのプレビューの両方で使う。
/// </summary>
public static class IconLayoutRenderer
{
    private enum TextKind { Name, Description }

    public static SKBitmap Render(IconLayoutTemplate template, LayoutRenderContext ctx)
    {
        template = (template ?? new IconLayoutTemplate()).EnsureComplete();
        ctx ??= LayoutRenderContext.Sample();

        var w = Math.Clamp(template.Width, 16, 4096);
        var h = Math.Clamp(template.Height, 16, 4096);

        // BGRA(Premul) で出力すると WPF の Pbgra32(WriteableBitmap) へ無変換でコピーでき、
        // 編集プレビューの再描画が軽くなる。PNGエンコード経路でも問題なく扱える。
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.Transparent);

        DrawBackground(c, template, ctx, w, h);
        DrawPreviewImage(c, template.Preview, ctx.Preview);
        DrawText(c, template.Name, ctx.DisplayName, TextKind.Name, GetTypeface(true));
        DrawText(c, template.Description, ctx.Description, TextKind.Description, GetTypeface(false));

        return bmp;
    }

    #region background

    private static void DrawBackground(SKCanvas c, IconLayoutTemplate t, LayoutRenderContext ctx, int w, int h)
    {
        switch (t.BackgroundMode)
        {
            case EIconLayoutBackground.SolidColor:
                c.DrawRect(new SKRect(0, 0, w, h), new SKPaint { Color = LayoutColor.Parse(t.BackgroundColor, SKColors.Black) });
                break;
            case EIconLayoutBackground.Image:
            {
                var bg = LoadBackgroundImage(t.BackgroundImagePath);
                if (bg != null)
                    c.DrawBitmap(bg, new SKRect(0, 0, w, h), new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High });
                else
                    c.DrawRect(new SKRect(0, 0, w, h), new SKPaint { Color = LayoutColor.Parse(t.BackgroundColor, SKColors.Black) });
                break;
            }
            default:
                DrawRarityBackground(c, ctx, w, h);
                break;
        }
    }

    private static void DrawRarityBackground(SKCanvas c, LayoutRenderContext ctx, int w, int h)
    {
        var bg = (ctx.Background is { Length: >= 2 }) ? (SKColor[]) ctx.Background.Clone() : new[] { SKColor.Parse("5BFD00"), SKColor.Parse("003700") };
        var border = (ctx.Border is { Length: >= 2 }) ? ctx.Border : bg;

        if (bg[0] == bg[1]) bg[0] = border[0];
        bg[0].ToHsl(out _, out _, out var l1);
        bg[1].ToHsl(out _, out _, out var l2);
        var reverse = l1 > l2;

        c.DrawRect(new SKRect(0, 0, w, h), new SKPaint
        {
            IsAntialias = true, FilterQuality = SKFilterQuality.High,
            Shader = SKShader.CreateLinearGradient(new SKPoint(w / 2f, h), new SKPoint(w, h / 4f), border, SKShaderTileMode.Clamp)
        });

        const int margin = 2;
        c.DrawRect(new SKRect(margin, margin, w - margin, h - margin), new SKPaint
        {
            IsAntialias = true, FilterQuality = SKFilterQuality.High,
            Shader = SKShader.CreateRadialGradient(new SKPoint(w / 2f, h / 2f), w / 5f * 4f,
                new[] { bg[reverse ? 0 : 1], bg[reverse ? 1 : 0] }, SKShaderTileMode.Clamp)
        });
    }

    private static readonly Dictionary<string, SKBitmap> _bgCache = new();

    private static SKBitmap LoadBackgroundImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        if (_bgCache.TryGetValue(path, out var cached)) return cached;
        try
        {
            using var s = File.OpenRead(path);
            var bmp = SKBitmap.Decode(s);
            if (bmp != null) _bgCache[path] = bmp;
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>背景画像ファイルを差し替えた際にキャッシュを破棄する。</summary>
    public static void InvalidateBackgroundCache()
    {
        foreach (var b in _bgCache.Values) b?.Dispose();
        _bgCache.Clear();
    }

    #endregion

    #region preview image

    private static void DrawPreviewImage(SKCanvas c, IconImageElement e, SKBitmap img)
    {
        if (e is not { Visible: true } || img == null) return;

        var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
        if (e.KeepAspect && img.Width > 0 && img.Height > 0)
        {
            var ratio = Math.Min(e.Width / img.Width, e.Height / img.Height);
            var dw = (float) (img.Width * ratio);
            var dh = (float) (img.Height * ratio);
            var dx = (float) (e.X + (e.Width - dw) / 2);
            var dy = (float) (e.Y + (e.Height - dh) / 2);
            c.DrawBitmap(img, new SKRect(dx, dy, dx + dw, dy + dh), paint);
        }
        else
        {
            c.DrawBitmap(img, new SKRect((float) e.X, (float) e.Y, (float) (e.X + e.Width), (float) (e.Y + e.Height)), paint);
        }
    }

    #endregion

    #region text

    private static void DrawText(SKCanvas c, IconTextElement e, string text, TextKind kind, SKTypeface typeface)
    {
        if (e is not { Visible: true } || string.IsNullOrWhiteSpace(text)) return;

        using var fill = new SKPaint
        {
            IsAntialias = true, FilterQuality = SKFilterQuality.High,
            Typeface = typeface, TextSize = (float) e.FontSize,
            Color = LayoutColor.Parse(e.Color, SKColors.White)
        };
        SKPaint stroke = null;
        if (e.OutlineThickness > 0)
            stroke = new SKPaint
            {
                IsAntialias = true, FilterQuality = SKFilterQuality.High,
                Typeface = typeface, TextSize = (float) e.FontSize,
                Color = LayoutColor.Parse(e.OutlineColor, SKColors.Black),
                Style = SKPaintStyle.Stroke, StrokeWidth = (float) e.OutlineThickness * 2f, StrokeJoin = SKStrokeJoin.Round
            };

        var maxWidth = (float) Math.Max(1, e.Width);
        var shaper = GetShaper(typeface);

        try
        {
            if (kind == TextKind.Name)
            {
                // 1行・幅に収まるよう自動縮小
                while (fill.TextSize > 6 && shaper.Shape(text, fill).Width > maxWidth)
                {
                    fill.TextSize -= 1;
                    if (stroke != null) stroke.TextSize = fill.TextSize;
                }

                var width = shaper.Shape(text, fill).Width;
                var x = AlignX((float) e.X, maxWidth, width, e.Align);
                var baseline = (float) e.Y - fill.FontMetrics.Ascent;
                if (stroke != null) c.DrawShapedText(shaper, text, x, baseline, stroke);
                c.DrawShapedText(shaper, text, x, baseline, fill);
            }
            else
            {
                var lines = Utils.SplitLines(text, fill, maxWidth) ?? new List<string>();
                var maxLines = Math.Max(1, e.MaxLines);
                var lineHeight = fill.TextSize * 1.2f;
                var y = (float) e.Y - fill.FontMetrics.Ascent;

                for (var i = 0; i < lines.Count && i < maxLines; i++)
                {
                    var line = lines[i]?.Trim();
                    if (string.IsNullOrEmpty(line)) { y += lineHeight; continue; }

                    var width = shaper.Shape(line, fill).Width;
                    var x = AlignX((float) e.X, maxWidth, width, e.Align);
                    if (stroke != null) c.DrawShapedText(shaper, line, x, y, stroke);
                    c.DrawShapedText(shaper, line, x, y, fill);
                    y += lineHeight;
                }
            }
        }
        finally
        {
            stroke?.Dispose();
        }
    }

    private static float AlignX(float left, float areaWidth, float textWidth, EIconLayoutAlign align) => align switch
    {
        EIconLayoutAlign.Center => left + (areaWidth - textWidth) / 2f,
        EIconLayoutAlign.Right => left + areaWidth - textWidth,
        _ => left
    };

    private static SKTypeface GetTypeface(bool name)
    {
        var tf = Utils.Typefaces;
        if (tf != null)
            return (name ? tf.DisplayName : tf.Description) ?? tf.Default ?? SKTypeface.Default;
        return SKTypeface.Default;
    }

    // CustomSKShaper の生成は HarfBuzz のフォント/フェイス確保を伴い高コストなので、
    // タイプフェイス単位でキャッシュしてドラッグ中の連続再描画を軽くする。
    // 一括書き出し等でワーカースレッドからも呼ばれ得るため、スレッドローカルにして競合を防ぐ。
    private static readonly System.Threading.ThreadLocal<Dictionary<SKTypeface, CustomSKShaper>> _shaperCache =
        new(() => new Dictionary<SKTypeface, CustomSKShaper>());

    private static CustomSKShaper GetShaper(SKTypeface typeface)
    {
        typeface ??= SKTypeface.Default;
        var cache = _shaperCache.Value;
        if (!cache.TryGetValue(typeface, out var shaper))
        {
            shaper = new CustomSKShaper(typeface);
            cache[typeface] = shaper;
        }
        return shaper;
    }

    #endregion
}

/// <summary>"#AARRGGBB" / "#RRGGBB" の相互変換（編集ウインドウと描画で共通使用）。</summary>
public static class LayoutColor
{
    public static SKColor Parse(string hex, SKColor fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var s = hex.TrimStart('#');
        try
        {
            switch (s.Length)
            {
                case 8:
                    return new SKColor(
                        Convert.ToByte(s.Substring(2, 2), 16),
                        Convert.ToByte(s.Substring(4, 2), 16),
                        Convert.ToByte(s.Substring(6, 2), 16),
                        Convert.ToByte(s.Substring(0, 2), 16));
                case 6:
                    return new SKColor(
                        Convert.ToByte(s.Substring(0, 2), 16),
                        Convert.ToByte(s.Substring(2, 2), 16),
                        Convert.ToByte(s.Substring(4, 2), 16));
            }
        }
        catch { /* 不正な文字列はフォールバック */ }
        return fallback;
    }

    public static string ToHex(byte a, byte r, byte g, byte b) => $"#{a:X2}{r:X2}{g:X2}{b:X2}";
}
