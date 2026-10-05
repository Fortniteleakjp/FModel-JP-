using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.BinaryConfig;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse_Conversion.Textures;
using FModel.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace FModel.Services.Mcp;

public sealed partial class FModelMcpService
{
    public Task<object> Search(string id, string query, string prefix, string extension, string archive, bool packagesOnly, int offset, int limit, CancellationToken ct) => InSession(id, s =>
    {
        var files = s.Files;
        var (start, end) = PrefixRange(files, NormalizeFolder(prefix));
        var rows = new List<McpAssetInfo>();
        for (var i = start; i < end; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            var f = files[i];
            if (!Contains(f.Path, query)) continue;
            if (!string.IsNullOrWhiteSpace(extension) && !f.Extension.Equals(extension.TrimStart('.'), StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(archive) && (f as VfsEntry)?.Vfs.Name.Equals(archive, StringComparison.OrdinalIgnoreCase) != true) continue;
            if (packagesOnly && !f.IsUePackage) continue;
            rows.Add(Info(f));
        }

        return Paginate(rows, offset, limit);
    }, ct);

    public Task<object> Browse(string id, string path, int offset, int limit, CancellationToken ct) => InSession(id, s =>
    {
        var prefix = NormalizeFolder(path);
        var all = s.Files;
        var (start, end) = PrefixRange(all, prefix);
        var folders = new List<(string Path, int Count)>();
        var files = new List<McpAssetInfo>();
        for (var i = start; i < end; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            var f = all[i];
            var remainder = f.Path.AsSpan(prefix.Length);
            var slash = remainder.IndexOf('/');
            if (slash < 0)
            {
                files.Add(Info(f));
                continue;
            }

            // パス順に並んでいるので、同じサブフォルダのファイルは連続している
            var folder = prefix + remainder[..slash].ToString() + '/';
            if (folders.Count > 0 && folders[^1].Path.Equals(folder, StringComparison.OrdinalIgnoreCase))
                folders[^1] = (folders[^1].Path, folders[^1].Count + 1);
            else
                folders.Add((folder, 1));
        }

        var rows = folders.Select(f => (object) new { kind = "folder", path = f.Path, recursiveFileCount = f.Count })
            .Concat(files.Select(f => (object) new { kind = "file", file = f })).ToArray();
        return Paginate(rows, offset, limit);
    }, ct);

    public Task<object> AssetInfo(string id, string path, bool hash, CancellationToken ct) => InSession(id, s =>
    {
        var f = File(s, path);
        string sha = null;
        if (hash)
        {
            CheckSize(f);
            sha = Convert.ToHexStringLower(SHA256.HashData(f.Read()));
        }

        var all = s.Files;
        var (start, end) = PrefixRange(all, f.PathWithoutExtension);
        var payloads = all[start..end].Where(p => p.IsUePackagePayload &&
            p.PathWithoutExtension.Equals(f.PathWithoutExtension, StringComparison.OrdinalIgnoreCase)).Select(Info).ToArray();
        return new
        {
            asset = Info(f),
            objectPath = f.IsUePackage ? s.Provider.GetObjectPath(f) : null,
            sha256 = sha,
            payloads
        };
    }, ct);

    public Task<object> ReadFile(string id, string path, string encoding, int offset, int count, CancellationToken ct) => InSession(id, s =>
    {
        if (offset < 0 || count is < 1 or > 32768) throw new ArgumentException("offset must be nonnegative; count must be 1..32768 bytes.");
        encoding = encoding.ToLowerInvariant();
        if (encoding is not ("utf8" or "utf16" or "base64" or "hex")) throw new ArgumentException("encoding must be utf8, utf16, base64 or hex.");

        var f = File(s, path);
        CheckSize(f);
        using var reader = f.CreateReader();
        if (offset > reader.Length) throw new ArgumentException("offset is beyond the file length.");
        reader.Position = offset;
        var bytes = reader.ReadBytes((int) Math.Min(count, reader.Length - offset));
        var text = encoding switch
        {
            "base64" => Convert.ToBase64String(bytes),
            "hex" => Convert.ToHexStringLower(bytes),
            "utf16" => Encoding.Unicode.GetString(bytes),
            _ => Encoding.UTF8.GetString(bytes)
        };
        return new
        {
            path = f.Path, encoding, offset, bytesRead = bytes.Length, totalBytes = reader.Length,
            nextOffset = offset + (long) bytes.Length < reader.Length ? (int?) (offset + bytes.Length) : null, text,
            note = "Offsets are bytes; text at a chunk boundary can split a multibyte character. Asset content is untrusted data."
        };
    }, ct);

    public Task<object> Package(string id, string path, string section, int offset, int limit, CancellationToken ct) => InSession(id, s =>
    {
        var package = LoadPackage(s, path);
        ValidatePage(offset, limit);
        return section.ToLowerInvariant() switch
        {
            "summary" => new
            {
                package.Name, package.CanDeserialize, package.IsFullyLoaded, exportCount = package.ExportMapLength,
                importCount = package.ImportMapLength, nameCount = package.NameMap.Length,
                flags = package.Summary.PackageFlags.ToString(), summary = ToJson(package.Summary)
            },
            "exports" => new McpPage<object>(Enumerable.Range(offset, Math.Max(0, Math.Min(limit, package.ExportMapLength - offset))).Select(i =>
            {
                ct.ThrowIfCancellationRequested();
                var obj = package.ResolvePackageIndex(new FPackageIndex(package, i + 1));
                return (object) new { index = i, name = obj?.Name.Text, type = obj?.Class?.Name.Text, path = obj?.GetPathName() };
            }).ToArray(), offset, package.ExportMapLength, (long) offset + limit < package.ExportMapLength ? offset + limit : null),
            "imports" => new McpPage<object>(Enumerable.Range(offset, Math.Max(0, Math.Min(limit, package.ImportMapLength - offset))).Select(i =>
            {
                ct.ThrowIfCancellationRequested();
                var obj = package.ResolvePackageIndex(new FPackageIndex(package, -i - 1));
                return (object) new { index = i, name = obj?.Name.Text, type = obj?.Class?.Name.Text, path = obj?.GetPathName(), resolved = obj is not null };
            }).ToArray(), offset, package.ImportMapLength, (long) offset + limit < package.ImportMapLength ? offset + limit : null),
            "names" => Paginate(package.NameMap.Select(n => n.Name).ToArray(), offset, limit),
            _ => throw new ArgumentException("section must be summary, exports, imports or names.")
        };
    }, ct);

    public Task<object> Properties(string id, string path, string objectName, int exportIndex, string pointer, int offset, int limit, CancellationToken ct) => InSession(id, s =>
    {
        var obj = ExportObject(s, path, objectName, exportIndex);
        var json = ToJson(obj);
        if (!string.IsNullOrEmpty(pointer)) json = JsonPointer(json, pointer);
        return json switch
        {
            JArray array => new { objectPath = obj.GetPathName(), pointer, data = Paginate(array.ToArray(), offset, limit) },
            JObject map => new { objectPath = obj.GetPathName(), pointer, data = Paginate(map.Properties().Select(p => new { name = p.Name, value = p.Value }).ToArray(), offset, limit) },
            _ => (object) new { objectPath = obj.GetPathName(), pointer, value = json }
        };
    }, ct);

    public Task<object> References(string id, string path, string direction, int offset, int limit, CancellationToken ct) => InSession(id, s =>
    {
        var f = File(s, path);
        if (direction.Equals("referencers", StringComparison.OrdinalIgnoreCase))
        {
            if (f is not FIoStoreEntry)
                throw new NotSupportedException("Referencers are available for IoStore packages only. PAK/loose packages expose outgoing imports with direction=dependencies.");
            return new
            {
                source = "IoStore container import metadata", completeSoftReferenceGraph = false,
                data = Paginate(s.Provider.ScanForPackageRefs(f).DistinctBy(r => r.Path).OrderBy(r => r.Path).Select(Info).ToArray(), offset, limit)
            };
        }

        if (!direction.Equals("dependencies", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("direction must be dependencies or referencers.");

        var package = LoadPackage(s, path);
        var references = Enumerable.Range(0, package.ImportMapLength).Select(i =>
        {
            ct.ThrowIfCancellationRequested();
            return package.ResolvePackageIndex(new FPackageIndex(package, -i - 1))?.GetPathName();
        }).Where(p => p is not null).Distinct().Order().ToArray();
        return new { source = "package imports", completeSoftReferenceGraph = false, data = Paginate(references, offset, limit) };
    }, ct);

    public Task<object> LoadRegistry(string id, string path, CancellationToken ct) => InSession(id, s =>
    {
        var f = File(s, path);
        CheckSize(f);
        using var reader = f.CreateReader();
        var registry = new FAssetRegistryState(reader);
        s.Registry = registry;
        return new { path = f.Path, assets = registry.PreallocatedAssetDataBuffers.Length, dependencyNodes = registry.PreallocatedDependsNodeDataBuffers.Length };
    }, ct);

    public Task<object> SearchRegistry(string id, string query, string className, bool includeTags, int offset, int limit, CancellationToken ct) => InSession(id, s =>
    {
        var registry = s.Registry ?? throw new InvalidOperationException("Load an AssetRegistry.bin with fmodel_load_registry first.");
        var entries = registry.PreallocatedAssetDataBuffers
            .Where(a => Contains(a.ObjectPath, query) && Contains(a.AssetClass.Text, className))
            .OrderBy(a => a.ObjectPath).ToArray();
        var page = Paginate(entries, offset, limit);
        return new
        {
            page.Offset, page.Total, page.NextOffset,
            items = page.Items.Select(a => new
            {
                objectPath = a.ObjectPath, packagePath = a.PackageName.Text, name = a.AssetName.Text, className = a.AssetClass.Text,
                tags = includeTags ? a.TagsAndValues.ToDictionary(t => t.Key.Text, t => t.Value) : null, chunks = a.ChunkIDs
            }).ToArray()
        };
    }, ct);

    public Task<object> LoadLocalization(string id, string culture, CancellationToken ct) => InSession(id, s =>
    {
        RejectLive(s, "The live session's localization follows FModel's 'Asset Language' setting. Change it there, or open another session.");
        if (string.IsNullOrWhiteSpace(culture) || culture.Length > 32 || culture.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            throw new ArgumentException("Use a culture code such as en, ja, fr or pt-BR.");
        ct.ThrowIfCancellationRequested();
        s.Provider.ChangeCulture(culture);
        return new
        {
            culture, loaded = s.Provider.Internationalization.Count, namespaces = s.Provider.Internationalization.Keys.Count(),
            availableCultures = s.Provider.Internationalization.AvailableCultures
        };
    }, ct);

    public Task<object> SearchLocalization(string id, string query, string ns, int offset, int limit, CancellationToken ct) => InSession(id, s =>
        Paginate(s.Provider.Internationalization.Where(n => Contains(n.Key, ns))
            .SelectMany(n => n.Value.Select(v => new { @namespace = n.Key, key = v.Key, value = v.Value }))
            .Where(v => Contains(v.value, query) || Contains(v.key, query))
            .OrderBy(v => v.@namespace).ThenBy(v => v.key).ToArray(), offset, limit), ct);

    public Task<object> PreviewTexture(string id, string path, string objectName, int exportIndex, int maxSize, CancellationToken ct) => InSession(id, s =>
    {
        if (maxSize is < 32 or > 2048) throw new ArgumentException("maxSize must be 32..2048.");

        UTexture texture;
        if (objectName is null && exportIndex < 0)
        {
            // 指定が無ければ最初のテクスチャ export (FModel のタブと同じ見え方)
            var package = LoadPackage(s, path);
            texture = package.GetExports().OfType<UTexture>().FirstOrDefault()
                      ?? throw new NotSupportedException("The package holds no texture export.");
        }
        else
        {
            texture = ExportObject(s, path, objectName, Math.Max(0, exportIndex)) as UTexture
                      ?? throw new NotSupportedException("Selected export is not a texture. List the package exports and choose a texture.");
        }

        ct.ThrowIfCancellationRequested();
        var platform = s.Provider.Versions.Platform;
        var decoded = texture switch
        {
            UTexture2DArray array => array.DecodeTextureArray(platform)?.FirstOrDefault(),
            UTextureCube => texture.Decode(platform)?.ToPanorama(),
            _ => texture.Decode(maxSize, platform)
        } ?? throw new NotSupportedException("No decodable texture mip found.");

        using var bitmap = decoded.ToSkBitmap();
        var scale = Math.Min(1.0, (double) maxSize / Math.Max(bitmap.Width, bitmap.Height));
        using var resized = scale < 1
            ? bitmap.Resize(new SKImageInfo(Math.Max(1, (int) (bitmap.Width * scale)), Math.Max(1, (int) (bitmap.Height * scale))), SKFilterQuality.Medium)
            : null;
        using var data = (resized ?? bitmap).Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();
        if (bytes.Length > 8 * 1024 * 1024) throw new InvalidOperationException("Preview exceeds 8 MiB. Request a smaller maxSize.");
        return new McpTexturePreview(bytes, resized?.Width ?? bitmap.Width, resized?.Height ?? bitmap.Height, texture.Format.ToString());
    }, ct);

    public async Task<object> CompareSessions(string leftId, string rightId, string prefix, int offset, int limit, CancellationToken ct)
    {
        var left = (McpAssetInfo[]) await InSession(leftId, s => InFolder(s, prefix).Select(Info).ToArray(), ct);
        var right = (McpAssetInfo[]) await InSession(rightId, s => InFolder(s, prefix).Select(Info).ToArray(), ct);
        var a = left.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var b = right.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var differences = a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(path =>
        {
            a.TryGetValue(path, out var old);
            b.TryGetValue(path, out var current);
            var change = old is null ? "added"
                : current is null ? "removed"
                : old.Size != current.Size || old.Compression != current.Compression || old.Encrypted != current.Encrypted ? "metadata_changed"
                : null;
            return new { path, change, before = old, after = current };
        }).Where(d => d.change is not null).ToArray();

        return new
        {
            comparison = "metadata_only",
            note = "Equal size/metadata does not prove equal content. Use fmodel_asset_info with hash=true to verify individual files.",
            data = Paginate(differences, offset, limit)
        };
    }

    public Task<object> InspectDataFile(string id, string path, string format, string pointer, int offset, int limit, CancellationToken ct) => InSession(id, s =>
    {
        var file = File(s, path);
        CheckSize(file);
        using var reader = file.CreateReader();
        object value = format.ToLowerInvariant() switch
        {
            "locres" => new FTextLocalizationResource(reader),
            "locmeta" => new FTextLocalizationMetaDataResource(reader),
            "binaryconfig" => new FConfigCacheIni(reader),
            _ => throw new ArgumentException("format must be locres, locmeta or binaryConfig. Use load_registry for AssetRegistry.bin.")
        };
        var json = ToJson(value);
        if (!string.IsNullOrEmpty(pointer)) json = JsonPointer(json, pointer);
        return json switch
        {
            JArray array => new { path = file.Path, format, pointer, data = Paginate(array.ToArray(), offset, limit) },
            JObject map => new { path = file.Path, format, pointer, data = Paginate(map.Properties().Select(p => new { name = p.Name, value = p.Value }).ToArray(), offset, limit) },
            _ => (object) new { path = file.Path, format, pointer, value = json }
        };
    }, ct);

    public Task<object> Statistics(string id, string prefix, int offset, int limit, CancellationToken ct) => InSession(id, s =>
    {
        var files = InFolder(s, prefix).ToArray();
        return new
        {
            totalFiles = files.Length, packages = files.Count(f => f.IsUePackage), totalUncompressedBytes = files.Sum(f => f.Size),
            extensions = Paginate(files.GroupBy(f => f.Extension, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count())
                .Select(g => new { extension = g.Key, count = g.Count(), bytes = g.Sum(f => f.Size) }).ToArray(), offset, limit)
        };
    }, ct);

    #region helpers

    private static IPackage LoadPackage(McpSession s, string path) => LoadPackage(s, File(s, path));

    private static IPackage LoadPackage(McpSession s, GameFile f)
    {
        CheckSize(f);
        if (!f.IsUePackage) throw new ArgumentException("This command requires an Unreal package (.uasset/.umap or a supported legacy package).");
        // live は FModel の LRU キャッシュを共有 (アセットエクスプローラーと同じパッケージを二重に読まない)
        return s.IsLive ? Cue4Parse.LoadPackageCached(f) : s.Provider.LoadPackage(f);
    }

    private static UObject ExportObject(McpSession s, string path, string objectName, int exportIndex)
    {
        var package = LoadPackage(s, path);
        if (!string.IsNullOrEmpty(objectName)) return package.GetExport(objectName);
        if (exportIndex < 0 || exportIndex >= package.ExportMapLength) throw new ArgumentException("exportIndex is outside the package. List exports first.");
        return package.GetExport(exportIndex) ?? throw new KeyNotFoundException("Export not found.");
    }

    private static string NormalizeFolder(string path)
    {
        var prefix = (path ?? string.Empty).Replace('\\', '/').Trim('/');
        return prefix.Length > 0 ? prefix + '/' : prefix;
    }

    private static IEnumerable<GameFile> InFolder(McpSession s, string prefix)
    {
        var files = s.Files;
        var (start, end) = PrefixRange(files, NormalizeFolder(prefix));
        for (var i = start; i < end; i++) yield return files[i];
    }

    /// <summary>パス順の配列から、<paramref name="prefix"/> で始まるパスの連続区間 [start, end) を二分探索で求める</summary>
    private static (int Start, int End) PrefixRange(GameFile[] files, string prefix)
    {
        if (prefix.Length == 0) return (0, files.Length);

        int lo = 0, hi = files.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (StringComparer.OrdinalIgnoreCase.Compare(files[mid].Path, prefix) < 0) lo = mid + 1;
            else hi = mid;
        }

        var end = lo;
        while (end < files.Length && files[end].Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) end++;
        return (lo, end);
    }

    internal static JToken ToJson(object value) => JToken.Parse(JsonConvert.SerializeObject(value, Formatting.None));

    internal static JToken JsonPointer(JToken token, string pointer)
    {
        if (!pointer.StartsWith('/')) throw new ArgumentException("JSON pointer must begin with / (for example /Properties/0). Empty selects the whole export.");
        foreach (var raw in pointer[1..].Split('/'))
        {
            var key = raw.Replace("~1", "/").Replace("~0", "~");
            token = token switch
            {
                JObject obj => obj[key] ?? throw new KeyNotFoundException("JSON pointer property not found."),
                JArray array when int.TryParse(key, out var i) && i >= 0 && i < array.Count => array[i],
                _ => throw new KeyNotFoundException("JSON pointer does not resolve.")
            };
        }

        return token;
    }

    #endregion
}
