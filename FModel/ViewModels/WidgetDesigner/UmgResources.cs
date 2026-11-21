using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine.Font;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Textures;
using FModel.Framework;
using FModel.Settings;
using Serilog;
using SkiaSharp;

namespace FModel.ViewModels.WidgetDesigner;

/// <summary>Decoded brush images, tinted the way Slate multiplies the brush color with the texture.</summary>
public sealed class UmgImageCache(CancellationToken cancellationToken)
{
    private const int _MAX_SIZE = 2048;

    private readonly Dictionary<string, SKBitmap> _bitmaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, Color), ImageSource> _images = new();

    /// <summary>Null when <paramref name="resource"/> isn't something drawable here (a material, a font...).</summary>
    public ImageSource Get(UObject resource, Color tint)
    {
        var path = resource.GetPathName();
        if (_images.TryGetValue((path, tint), out var cached)) return cached;

        ImageSource image = null;
        try
        {
            var bitmap = GetBitmap(resource, path);
            if (bitmap != null) image = Tint(bitmap, tint).ToBitmapSource();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Debug(e, "Failed to decode the brush image {Path}", path);
        }

        _images[(path, tint)] = image;
        return image;
    }

    private SKBitmap GetBitmap(UObject resource, string path)
    {
        if (_bitmaps.TryGetValue(path, out var cached)) return cached;
        cancellationToken.ThrowIfCancellationRequested();

        SKBitmap bitmap = null;
        switch (resource)
        {
            case UTexture texture:
                bitmap = texture.Decode(_MAX_SIZE, UserSettings.GetExportOptions(), cancellationToken)?.ToSkBitmap();
                break;
            case not null when resource.ExportType == "PaperSprite":
                bitmap = DecodeSprite(resource);
                break;
        }

        _bitmaps[path] = bitmap;
        return bitmap;
    }

    /// <summary>The part of the sprite's texture it uses (baked atlas first, then the source texture).</summary>
    private SKBitmap DecodeSprite(UObject sprite)
    {
        var texture = sprite.GetOrDefault<UTexture>("BakedSourceTexture") ?? sprite.GetOrDefault<UTexture>("SourceTexture");
        if (texture == null) return null;
        var full = texture.Decode(UserSettings.GetExportOptions(), cancellationToken)?.ToSkBitmap();
        if (full == null) return null;

        var baked = sprite.GetOrDefault<UTexture>("BakedSourceTexture") != null;
        var uv = Vec(sprite, baked ? "BakedSourceUV" : "SourceUV");
        var dimension = Vec(sprite, baked ? "BakedSourceDimension" : "SourceDimension");
        if (dimension.X <= 0 || dimension.Y <= 0) return full;

        // the decoded texture may be a smaller mip than the one the coordinates are expressed in
        var scale = texture is UTexture2D { PlatformData.SizeX: > 0 } t2d ? (double) full.Width / t2d.PlatformData.SizeX : 1;
        var rect = SKRectI.Create((int) (uv.X * scale), (int) (uv.Y * scale), Math.Max(1, (int) (dimension.X * scale)), Math.Max(1, (int) (dimension.Y * scale)));
        rect.Intersect(new SKRectI(0, 0, full.Width, full.Height));
        if (rect.IsEmpty) return full;

        var cropped = new SKBitmap(rect.Width, rect.Height, full.ColorType, full.AlphaType);
        using (var canvas = new SKCanvas(cropped))
            canvas.DrawBitmap(full, rect, new SKRect(0, 0, rect.Width, rect.Height));
        full.Dispose();
        return cropped;
    }

    private static Vector Vec(UObject obj, string name) =>
        obj.TryGetValue(out FVector2D v, name) ? new Vector(v.X, v.Y) : default;

    private static SKBitmap Tint(SKBitmap source, Color tint)
    {
        if (tint == Colors.White) return source;

        var tinted = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(tinted);
        using var paint = new SKPaint();
        paint.ColorFilter = SKColorFilter.CreateColorMatrix(
        [
            tint.R / 255f, 0, 0, 0, 0,
            0, tint.G / 255f, 0, 0, 0,
            0, 0, tint.B / 255f, 0, 0,
            0, 0, 0, tint.A / 255f, 0
        ]);
        canvas.DrawBitmap(source, 0, 0, paint);
        return tinted;
    }
}

/// <summary>
/// Game fonts (UFont composite fonts → UFontFace → .ufont data) written to the temp folder so WPF can draw with them.
/// Shared by every window: the files and families stay valid for the session.
/// </summary>
public sealed class UmgFontCache(IFileProvider provider)
{
    public static readonly FontFamily DefaultFamily = new("Roboto, Segoe UI, Yu Gothic UI");
    private const string _FALLBACK = ", Segoe UI, Yu Gothic UI, Microsoft YaHei UI, Malgun Gothic";

    private static readonly ConcurrentDictionary<string, (FontFamily Family, FontWeight Weight, FontStyle Style)?> _faces = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string _directory = Path.Combine(Path.GetTempPath(), "FModel", "UmgFonts");

    public void Apply(UmgFont font, UObject fontObject, string typeface)
    {
        font.Family = DefaultFamily;
        font.Description = fontObject == null ? "Roboto (default)" : $"{fontObject.Name} {typeface}".Trim();
        if (typeface?.Contains("Bold", StringComparison.OrdinalIgnoreCase) == true) font.Weight = FontWeights.Bold;
        if (typeface?.Contains("Italic", StringComparison.OrdinalIgnoreCase) == true) font.Style = FontStyles.Italic;
        if (fontObject == null) return;

        try
        {
            var face = FindFace(fontObject, typeface);
            if (face == null) return;
            if (Resolve(face) is not { } resolved) return;

            font.Family = resolved.Family;
            font.Weight = resolved.Weight;
            font.Style = resolved.Style;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Debug(e, "Failed to load the font {Font} {Typeface}", fontObject.Name, typeface);
        }
    }

    /// <summary>The font face of the composite font's default typeface named <paramref name="typeface"/> (or its first one).</summary>
    private static UObject FindFace(UObject fontObject, string typeface)
    {
        if (fontObject is UFontFace || fontObject.ExportType == "FontFace") return fontObject;

        var composite = fontObject.GetOrDefault<FStructFallback>("CompositeFont");
        var fonts = composite?.GetOrDefault<FStructFallback>("DefaultTypeface")?.GetOrDefault<FStructFallback[]>("Fonts");
        if (fonts is not { Length: > 0 }) return null;

        var entry = fonts.FirstOrDefault(f => f.GetOrDefault<FName>("Name").Text.Equals(typeface, StringComparison.OrdinalIgnoreCase)) ?? fonts[0];
        // FFontData is read natively, a fallback struct only when the version isn't known to CUE4Parse
        if (entry.GetOrDefault<FFontData>("Font") is { LocalFontFaceAsset: { IsNull: false } index })
            return index.TryLoad(out var face) ? face as UObject : null;
        return entry.GetOrDefault<FStructFallback>("Font")?.GetOrDefault<UObject>("LocalFontFaceAsset");
    }

    private (FontFamily Family, FontWeight Weight, FontStyle Style)? Resolve(UObject face)
    {
        var path = face.GetPathName();
        return _faces.GetOrAdd(path, _ =>
        {
            var data = (face as UFontFace)?.FontFaceData?.Data;
            if (data is not { Length: > 0 })
            {
                var package = face.Owner?.Name;
                if (package != null && provider.TryGetGameFile(package + ".ufont", out var file)) data = file.Read();
            }

            data = Unwrap(data);
            if (data is not { Length: > 4 }) return null;
            return Install(path, data);
        });
    }

    /// <summary>
    /// The font file itself: newer .ufont files hold the serialized FFontFaceData, the bytes behind an int32 count
    /// (and some trailing fields), which WPF can't read.
    /// </summary>
    private static byte[] Unwrap(byte[] data)
    {
        if (data is not { Length: > 8 } || IsFont(data, 0)) return data;
        var count = BitConverter.ToInt32(data, 0);
        return count > 4 && count <= data.Length - 4 && IsFont(data, 4) ? data.AsSpan(4, count).ToArray() : data;
    }

    private static bool IsFont(byte[] data, int offset) =>
        data.Length >= offset + 4 && (
            data[offset] == 0 && data[offset + 1] == 1 && data[offset + 2] == 0 && data[offset + 3] == 0 || // TrueType
            data[offset] == 'O' && data[offset + 1] == 'T' && data[offset + 2] == 'T' && data[offset + 3] == 'O' ||
            data[offset] == 't' && data[offset + 1] == 'r' && data[offset + 2] == 'u' && data[offset + 3] == 'e' ||
            data[offset] == 't' && data[offset + 1] == 't' && data[offset + 2] == 'c' && data[offset + 3] == 'f');

    private static (FontFamily, FontWeight, FontStyle)? Install(string path, byte[] data)
    {
        Directory.CreateDirectory(_directory);
        var isOpenType = data[0] == 'O' && data[1] == 'T' && data[2] == 'T' && data[3] == 'O';
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path)))[..12];
        var name = new string(path.SubstringAfterLast('/').Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        var file = Path.Combine(_directory, $"{name}_{hash}{(isOpenType ? ".otf" : ".ttf")}");
        if (!File.Exists(file) || new FileInfo(file).Length != data.Length)
            File.WriteAllBytes(file, data);

        var glyphs = new GlyphTypeface(new Uri(file));
        // WPF matches the typographic family (name ID 16, else 1), the face is then chosen by weight and style
        var familyName = glyphs.FamilyNames.TryGetValue(CultureInfo.GetCultureInfo("en-US"), out var english)
            ? english
            : glyphs.FamilyNames.Values.FirstOrDefault() ?? name;

        // every font of the folder whose family matches is a candidate, weight and style then pick the face
        var family = new FontFamily(new Uri(_directory + Path.DirectorySeparatorChar), "./#" + familyName + _FALLBACK);
        return (family, glyphs.Weight, glyphs.Style);
    }
}

file static class StringExtensions
{
    public static string SubstringAfterLast(this string s, char c)
    {
        var index = s.LastIndexOf(c);
        return index < 0 ? s : s[(index + 1)..];
    }
}
