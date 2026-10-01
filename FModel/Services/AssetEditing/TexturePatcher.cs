using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using CommunityToolkit.HighPerformance;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Readers;
using Serilog;
using SkiaSharp;
using BcPixelFormat = BCnEncoder.Encoder.PixelFormat;

namespace FModel.Services.AssetEditing;

/// <summary>
/// replaces the pixels of a cooked 2D texture with an image
/// the image is encoded exactly like the texture already is (same size, same pixel format, same mips), so every mip's payload
/// keeps its size and is overwritten where it sits (.uexp, IoStore export data, .ubulk, .uptnl): no header, offset or table changes
/// </summary>
public static class TexturePatcher
{
    public sealed class Result
    {
        public string TextureName;
        /// <summary>the bytes written for each mip, compared against what the written package reads back</summary>
        public List<byte[]> Mips = [];
    }

    private static readonly MethodInfo _getBulkArchive = typeof(TBulkData<byte>).GetMethod("GetBulkArchive", BindingFlags.Instance | BindingFlags.NonPublic);

    public static Result Apply(EditedAssetWriter.SourcePackage package, IFileProvider provider, IPackage source, GameFile entry, string imagePath, AssetEditReport report)
    {
        var textures = source.GetExports().OfType<UTexture2D>().ToList();
        var texture = textures.FirstOrDefault(t => t.Name.Equals(entry.NameWithoutExtension, StringComparison.OrdinalIgnoreCase)) ?? textures.FirstOrDefault();
        if (texture == null)
        {
            report.Error(null, "このパッケージには差し替えられる 2D テクスチャがありません（キューブマップ・テクスチャ配列・ボリュームテクスチャは非対応）");
            return null;
        }

        var platformData = texture.PlatformData;
        if (platformData.VTData != null)
        {
            report.Error(texture.Name, "バーチャルテクスチャ（VT）には対応していません");
            return null;
        }
        if (platformData.Mips.Length == 0 || platformData.Mips.Any(m => m.SizeZ > 1) || platformData.GetNumSlices() > 1)
        {
            report.Error(texture.Name, "ミップを持つ通常の 2D テクスチャのみ対応しています");
            return null;
        }
        if (!CanEncode(texture.Format))
        {
            report.Error(texture.Name, $"ピクセル形式 {texture.Format} の書き出しには対応していません");
            return null;
        }

        using var image = Load(imagePath, report);
        if (image == null) return null;

        var top = platformData.Mips[0];
        if (image.Width != top.SizeX || image.Height != top.SizeY)
            report.Warn(texture.Name, $"画像を {image.Width}x{image.Height} から元のサイズ {top.SizeX}x{top.SizeY} に縮小・拡大しました（解像度は変更できません）");

        var result = new Result { TextureName = texture.Name };
        for (var i = 0; i < platformData.Mips.Length; i++)
        {
            var mip = platformData.Mips[i];
            var path = $"{texture.Name}.Mip[{i}] ({mip.SizeX}x{mip.SizeY})";
            if (mip.BulkData is not { } bulk || bulk.Header.SizeOnDisk == 0)
            {
                report.Error(path, "ミップのデータが見つかりません");
                return null;
            }
            if (bulk.BulkDataFlags.HasFlag(EBulkDataFlags.BULKDATA_SerializeCompressedZLIB) || bulk.BulkDataFlags.HasFlag(EBulkDataFlags.BULKDATA_CompressedLZO))
            {
                report.Error(path, "圧縮されたバルクデータには対応していません");
                return null;
            }

            var original = bulk.ReadDataOnce(false);
            if (original == null)
            {
                Log.Warning("Texture mip {Path} could not be read ({Flags})", path, bulk.BulkDataFlags);
                report.Error(path, bulk.BulkDataFlags.HasFlag(EBulkDataFlags.BULKDATA_OptionalPayload)
                    ? "このミップはオプションデータ（.uptnl / オンデマンド配信）にあり、読み込めないため差し替えできません"
                    : "このミップのデータを読み込めないため差し替えできません");
                return null;
            }
            if (!TryLocate(package, entry, bulk, original, out var target))
            {
                report.Error(path, "ミップのデータがパッケージ内のどこにあるか特定できませんでした（データが配信されていない可能性があります）");
                return null;
            }

            using var resized = Resize(image, mip.SizeX, mip.SizeY);
            var encoded = Encode(resized, texture.Format);
            if (encoded.Length != original.Length)
            {
                report.Error(path, $"エンコード後のサイズ（{encoded.Length} バイト）が元（{original.Length} バイト）と一致しません");
                return null;
            }

            encoded.CopyTo(target);
            result.Mips.Add(encoded);
        }

        // IoStore: the export data changed, the layout UAssetAPI reads has to be written again from it
        if (package.Model != null)
        {
            package.Header = package.Model.Write(UAssetApiSupport.ToEngineVersion(provider.Versions.Game), false, out var exports);
            package.Exports = exports;
        }

        report.Change(texture.Name, $"画像を {System.IO.Path.GetFileName(imagePath)} に差し替え（{texture.Format}, {top.SizeX}x{top.SizeY}, ミップ {platformData.Mips.Length} 枚）");
        return result;
    }

    /// <summary>
    /// finds the bytes CUE4Parse read the mip from in the files that will be written, checking they're the same bytes
    /// </summary>
    private static bool TryLocate(EditedAssetWriter.SourcePackage package, GameFile entry, TBulkData<byte> bulk, byte[] original, out Span<byte> target)
    {
        target = default;
        var args = new object[] { null, 0L };
        if (_getBulkArchive?.Invoke(bulk, args) is not true || args[0] is not FAssetArchive archive)
        {
            Log.Warning("Texture mip has no archive to read from ({Flags}, offset {Offset}, {Size} bytes)", bulk.BulkDataFlags, bulk.Header.OffsetInFile, bulk.Header.SizeOnDisk);
            return false;
        }
        var position = (long) args[1];

        var extension = EditedAssetWriter.PayloadExtension(entry, archive.Name);
        byte[] file = null;
        if (package.Model != null && extension is ".uasset" or ".umap")
        {
            // inline in the IoStore package: find the export it belongs to
            for (var i = 0; i < package.Model.Exports.Count; i++)
            {
                var start = package.ZenExportOffsets[i];
                var data = package.Model.Exports[i].Data;
                if (position < start || position + original.Length > start + data.Length) continue;
                file = data;
                position -= start;
                break;
            }
        }
        else if (package.Model == null && extension == ".uexp") file = package.Exports;
        else if (package.Model == null && extension is ".uasset" or ".umap") file = package.Header;
        else package.Payloads.TryGetValue(extension, out file);

        bool Matches(long at) => file != null && at >= 0 && at + original.Length <= file.Length && file.AsSpan((int) at, original.Length).SequenceEqual(original);

        // separate payloads may be handed out as a slice holding just this bulk data, then it sits at its offset in the whole file
        if (!Matches(position) && Matches(bulk.Header.OffsetInFile)) position = bulk.Header.OffsetInFile;
        if (!Matches(position))
        {
            Log.Warning("Texture mip not found where CUE4Parse read it: {Archive} @{Position}/{Offset} ({Size} bytes, {Flags}), resolved to {Extension} ({Length} bytes)",
                archive.Name, position, bulk.Header.OffsetInFile, original.Length, bulk.BulkDataFlags, extension, file?.Length ?? -1);
            return false;
        }
        var span = file.AsSpan((int) position, original.Length);

        target = span;
        return true;
    }

    #region image

    private static SKBitmap Load(string path, AssetEditReport report)
    {
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec == null)
            {
                report.Error(null, "画像を読み込めませんでした（PNG / JPEG / BMP / WebP に対応）");
                return null;
            }
            var info = codec.Info.WithColorType(SKColorType.Rgba8888).WithAlphaType(SKAlphaType.Unpremul);
            return SKBitmap.Decode(codec, info);
        }
        catch (Exception e)
        {
            report.Error(null, $"画像を読み込めませんでした: {e.Message}");
            return null;
        }
    }

    private static SKBitmap Resize(SKBitmap image, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        if (image.Width == width && image.Height == height) return image.Copy();
        return image.Resize(info, SKFilterQuality.High) ?? throw new InvalidOperationException("画像の縮小に失敗しました");
    }

    #endregion

    #region encoding

    private static bool CanEncode(EPixelFormat format) => format is
        EPixelFormat.PF_DXT1 or EPixelFormat.PF_DXT3 or EPixelFormat.PF_DXT5 or EPixelFormat.PF_BC4 or EPixelFormat.PF_BC5 or
        EPixelFormat.PF_BC6H or EPixelFormat.PF_BC7 or EPixelFormat.PF_B8G8R8A8 or EPixelFormat.PF_R8G8B8A8 or
        EPixelFormat.PF_G8 or EPixelFormat.PF_L8 or EPixelFormat.PF_A8 or EPixelFormat.PF_R8G8 or EPixelFormat.PF_G16 or EPixelFormat.PF_FloatRGBA;

    /// <param name="image">RGBA8888, unpremultiplied, already at the mip's size</param>
    private static byte[] Encode(SKBitmap image, EPixelFormat format)
    {
        var rgba = image.GetPixelSpan();
        int width = image.Width, height = image.Height, count = width * height;
        switch (format)
        {
            case EPixelFormat.PF_DXT1: return Bc(rgba, width, height, CompressionFormat.Bc1);
            case EPixelFormat.PF_DXT3: return Bc(rgba, width, height, CompressionFormat.Bc2);
            case EPixelFormat.PF_DXT5: return Bc(rgba, width, height, CompressionFormat.Bc3);
            case EPixelFormat.PF_BC4: return Bc(rgba, width, height, CompressionFormat.Bc4);
            case EPixelFormat.PF_BC5: return Bc(rgba, width, height, CompressionFormat.Bc5);
            case EPixelFormat.PF_BC7: return Bc(rgba, width, height, CompressionFormat.Bc7);
            case EPixelFormat.PF_BC6H:
            {
                var colors = new ColorRgbFloat[count];
                for (var i = 0; i < count; i++)
                    colors[i] = new ColorRgbFloat(rgba[i * 4] / 255f, rgba[i * 4 + 1] / 255f, rgba[i * 4 + 2] / 255f);
                return Encoder(CompressionFormat.Bc6U).EncodeToRawBytesHdr(new ReadOnlyMemory2D<ColorRgbFloat>(colors, height, width), 0, out _, out _);
            }
            case EPixelFormat.PF_R8G8B8A8:
                return rgba.ToArray();
            case EPixelFormat.PF_B8G8R8A8:
            {
                var bgra = rgba.ToArray();
                for (var i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
                return bgra;
            }
            case EPixelFormat.PF_G8 or EPixelFormat.PF_L8:
                return Channel(rgba, count, 0);
            case EPixelFormat.PF_A8:
                return Channel(rgba, count, 3);
            case EPixelFormat.PF_R8G8:
            {
                var rg = new byte[count * 2];
                for (var i = 0; i < count; i++)
                {
                    rg[i * 2] = rgba[i * 4];
                    rg[i * 2 + 1] = rgba[i * 4 + 1];
                }
                return rg;
            }
            case EPixelFormat.PF_G16:
            {
                var gray = new byte[count * 2];
                for (var i = 0; i < count; i++) BitConverter.TryWriteBytes(gray.AsSpan(i * 2), (ushort) (rgba[i * 4] * 257));
                return gray;
            }
            case EPixelFormat.PF_FloatRGBA:
            {
                var half = new byte[count * 8];
                var values = MemoryMarshal.Cast<byte, System.Half>(half.AsSpan());
                for (var i = 0; i < count * 4; i++) values[i] = (System.Half) (rgba[i] / 255f);
                return half;
            }
            default:
                throw new NotSupportedException(format.ToString());
        }
    }

    private static byte[] Channel(ReadOnlySpan<byte> rgba, int count, int channel)
    {
        var result = new byte[count];
        for (var i = 0; i < count; i++) result[i] = rgba[i * 4 + channel];
        return result;
    }

    private static byte[] Bc(ReadOnlySpan<byte> rgba, int width, int height, CompressionFormat format)
        => Encoder(format).EncodeToRawBytes(rgba, width, height, BcPixelFormat.Rgba32, 0, out _, out _);

    private static BcEncoder Encoder(CompressionFormat format)
    {
        var encoder = new BcEncoder(format);
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Quality = CompressionQuality.Balanced;
        encoder.Options.IsParallel = true;
        return encoder;
    }

    #endregion
}
