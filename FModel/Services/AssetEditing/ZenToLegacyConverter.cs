using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using EPackageFlags = CUE4Parse.UE4.Objects.UObject.EPackageFlags;
using EObjectDataResourceVersion = UAssetAPI.UnrealTypes.EObjectDataResourceVersion;
using EObjectFlags = CUE4Parse.UE4.Assets.Exports.EObjectFlags;

namespace FModel.Services.AssetEditing;

/// <summary>
/// rebuilds the legacy .uasset/.uexp pair of a cooked IoStore (zen) package
/// zen keeps the export data byte for byte, only the header is different: the name map is kept in order and the
/// zen import map becomes the first legacy imports so every FName and FPackageIndex inside the export data stays valid,
/// what legacy needs on top (package imports, class/outer chains) is appended after them
/// </summary>
public static class ZenToLegacyConverter
{
    private const string CoreUObject = "/Script/CoreUObject";

    public static EditedAssetWriter.SourcePackage Convert(IoPackage package, IFileProvider provider, GameFile entry, AssetEditReport report)
    {
        if (provider.Versions.Game < EGame.GAME_UE5_0)
            throw new NotSupportedException("UE4 の IoStore パッケージには対応していません");

        var zen = entry.Read();
        var ar = new FByteArchive(entry.Path, zen, provider.Versions);
        var summary = new FZenPackageSummary(ar);

        // export data, the same bytes the legacy .uexp holds
        var exportData = new byte[package.ExportMap.Length][];
        var exportOffsets = new int[package.ExportMap.Length];
        if (provider.Versions.Game >= EGame.GAME_UE5_3)
        {
            for (var i = 0; i < package.ExportMap.Length; i++)
            {
                var export = package.ExportMap[i];
                exportOffsets[i] = (int) (summary.HeaderSize + export.CookedSerialOffset);
                exportData[i] = zen.AsSpan(exportOffsets[i], (int) export.CookedSerialSize).ToArray();
            }
        }
        else
        {
            // 5.0 - 5.2: exports follow each other in export bundle order
            ar.Position = summary.ExportBundleEntriesOffset;
            var entries = ar.ReadArray<FExportBundleEntry>(package.ExportMap.Length * 2);
            var offset = (int) summary.HeaderSize;
            foreach (var bundleEntry in entries)
            {
                if (bundleEntry.CommandType != EExportCommandType.ExportCommandType_Serialize) continue;
                var size = (int) package.ExportMap[bundleEntry.LocalExportIndex].CookedSerialSize;
                exportOffsets[bundleEntry.LocalExportIndex] = offset;
                exportData[bundleEntry.LocalExportIndex] = zen.AsSpan(offset, size).ToArray();
                offset += size;
            }
            report.Warn(null, "UE5.3 より前の IoStore にはエクスポート単位の依存関係が無いため、プリロード依存関係は空で出力します");
        }
        if (exportData.Any(d => d == null)) throw new InvalidDataException("エクスポートデータの位置を特定できませんでした");

        var dependencies = provider.Versions.Game >= EGame.GAME_UE5_3 ? ReadDependencies(ar, summary, package.ExportMap.Length) : null;

        var model = new Builder(package, provider, report).Build(exportData, dependencies, summary.PackageFlags);

        // UAssetAPI reads this one, the final file is written again from the edited tables in the engine's own layout
        var header = model.Write(UAssetApiSupport.ToEngineVersion(provider.Versions.Game), false, out var exports);
        return new EditedAssetWriter.SourcePackage
        {
            Name = package.Name,
            Extension = "." + entry.Extension,
            Header = header,
            Exports = exports,
            Payloads = ReadPayloads(provider, entry),
            Model = model,
            ZenExportOffsets = exportOffsets
        };
    }

    private static Dictionary<string, byte[]> ReadPayloads(IFileProvider provider, GameFile entry)
    {
        var payloads = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, bytes) in provider.SavePackage(entry))
        {
            var extension = EditedAssetWriter.PayloadExtension(entry, path);
            if (extension is ".uasset" or ".umap" or ".uexp") continue;
            payloads[extension] = bytes;
        }
        return payloads;
    }

    /// <summary>
    /// 5.3+ keeps per export dependencies in "dependency bundles", the same four lists legacy preload dependencies have
    /// </summary>
    private static List<int>[][] ReadDependencies(FArchive ar, FZenPackageSummary summary, int exportCount)
    {
        if (summary.DependencyBundleHeadersOffset <= 0 || summary.DependencyBundleEntriesOffset <= 0) return null;

        ar.Position = summary.DependencyBundleEntriesOffset;
        var entryCount = (summary.ImportedPackageNamesOffset - summary.DependencyBundleEntriesOffset) / sizeof(int);
        var entries = ar.ReadArray<int>(Math.Max(0, entryCount));

        ar.Position = summary.DependencyBundleHeadersOffset;
        var result = new List<int>[exportCount][];
        for (var i = 0; i < exportCount; i++)
        {
            var first = ar.Read<int>();
            // EntryCount[Command][Dependency]: [Create][Create], [Create][Serialize], [Serialize][Create], [Serialize][Serialize]
            var counts = ar.ReadArray<uint>(4);
            var lists = new List<int>[4];
            var running = first;
            for (var j = 0; j < 4; j++)
            {
                lists[j] = [];
                for (var k = 0; k < counts[j] && first >= 0 && running < entries.Length; k++)
                    lists[j].Add(entries[running++]);
            }
            result[i] = lists;
        }
        return result;
    }

    private sealed class Builder
    {
        private readonly IoPackage _package;
        private readonly IFileProvider _provider;
        private readonly AssetEditReport _report;
        private readonly IoGlobalData _global;
        private readonly LegacyPackageModel _model = new();
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _importByPath = new(StringComparer.OrdinalIgnoreCase);

        public Builder(IoPackage package, IFileProvider provider, AssetEditReport report)
        {
            _package = package;
            _provider = provider;
            _report = report;
            _global = (provider as IVfsFileProvider)?.GlobalData;

            // keep the zen name map in order, export data indexes into it
            foreach (var entry in package.NameMap)
            {
                _model.Names.Add(entry.Name);
                _names.Add(entry.Name);
            }
        }

        public LegacyPackageModel Build(byte[][] exportData, List<int>[][] dependencies, EPackageFlags packageFlags)
        {
            _model.PackageFlags = (uint) packageFlags;

            // the zen import map keeps its slots, export data references them by index
            var unresolved = 0;
            for (var i = 0; i < _package.ImportMap.Length; i++) _model.Imports.Add(null);
            for (var i = 0; i < _package.ImportMap.Length; i++)
            {
                var key = PathOf(_package.ImportMap[i]);
                if (key != null) _importByPath.TryAdd(key, -i - 1);
            }
            for (var i = 0; i < _package.ImportMap.Length; i++)
            {
                _model.Imports[i] = Describe(_package.ImportMap[i]);
                if (_model.Imports[i] != null) continue;

                unresolved++;
                _model.Imports[i] = new LegacyPackageModel.Import { ClassPackage = (CoreUObject, 0), ClassName = ("Object", 0), Object = ("None", 0) };
            }
            // a placeholder would silently break every reference to it once the package is repacked
            if (unresolved > 0)
                _report.Error(null, $"参照先パッケージを読み込めないインポートが {unresolved} 件あるため、正しい .uasset を作れません（参照先のアーカイブが読み込まれているか確認してください）");

            for (var i = 0; i < _package.ExportMap.Length; i++)
            {
                var export = _package.ExportMap[i];
                var deps = dependencies?[i];
                _model.Exports.Add(new LegacyPackageModel.Export
                {
                    Class = ToLegacy(export.ClassIndex),
                    Super = ToLegacy(export.SuperIndex),
                    Template = ToLegacy(export.TemplateIndex),
                    Outer = ToLegacy(export.OuterIndex),
                    Name = (_package.NameMap[(int) export.ObjectName.NameIndex].Name, (int) export.ObjectName.ExtraIndex),
                    Flags = (uint) export.ObjectFlags,
                    NotForClient = (export.FilterFlags & 1) != 0,
                    NotForServer = (export.FilterFlags & 2) != 0,
                    IsAsset = export.OuterIndex.IsNull && !export.ObjectFlags.HasFlag(EObjectFlags.RF_ClassDefaultObject) && export.ObjectFlags.HasFlag(EObjectFlags.RF_Public),
                    Data = exportData[i],
                    // zen: [Create][Create], [Create][Serialize], [Serialize][Create], [Serialize][Serialize]
                    CreateBeforeCreate = deps?[0] ?? [],
                    SerializeBeforeCreate = deps?[1] ?? [],
                    CreateBeforeSerialize = deps?[2] ?? [],
                    SerializeBeforeSerialize = deps?[3] ?? []
                });
            }

            foreach (var entry in _package.BulkDataMap)
            {
                _model.DataResources.Add(new LegacyPackageModel.DataResource
                {
                    CookedIndex = entry.CookedIndex.Value,
                    SerialOffset = (long) entry.SerialOffset,
                    DuplicateSerialOffset = (long) entry.DuplicateSerialOffset,
                    SerialSize = (long) entry.SerialSize,
                    RawSize = (long) entry.SerialSize,
                    LegacyBulkDataFlags = entry.Flags
                });
            }

            // every name the header needs must exist before the name map is written
            foreach (var import in _model.Imports)
            {
                Name(import.ClassPackage.Name);
                Name(import.ClassName.Name);
                Name(import.Object.Name);
            }
            Name("None");
            return _model;
        }

        private void Name(string name)
        {
            if (_names.Add(name)) _model.Names.Add(name);
        }

        private int ToLegacy(FPackageObjectIndex index)
        {
            if (index.IsNull) return 0;
            if (index.IsExport) return (int) index.AsExport + 1;

            for (var i = 0; i < _package.ImportMap.Length; i++)
                if (_package.ImportMap[i].Equals(index)) return -i - 1;

            // classes/templates zen didn't need in the import map
            var path = PathOf(index);
            if (path != null && _importByPath.TryGetValue(path, out var known)) return known;
            var import = Describe(index);
            return import == null ? 0 : Add(path, import);
        }

        private int Add(string path, LegacyPackageModel.Import import)
        {
            _model.Imports.Add(import);
            var index = -_model.Imports.Count;
            if (path != null) _importByPath[path] = index;
            return index;
        }

        private LegacyPackageModel.Import Describe(FPackageObjectIndex index)
        {
            if (index.IsScriptImport) return DescribeScript(index);
            if (index.IsPackageImport) return _package.ResolveObjectIndex(index) is { } resolved ? DescribeResolved(resolved) : null;
            return null;
        }

        private string PathOf(FPackageObjectIndex index)
        {
            if (index.IsScriptImport) return ScriptPath(index);
            if (index.IsPackageImport) return _package.ResolveObjectIndex(index) is { } resolved ? ResolvedPath(resolved) : null;
            return null;
        }

        private bool TryScript(FPackageObjectIndex index, out FScriptObjectEntry entry)
        {
            entry = default;
            return _global != null && _global.ScriptObjectEntriesMap.TryGetValue(index, out entry);
        }

        private string ScriptPath(FPackageObjectIndex index)
        {
            var parts = new List<string>();
            while (!index.IsNull && TryScript(index, out var entry))
            {
                parts.Add(_package.CreateFNameFromMappedName(entry.ObjectName).Text);
                index = entry.OuterIndex;
            }
            if (parts.Count == 0) return null;
            parts.Reverse();
            return string.Join('.', parts);
        }

        private LegacyPackageModel.Import DescribeScript(FPackageObjectIndex index)
        {
            if (!TryScript(index, out var entry)) return null;

            var name = _package.CreateFNameFromMappedName(entry.ObjectName);
            var import = new LegacyPackageModel.Import { Object = (name.PlainText, name.Number) };
            if (entry.OuterIndex.IsNull)
            {
                import.ClassPackage = (CoreUObject, 0);
                import.ClassName = ("Package", 0);
                return import;
            }

            var outerPath = ScriptPath(entry.OuterIndex);
            import.Outer = outerPath != null && _importByPath.TryGetValue(outerPath, out var known)
                ? known
                : DescribeScript(entry.OuterIndex) is { } outer ? Add(outerPath, outer) : 0;

            if (!entry.CDOClassIndex.IsNull && TryScript(entry.CDOClassIndex, out var classEntry))
            {
                // Default__Foo is an instance of Foo, which lives in its own script package
                var className = _package.CreateFNameFromMappedName(classEntry.ObjectName);
                import.ClassName = (className.PlainText, className.Number);
                import.ClassPackage = (Outermost(entry.CDOClassIndex), 0);
            }
            else
            {
                var nested = TryScript(entry.OuterIndex, out var outerEntry) && !outerEntry.OuterIndex.IsNull;
                import.ClassPackage = (CoreUObject, 0);
                import.ClassName = (nested ? "Function"
                    : _provider.MappingsForGame?.Enums.ContainsKey(name.Text) == true ? "Enum"
                    : "Class", 0);
            }
            return import;
        }

        private string Outermost(FPackageObjectIndex index)
        {
            var name = CoreUObject;
            while (!index.IsNull && TryScript(index, out var entry))
            {
                name = _package.CreateFNameFromMappedName(entry.ObjectName).Text;
                index = entry.OuterIndex;
            }
            return name;
        }

        private static string ResolvedPath(ResolvedObject resolved)
        {
            var parts = new List<string>();
            for (var current = resolved; current != null; current = current.Outer) parts.Add(current.Name.Text);
            parts.Reverse();
            return string.Join('.', parts);
        }

        private LegacyPackageModel.Import DescribeResolved(ResolvedObject resolved)
        {
            var name = resolved.Name;
            var import = new LegacyPackageModel.Import { Object = (name.PlainText, name.Number) };
            var outer = resolved.Outer;
            if (outer == null)
            {
                import.ClassPackage = (CoreUObject, 0);
                import.ClassName = ("Package", 0);
                return import;
            }

            var outerPath = ResolvedPath(outer);
            import.Outer = _importByPath.TryGetValue(outerPath, out var known) ? known : Add(outerPath, DescribeResolved(outer));

            var cls = resolved.Class;
            if (cls == null || cls.Outer == null)
            {
                import.ClassPackage = (CoreUObject, 0);
                import.ClassName = (cls?.Name.PlainText ?? "Object", cls?.Name.Number ?? 0);
            }
            else
            {
                var root = cls;
                while (root.Outer != null) root = root.Outer;
                import.ClassPackage = (root.Name.Text, 0);
                import.ClassName = (cls.Name.PlainText, cls.Name.Number);
            }
            return import;
        }
    }
}
