using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Readers;
using CUE4Parse.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using UAssetAPI;

namespace FModel.Services.AssetEditing;

/// <summary>
/// turns the json FModel shows for a package, edited by the user, back into a .uasset (+ .uexp/.ubulk)
/// the original package is read with UAssetAPI, only the edited values are changed, and the written files
/// are read back with CUE4Parse so the user knows whether the result shows what they typed
/// </summary>
public static class EditedAssetWriter
{
    public sealed class Request
    {
        public IFileProvider Provider { get; init; }
        public GameFile Entry { get; init; }
        /// <summary>the document as displayed and as edited, both null when only the texture is replaced</summary>
        public string OriginalJson { get; init; }
        public string EditedJson { get; init; }
        /// <summary>index of the first export displayed in the document (pagination)</summary>
        public int ExportStart { get; init; }
        /// <summary>an image to replace the package's texture with, see <see cref="TexturePatcher"/></summary>
        public string ReplacementImagePath { get; init; }
        /// <summary>an OBJ exported by <see cref="MeshPatcher.ExportObj"/> and reshaped, see <see cref="MeshPatcher"/></summary>
        public string ReplacementMeshPath { get; init; }
        public string OutputDirectory { get; init; }
    }

    public static AssetEditReport Write(Request request)
    {
        var report = new AssetEditReport();

        JArray original = null, edited = null;
        if (request.OriginalJson != null && request.EditedJson != null)
        {
            if (!TryParse(request.OriginalJson, out original, out var parseError))
            {
                report.Error(null, $"元の JSON を解析できません: {parseError}");
                return report;
            }
            if (!TryParse(request.EditedJson, out edited, out parseError))
            {
                report.Error(null, $"編集後の JSON が正しくありません: {parseError}");
                return report;
            }
        }

        var hasJsonEdits = original != null && !JToken.DeepEquals(original, edited);
        var hasImage = !string.IsNullOrEmpty(request.ReplacementImagePath);
        var hasMesh = !string.IsNullOrEmpty(request.ReplacementMeshPath);
        if (!hasJsonEdits && !hasImage && !hasMesh)
        {
            report.Error(null, "JSON に変更がありません");
            return report;
        }

        var provider = request.Provider;
        var source = provider.LoadPackage(request.Entry);
        var package = LoadSource(provider, request.Entry, source, report);
        if (package == null || report.HasErrors) return report;

        // pixels first: they keep their size, so the json edits below are applied on top of the patched bytes
        TexturePatcher.Result texture = null;
        if (hasImage)
        {
            try
            {
                texture = TexturePatcher.Apply(package, provider, source, request.Entry, request.ReplacementImagePath, report);
            }
            catch (Exception e)
            {
                report.Error(null, $"テクスチャを差し替えられませんでした: {e.Message}");
            }
            if (texture == null || report.HasErrors) return report;
        }

        MeshPatcher.Result mesh = null;
        if (hasMesh)
        {
            try
            {
                mesh = MeshPatcher.Apply(package, provider, source, request.Entry, request.ReplacementMeshPath, report);
            }
            catch (Exception e)
            {
                report.Error(null, $"メッシュを差し替えられませんでした: {e.Message}");
            }
            if (mesh == null || report.HasErrors) return report;
        }

        byte[] header, exports;
        if (hasJsonEdits)
        {
            if (!ApplyJson(package, provider, source, original, edited, request.ExportStart, report, out header, out exports)) return report;
        }
        else if (package.Model != null)
        {
            header = package.Model.Write(UAssetApiSupport.ToEngineVersion(provider.Versions.Game), provider.Versions.Game >= CUE4Parse.UE4.Versions.EGame.GAME_UE5_8, out exports);
        }
        else
        {
            header = package.Header;
            exports = package.Exports;
        }

        var written = new SourcePackage
        {
            Name = package.Name,
            Extension = package.Extension,
            Header = header,
            Exports = exports,
            Payloads = package.Payloads
        };

        Save(written, request, report);
        Verify(written, provider, hasJsonEdits ? original : null, edited, request.ExportStart, texture, mesh, report);
        return report;
    }

    private static bool ApplyJson(SourcePackage package, IFileProvider provider, IPackage source, JArray original, JArray edited, int exportStart,
        AssetEditReport report, out byte[] header, out byte[] exports)
    {
        header = exports = null;
        UAsset asset;
        try
        {
            asset = Read(package, provider, source);
        }
        catch (Exception e)
        {
            report.Error(null, $"UAssetAPI でパッケージを読み込めませんでした: {e.Message}");
            return false;
        }

        // editor (uncooked) packages carry offsets into their editor-only data that UAssetAPI doesn't keep in sync
        if ((asset.PackageFlags & (UAssetAPI.UnrealTypes.EPackageFlags.PKG_Cooked | UAssetAPI.UnrealTypes.EPackageFlags.PKG_FilterEditorOnly)) == 0)
        {
            report.Error(null, "クック前（エディタ用）のパッケージには対応していません");
            return false;
        }

        var unparsed = asset.Exports.Where(e => e is UAssetAPI.ExportTypes.RawExport).Select(e => e.ObjectName.ToString()).ToList();
        var unparsedList = $"{string.Join(", ", unparsed.Take(5))}{(unparsed.Count > 5 ? " ..." : "")}";
        if (!RoundTrips(asset, package))
        {
            // unparsed exports are copied as raw bytes, their names/indexes only stay valid if everything else is written back identically
            if (unparsed.Count > 0)
            {
                report.Error(null, $"UAssetAPI がこのパッケージを元どおりに書き戻せず、解析できないエクスポート（{unparsedList}）も含むため、書き出すと壊れる可能性があります");
                return false;
            }
            report.Warn(null, "UAssetAPI での読み書きが元のバイナリと完全一致しません。変更していない部分も再シリアライズされます");
        }
        else if (unparsed.Count > 0)
        {
            report.Warn(null, $"UAssetAPI が解析できなかったエクスポートがあります（そのまま書き戻します）: {unparsedList}");
        }

        var changesBefore = report.Changes.Count;
        var references = new PackageReferenceResolver(asset, source, provider);
        new AssetJsonPatcher(asset, references, report).Apply(original, edited, exportStart);
        if (report.HasErrors) return false;
        if (report.Changes.Count == changesBefore)
        {
            report.Error(null, "書き戻す変更が見つかりませんでした");
            return false;
        }

        byte[] data;
        try
        {
            data = asset.WriteData().ToArray();
        }
        catch (Exception e)
        {
            report.Error(null, $"uasset の書き出しに失敗しました: {e.Message}");
            return false;
        }

        if (package.Model != null)
        {
            header = LegacyPackageModel.FromUAsset(asset, data).Write(UAssetApiSupport.ToEngineVersion(provider.Versions.Game),
                provider.Versions.Game >= CUE4Parse.UE4.Versions.EGame.GAME_UE5_8, out exports);
        }
        else
        {
            var split = (int) asset.Exports[0].SerialOffset;
            header = data[..split];
            exports = data[split..];
        }
        return true;
    }

    #region source

    /// <summary>
    /// a cooked package split the way the legacy loader reads it
    /// </summary>
    public sealed class SourcePackage
    {
        /// <summary>package name CUE4Parse gives it, used to read it back with the same object paths</summary>
        public string Name { get; init; }
        /// <summary>".uasset" or ".umap"</summary>
        public string Extension { get; init; }
        public byte[] Header { get; set; }
        public byte[] Exports { get; set; }
        /// <summary>.ubulk/.uptnl/... keyed by extension, written unchanged</summary>
        public Dictionary<string, byte[]> Payloads { get; init; } = [];
        /// <summary>
        /// set when the header was rebuilt from IoStore: it's written in the layout UAssetAPI reads,
        /// the output is written again from the edited tables in the engine's own layout
        /// </summary>
        public LegacyPackageModel Model { get; init; }
        /// <summary>
        /// IoStore only: where each export's data starts in the original zen package, positions CUE4Parse reports are relative to it
        /// </summary>
        public int[] ZenExportOffsets { get; init; }
    }

    private static SourcePackage LoadSource(IFileProvider provider, GameFile entry, IPackage source, AssetEditReport report)
    {
        if (source is IoPackage io)
        {
            try
            {
                return ZenToLegacyConverter.Convert(io, provider, entry, report);
            }
            catch (Exception e)
            {
                report.Error(null, $"IoStore パッケージをレガシー形式へ変換できませんでした: {e.Message}");
                return null;
            }
        }

        var files = provider.SavePackage(entry);
        byte[] header = null, exports = null;
        var payloads = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, bytes) in files)
        {
            var extension = PayloadExtension(entry, path);
            switch (extension)
            {
                case ".uasset" or ".umap":
                    header = bytes;
                    break;
                case ".uexp":
                    exports = bytes;
                    break;
                default:
                    payloads[extension] = bytes;
                    break;
            }
        }

        if (header == null)
        {
            report.Error(null, "パッケージ本体を読み込めませんでした");
            return null;
        }

        return new SourcePackage
        {
            Name = source.Name,
            Extension = "." + entry.Extension,
            Header = header,
            Exports = exports ?? [],
            Payloads = payloads
        };
    }

    /// <summary>
    /// ".uexp", ".ubulk" but also ".m.ubulk"/".o.uasset": everything after the package's own name
    /// </summary>
    internal static string PayloadExtension(GameFile entry, string path)
    {
        var name = entry.NameWithoutExtension;
        var fileName = path.SubstringAfterLast('/');
        return (fileName.StartsWith(name, StringComparison.OrdinalIgnoreCase) && fileName.Length > name.Length
            ? fileName[name.Length..]
            : "." + fileName.SubstringAfterLast('.')).ToLowerInvariant();
    }

    private static UAsset Read(SourcePackage package, IFileProvider provider, IPackage source)
    {
        var mappings = UAssetApiSupport.GetMappings(provider.MappingsForGame);
        BlueprintSchemas.Add(mappings, source);

        var asset = new UAsset(UAssetApiSupport.ToEngineVersion(provider.Versions.Game), mappings)
        {
            UseSeparateBulkDataFiles = package.Exports.Length > 0
        };

        using var stream = new MemoryStream(package.Header.Length + package.Exports.Length);
        stream.Write(package.Header);
        stream.Write(package.Exports);
        stream.Position = 0;
        asset.Read(new UAssetApiSupport.SafeAssetBinaryReader(stream, asset));
        return asset;
    }

    private static bool RoundTrips(UAsset asset, SourcePackage package)
    {
        try
        {
            var data = asset.WriteData().ToArray();
            if (package.Model != null)
            {
                // the header gets rebuilt anyway, what matters is that every export serializes back to the same bytes
                var exports = LegacyPackageModel.FromUAsset(asset, data).Exports;
                return exports.Count == package.Model.Exports.Count &&
                       exports.Zip(package.Model.Exports).All(pair => pair.First.Data.AsSpan().SequenceEqual(pair.Second.Data));
            }

            return data.Length == package.Header.Length + package.Exports.Length &&
                   data.AsSpan(0, package.Header.Length).SequenceEqual(package.Header) &&
                   data.AsSpan(package.Header.Length).SequenceEqual(package.Exports);
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region output

    private static void Save(SourcePackage package, Request request, AssetEditReport report)
    {
        var relative = request.Entry.PathWithoutExtension.Replace('\\', '/').TrimStart('/');
        var basePath = Path.Combine(request.OutputDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);

        void WriteFile(string extension, byte[] bytes)
        {
            var path = basePath + extension;
            File.WriteAllBytes(path, bytes);
            report.OutputFiles.Add(path);
        }

        WriteFile(package.Extension, package.Header);
        if (package.Exports.Length > 0) WriteFile(".uexp", package.Exports);
        foreach (var (extension, bytes) in package.Payloads) WriteFile(extension, bytes);
    }

    #endregion

    #region verification

    /// <param name="original">null when the json wasn't edited</param>
    /// <param name="texture">null when no image was replaced</param>
    private static void Verify(SourcePackage package, IFileProvider provider, JArray original, JArray edited, int exportStart, TexturePatcher.Result texture,
        MeshPatcher.Result mesh, AssetEditReport report)
    {
        try
        {
            var versions = provider.Versions;
            FArchive Payload(string extension) => package.Payloads.TryGetValue(extension, out var bytes)
                ? new FByteArchive(package.Name + extension, bytes, versions)
                : null;

            IPackage reloaded = new Package(
                new FByteArchive(package.Name + package.Extension, package.Header, versions),
                package.Exports.Length > 0 ? new FByteArchive(package.Name + ".uexp", package.Exports, versions) : null,
                Payload(".ubulk"), Payload(".uptnl"), provider, false);

            if (original != null)
            {
                var exports = reloaded.GetExports(exportStart, Math.Min(edited.Count, reloaded.ExportMapLength - exportStart));
                var actual = JArray.Parse(JsonConvert.SerializeObject(exports));
                Compare(NormalizePaths(actual, provider), NormalizePaths(original.DeepClone(), provider), NormalizePaths(edited.DeepClone(), provider), "", report);
            }

            if (texture != null)
            {
                var reloadedTexture = reloaded.GetExports().OfType<CUE4Parse.UE4.Assets.Exports.Texture.UTexture2D>().FirstOrDefault(t => t.Name == texture.TextureName);
                var mips = reloadedTexture?.PlatformData.Mips;
                if (mips == null || mips.Length != texture.Mips.Count)
                {
                    report.VerificationMismatches.Add($"{texture.TextureName}: 書き出したテクスチャを読み直せませんでした");
                }
                else
                {
                    for (var i = 0; i < mips.Length; i++)
                    {
                        if (mips[i].BulkData?.ReadDataOnce(false) is { } data && data.AsSpan().SequenceEqual(texture.Mips[i])) continue;
                        report.VerificationMismatches.Add($"{texture.TextureName}.Mip[{i}]: 読み直した画素データが書き込んだものと一致しません");
                    }
                    report.ReloadedTexture = reloadedTexture;
                }
            }

            if (mesh != null)
            {
                var reloadedMesh = MeshPatcher.FindMesh(reloaded, mesh.MeshName, out var lods, out var error);
                if (reloadedMesh == null)
                {
                    report.VerificationMismatches.Add($"{mesh.MeshName}: 書き出したメッシュを読み直せませんでした（{error}）");
                }
                else
                {
                    foreach (var (index, expected) in mesh.Positions)
                    {
                        var lod = lods.FirstOrDefault(l => l.Index == index);
                        if (lod != null && lod.Positions.AsSpan().SequenceEqual(expected)) continue;
                        report.VerificationMismatches.Add($"{mesh.MeshName}.LOD{index}: 読み直した頂点位置が書き込んだものと一致しません");
                    }
                    report.ReloadedMesh = reloadedMesh;
                }
            }

            report.Verified = report.VerificationMismatches.Count == 0;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not read back the edited {Package}", package.Name);
            report.Warn(null, $"書き出したファイルを CUE4Parse で読み直せませんでした: {e.Message}");
        }
    }

    /// <summary>
    /// the same package is named "/Game/Foo" when loaded from IoStore and "Game/Content/Foo" when loaded from a legacy file
    /// </summary>
    private static JToken NormalizePaths(JToken token, IFileProvider provider)
    {
        if (token is not JContainer container) return token;
        foreach (var property in container.DescendantsAndSelf().OfType<JProperty>().ToList())
        {
            if (property.Name != "ObjectPath" || property.Value.Type != JTokenType.String) continue;

            var path = property.Value.ToString();
            var dot = path.LastIndexOf('.');
            if (dot <= 0 || !int.TryParse(path[(dot + 1)..], out _)) continue;
            try
            {
                property.Value = provider.FixPath(path[..dot]).SubstringBeforeLast('.').ToLowerInvariant() + path[dot..];
            }
            catch
            {
                // leave it as is
            }
        }
        return token;
    }

    /// <summary>
    /// what the user changed must read back as typed, what they didn't change should read back as before
    /// (derived values such as a quaternion's Size legitimately follow the edit, so those are only reported)
    /// </summary>
    private static void Compare(JToken actual, JToken original, JToken edited, string path, AssetEditReport report)
    {
        if (report.VerificationMismatches.Count >= 50) return;

        if (Same(original, edited))
        {
            if (!Same(actual, original) && report.SideEffects.Count < 20)
                report.SideEffects.Add($"{path}: {Short(original)} → {Short(actual)}");
            return;
        }

        switch (edited)
        {
            case JObject eo when actual is JObject ao:
            {
                var oo = original as JObject;
                var keys = eo.Properties().Select(p => p.Name).Concat(ao.Properties().Select(p => p.Name)).Distinct();
                foreach (var key in keys)
                {
                    // resolved through the localization at runtime, not part of the package
                    if (key == "LocalizedString") continue;
                    Compare(ao[key], oo?[key], eo[key], $"{path}.{key}", report);
                }
                return;
            }
            case JArray ea when actual is JArray aa:
            {
                if (ea.Count != aa.Count)
                {
                    report.VerificationMismatches.Add($"{path}: 要素数 {aa.Count}（期待値 {ea.Count}）");
                    return;
                }
                var oa = original as JArray;
                for (var i = 0; i < ea.Count; i++) Compare(aa[i], oa != null && i < oa.Count ? oa[i] : null, ea[i], $"{path}[{i}]", report);
                return;
            }
        }

        if (Same(actual, edited)) return;
        if (actual == null) report.VerificationMismatches.Add($"{path}: 書き出し後に存在しません（既定値と同じ値は保存されないことがあります）");
        else if (edited == null) report.VerificationMismatches.Add($"{path}: 削除したはずの値が残っています（{Short(actual)}）");
        else report.VerificationMismatches.Add($"{path}: {Short(actual)}（期待値 {Short(edited)}）");
    }

    private static bool Same(JToken a, JToken b)
    {
        // an emptied object/array is simply not written by CUE4Parse
        if (a == null || b == null) return IsEmpty(a) && IsEmpty(b);
        if (a is JValue av && b is JValue bv && IsNumber(av) && IsNumber(bv) && (av.Type == JTokenType.Float || bv.Type == JTokenType.Float))
        {
            var x = Convert.ToDouble(av.Value, CultureInfo.InvariantCulture);
            var y = Convert.ToDouble(bv.Value, CultureInfo.InvariantCulture);
            // floats are stored in single precision, what was typed may have more digits
            return Math.Abs(x - y) <= Math.Max(1e-6, Math.Abs(x) * 1e-6);
        }
        if (a is JObject ao && b is JObject bo)
        {
            var keys = ao.Properties().Select(p => p.Name).Concat(bo.Properties().Select(p => p.Name)).Distinct();
            return keys.All(k => k == "LocalizedString" || Same(ao[k], bo[k]));
        }
        if (a is JArray aa && b is JArray ba)
            return aa.Count == ba.Count && aa.Zip(ba).All(pair => Same(pair.First, pair.Second));
        return JToken.DeepEquals(a, b);
    }

    private static bool IsNumber(JValue value) => value.Type is JTokenType.Integer or JTokenType.Float;

    private static bool IsEmpty(JToken token) => token == null || token is JContainer { Count: 0 };

    private static string Short(JToken token)
    {
        var text = token?.ToString(Formatting.None) ?? "null";
        return text.Length > 60 ? text[..57] + "..." : text;
    }

    #endregion

    private static bool TryParse(string json, out JArray array, out string error)
    {
        array = null;
        error = null;
        try
        {
            var token = JToken.Parse(json);
            array = token as JArray ?? new JArray(token);
            return true;
        }
        catch (JsonReaderException e)
        {
            error = $"{e.Message}";
            return false;
        }
    }
}
