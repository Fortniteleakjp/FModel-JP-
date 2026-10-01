using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;

namespace FModel.Services.AssetEditing;

/// <summary>
/// the tables of a cooked, unversioned legacy package (.uasset header + .uexp export data)
/// written by hand because UAssetAPI can't build one from scratch, and because UE 5.8+ headers carry fields UAssetAPI doesn't know
/// </summary>
public sealed class LegacyPackageModel
{
    private const uint PackageFileTag = 0x9E2A83C1;

    public sealed class Import
    {
        public (string Name, int Number) ClassPackage;
        public (string Name, int Number) ClassName;
        public int Outer;
        public (string Name, int Number) Object;
        public bool Optional;
    }

    public sealed class Export
    {
        public int Class, Super, Template, Outer;
        public (string Name, int Number) Name;
        public uint Flags;
        public bool NotForClient, NotForServer, IsAsset;
        public byte[] Data;
        public List<int> SerializeBeforeSerialize = [], CreateBeforeSerialize = [], SerializeBeforeCreate = [], CreateBeforeCreate = [];
        public int DependencyCount => SerializeBeforeSerialize.Count + CreateBeforeSerialize.Count + SerializeBeforeCreate.Count + CreateBeforeCreate.Count;
    }

    public sealed class DataResource
    {
        public uint Flags;
        public byte CookedIndex;
        public long SerialOffset, DuplicateSerialOffset, SerialSize, RawSize;
        public int Outer;
        public uint LegacyBulkDataFlags;
    }

    public List<string> Names { get; } = [];
    public List<Import> Imports { get; } = [];
    public List<Export> Exports { get; } = [];
    public List<DataResource> DataResources { get; } = [];
    public uint PackageFlags { get; set; }

    private const uint FilterEditorOnlyFlag = 0x80000000; // PKG_FilterEditorOnly
    private bool FilterEditorOnly => (PackageFlags & FilterEditorOnlyFlag) != 0;

    /// <summary>
    /// the tables of a package UAssetAPI just serialized, export data cut out of <paramref name="written"/>
    /// </summary>
    public static LegacyPackageModel FromUAsset(UAsset asset, byte[] written)
    {
        var model = new LegacyPackageModel { PackageFlags = (uint) asset.PackageFlags };
        foreach (var name in asset.GetNameMapIndexList()) model.Names.Add(name.Value);

        (string, int) Name(FName name) => (name.Value.Value, name.Number);
        foreach (var import in asset.Imports)
        {
            model.Imports.Add(new Import
            {
                ClassPackage = Name(import.ClassPackage),
                ClassName = Name(import.ClassName),
                Outer = import.OuterIndex.Index,
                Object = Name(import.ObjectName),
                Optional = import.bImportOptional
            });
        }

        foreach (var export in asset.Exports)
        {
            model.Exports.Add(new Export
            {
                Class = export.ClassIndex?.Index ?? 0,
                Super = export.SuperIndex?.Index ?? 0,
                Template = export.TemplateIndex?.Index ?? 0,
                Outer = export.OuterIndex?.Index ?? 0,
                Name = Name(export.ObjectName),
                Flags = (uint) export.ObjectFlags,
                NotForClient = export.bNotForClient,
                NotForServer = export.bNotForServer,
                IsAsset = export.bIsAsset,
                Data = written.AsSpan((int) export.SerialOffset, (int) export.SerialSize).ToArray(),
                SerializeBeforeSerialize = export.SerializationBeforeSerializationDependencies.Select(i => i.Index).ToList(),
                CreateBeforeSerialize = export.CreateBeforeSerializationDependencies.Select(i => i.Index).ToList(),
                SerializeBeforeCreate = export.SerializationBeforeCreateDependencies.Select(i => i.Index).ToList(),
                CreateBeforeCreate = export.CreateBeforeCreateDependencies.Select(i => i.Index).ToList()
            });
        }

        foreach (var resource in asset.DataResources ?? [])
        {
            model.DataResources.Add(new DataResource
            {
                Flags = (uint) resource.Flags,
                CookedIndex = resource.CookedIndex,
                SerialOffset = resource.SerialOffset,
                DuplicateSerialOffset = resource.DuplicateSerialOffset,
                SerialSize = resource.SerialSize,
                RawSize = resource.RawSize,
                Outer = resource.OuterIndex?.Index ?? 0,
                LegacyBulkDataFlags = resource.LegacyBulkDataFlags
            });
        }
        return model;
    }

    /// <param name="engine">decides which summary fields exist</param>
    /// <param name="importPackageName">UE 5.8+ writes the package name of every import even in cooked packages, UAssetAPI doesn't read it</param>
    /// <param name="exports">the .uexp: export data followed by the package tag</param>
    public byte[] Write(EngineVersion engine, bool importPackageName, out byte[] exports)
    {
        var writer = new Writer(this, engine, importPackageName);

        // laid out twice: the summary points at tables that come after it, every field is fixed size so the second pass lands on the same offsets
        writer.Serialize(default, out var layout);
        var header = writer.Serialize(layout, out var check);
        if (header.Length != layout.TotalHeaderSize || !check.Equals(layout)) throw new InvalidDataException("ヘッダーのレイアウトが一致しません");

        using var stream = new MemoryStream();
        foreach (var export in Exports) stream.Write(export.Data);
        stream.Write(BitConverter.GetBytes(PackageFileTag));
        exports = stream.ToArray();
        return header;
    }

    private struct Layout
    {
        public int TotalHeaderSize, NameOffset, ImportOffset, ExportOffset, DependsOffset, AssetRegistryDataOffset, PreloadDependencyOffset, PreloadDependencyCount, DataResourceOffset;
    }

    /// <summary>
    /// follows the field order of FPackageFileSummary and the linker tables, see UAsset.ReadHeader/Read
    /// </summary>
    private sealed class Writer
    {
        private readonly LegacyPackageModel _model;
        private readonly bool _importPackageName;
        private readonly ObjectVersion _ue4;
        private readonly ObjectVersionUE5 _ue5;
        private readonly EngineVersion _engine;
        private readonly Dictionary<string, int> _nameIndex = new(StringComparer.Ordinal);

        public Writer(LegacyPackageModel model, EngineVersion engine, bool importPackageName)
        {
            _model = model;
            _engine = engine;
            _importPackageName = importPackageName;
            var versions = new UAsset(engine);
            _ue4 = versions.ObjectVersion;
            _ue5 = versions.ObjectVersionUE5;
            for (var i = 0; i < model.Names.Count; i++) _nameIndex.TryAdd(model.Names[i], i);
        }

        public byte[] Serialize(Layout offsets, out Layout layout)
        {
            var model = _model;
            using var stream = new MemoryStream();
            using var w = new BinaryWriter(stream, Encoding.ASCII, true);
            layout = new Layout();
            var isUe5 = _ue5 >= ObjectVersionUE5.INITIAL_VERSION;
            var legacyFileVersion = isUe5 ? -8 : -7;

            w.Write(PackageFileTag);
            w.Write(legacyFileVersion);
            w.Write(0); // LegacyUE3Version, 0 in unversioned packages
            w.Write(0); // FileVersionUE4, unversioned
            if (legacyFileVersion <= -8) w.Write(0); // FileVersionUE5, unversioned
            w.Write(0); // FileVersionLicenseeUE
            if (_ue5 >= ObjectVersionUE5.PACKAGE_SAVED_HASH)
            {
                w.Write(new byte[20]); // SavedHash
                w.Write(offsets.TotalHeaderSize);
            }
            w.Write(0); // custom versions, unversioned
            if (_ue5 < ObjectVersionUE5.PACKAGE_SAVED_HASH) w.Write(offsets.TotalHeaderSize);

            WriteFString(w, "None"); // FolderName
            w.Write(model.PackageFlags);
            w.Write(model.Names.Count);
            w.Write(offsets.NameOffset);
            if (_ue5 >= ObjectVersionUE5.ADD_SOFTOBJECTPATH_LIST)
            {
                w.Write(0);
                w.Write(0);
            }
            if (!model.FilterEditorOnly && _ue4 >= ObjectVersion.VER_UE4_ADDED_PACKAGE_SUMMARY_LOCALIZATION_ID) WriteFString(w, null);
            if (_ue4 >= ObjectVersion.VER_UE4_SERIALIZE_TEXT_IN_PACKAGES)
            {
                w.Write(0);
                w.Write(0);
            }
            w.Write(model.Exports.Count);
            w.Write(offsets.ExportOffset);
            w.Write(model.Imports.Count);
            w.Write(offsets.ImportOffset);
            if (_ue5 >= ObjectVersionUE5.VERSE_CELLS)
            {
                w.Write(0);
                w.Write(0);
                w.Write(0);
                w.Write(0);
            }
            if (_ue5 >= ObjectVersionUE5.METADATA_SERIALIZATION_OFFSET) w.Write(0);
            w.Write(offsets.DependsOffset);
            if (_ue4 >= ObjectVersion.VER_UE4_ADD_STRING_ASSET_REFERENCES_MAP)
            {
                w.Write(0);
                w.Write(0);
            }
            if (_ue4 >= ObjectVersion.VER_UE4_ADDED_SEARCHABLE_NAMES) w.Write(0);
            w.Write(0); // ThumbnailTableOffset
            if (_ue5 >= ObjectVersionUE5.IMPORT_TYPE_HIERARCHIES)
            {
                w.Write(0);
                w.Write(0);
            }
            if (_ue5 < ObjectVersionUE5.PACKAGE_SAVED_HASH) w.Write(new byte[16]); // PackageGuid
            if (!model.FilterEditorOnly && _ue4 >= ObjectVersion.VER_UE4_ADDED_PACKAGE_OWNER) w.Write(new byte[16]); // PersistentGuid

            w.Write(1); // generations
            w.Write(model.Exports.Count);
            w.Write(model.Names.Count);

            WriteEngineVersion(w); // saved by
            if (_ue4 >= ObjectVersion.VER_UE4_PACKAGE_SUMMARY_HAS_COMPATIBLE_ENGINE_VERSION) WriteEngineVersion(w);

            w.Write(0u); // CompressionFlags
            w.Write(0); // compressed chunks
            w.Write(0u); // PackageSource
            w.Write(0); // AdditionalPackagesToCook
            if (legacyFileVersion > -7) w.Write(0); // texture allocations

            w.Write(offsets.AssetRegistryDataOffset);
            w.Write(offsets.TotalHeaderSize + model.Exports.Sum(e => (long) e.Data.Length)); // BulkDataStartOffset, where the trailing package tag is
            if (_ue4 >= ObjectVersion.VER_UE4_WORLD_LEVEL_INFO) w.Write(0);
            if (_ue4 >= ObjectVersion.VER_UE4_CHANGED_CHUNKID_TO_BE_AN_ARRAY_OF_CHUNKIDS) w.Write(0);
            else if (_ue4 >= ObjectVersion.VER_UE4_ADDED_CHUNKID_TO_ASSETDATA_AND_UPACKAGE) w.Write(0);
            if (_ue4 >= ObjectVersion.VER_UE4_PRELOAD_DEPENDENCIES_IN_COOKED_EXPORTS)
            {
                w.Write(offsets.PreloadDependencyCount);
                w.Write(offsets.PreloadDependencyOffset);
            }
            if (_ue5 >= ObjectVersionUE5.NAMES_REFERENCED_FROM_EXPORT_DATA) w.Write(model.Names.Count);
            if (_ue5 >= ObjectVersionUE5.PAYLOAD_TOC) w.Write(-1L);
            if (_ue5 >= ObjectVersionUE5.DATA_RESOURCES) w.Write(offsets.DataResourceOffset);

            // name map
            layout.NameOffset = (int) stream.Position;
            foreach (var name in model.Names)
            {
                WriteFString(w, name);
                if (_ue4 >= ObjectVersion.VER_UE4_NAME_HASHES_SERIALIZED) w.Write(CRCGenerator.GenerateHash(name, false));
            }

            // imports
            layout.ImportOffset = (int) stream.Position;
            foreach (var import in model.Imports)
            {
                WriteName(w, import.ClassPackage);
                WriteName(w, import.ClassName);
                w.Write(import.Outer);
                WriteName(w, import.Object);
                if (_ue4 >= ObjectVersion.VER_UE4_NON_OUTER_PACKAGE_IMPORT && (!model.FilterEditorOnly || _importPackageName))
                    WriteName(w, import.Object); // cooked packages store the object name when there is no package name
                if (_ue5 >= ObjectVersionUE5.OPTIONAL_RESOURCES) w.Write(import.Optional ? 1 : 0);
            }

            // exports
            layout.ExportOffset = (int) stream.Position;
            var serialOffset = (long) offsets.TotalHeaderSize;
            var firstDependency = 0;
            foreach (var export in model.Exports)
            {
                w.Write(export.Class);
                w.Write(export.Super);
                if (_ue4 >= ObjectVersion.VER_UE4_TemplateIndex_IN_COOKED_EXPORTS) w.Write(export.Template);
                w.Write(export.Outer);
                WriteName(w, export.Name);
                w.Write(export.Flags);
                if (_ue4 < ObjectVersion.VER_UE4_64BIT_EXPORTMAP_SERIALSIZES)
                {
                    w.Write(export.Data.Length);
                    w.Write((int) serialOffset);
                }
                else
                {
                    w.Write((long) export.Data.Length);
                    w.Write(serialOffset);
                }
                serialOffset += export.Data.Length;
                w.Write(0); // bForcedExport
                w.Write(export.NotForClient ? 1 : 0);
                w.Write(export.NotForServer ? 1 : 0);
                if (_ue5 < ObjectVersionUE5.REMOVE_OBJECT_EXPORT_PACKAGE_GUID) w.Write(new byte[16]);
                if (_ue5 >= ObjectVersionUE5.TRACK_OBJECT_EXPORT_IS_INHERITED) w.Write(0);
                w.Write(0u); // PackageFlags
                if (_ue4 >= ObjectVersion.VER_UE4_LOAD_FOR_EDITOR_GAME) w.Write(1); // bNotAlwaysLoadedForEditorGame
                if (_ue4 >= ObjectVersion.VER_UE4_COOKED_ASSETS_IN_EDITOR_SUPPORT) w.Write(export.IsAsset ? 1 : 0);
                if (_ue5 >= ObjectVersionUE5.OPTIONAL_RESOURCES) w.Write(0); // GeneratePublicHash
                if (_ue4 >= ObjectVersion.VER_UE4_PRELOAD_DEPENDENCIES_IN_COOKED_EXPORTS)
                {
                    var total = export.DependencyCount;
                    w.Write(total > 0 ? firstDependency : -1);
                    w.Write(export.SerializeBeforeSerialize.Count);
                    w.Write(export.CreateBeforeSerialize.Count);
                    w.Write(export.SerializeBeforeCreate.Count);
                    w.Write(export.CreateBeforeCreate.Count);
                    firstDependency += total;
                }
                // unversioned packages don't have script serialization offsets
            }

            // depends map, one empty list per export
            layout.DependsOffset = (int) stream.Position;
            foreach (var _ in model.Exports) w.Write(0);

            // asset registry data: no dependency offset in cooked (filter editor only) packages, no assets
            layout.AssetRegistryDataOffset = (int) stream.Position;
            if (!model.FilterEditorOnly && _ue4 >= ObjectVersion.VER_UE4_ASSETREGISTRY_DEPENDENCYFLAGS) w.Write(-1L);
            w.Write(0);

            // preload dependencies, legacy order per export
            layout.PreloadDependencyOffset = (int) stream.Position;
            foreach (var export in model.Exports)
            {
                foreach (var index in export.SerializeBeforeSerialize) w.Write(index);
                foreach (var index in export.CreateBeforeSerialize) w.Write(index);
                foreach (var index in export.SerializeBeforeCreate) w.Write(index);
                foreach (var index in export.CreateBeforeCreate) w.Write(index);
            }
            layout.PreloadDependencyCount = firstDependency;

            // data resources (bulk data table)
            layout.DataResourceOffset = -1;
            if (_ue5 >= ObjectVersionUE5.DATA_RESOURCES && model.DataResources.Count > 0)
            {
                layout.DataResourceOffset = (int) stream.Position;
                var version = _engine >= EngineVersion.VER_UE5_4 ? EObjectDataResourceVersion.AddedCookedIndex : EObjectDataResourceVersion.Initial;
                w.Write((uint) version);
                w.Write(model.DataResources.Count);
                foreach (var resource in model.DataResources)
                {
                    w.Write(resource.Flags);
                    if (version >= EObjectDataResourceVersion.AddedCookedIndex) w.Write(resource.CookedIndex);
                    w.Write(resource.SerialOffset);
                    w.Write(resource.DuplicateSerialOffset);
                    w.Write(resource.SerialSize);
                    w.Write(resource.RawSize);
                    w.Write(resource.Outer);
                    w.Write(resource.LegacyBulkDataFlags);
                }
            }

            layout.TotalHeaderSize = (int) stream.Position;
            return stream.ToArray();
        }

        private void WriteName(BinaryWriter w, (string Name, int Number) name)
        {
            if (!_nameIndex.TryGetValue(name.Name ?? "None", out var index)) throw new InvalidDataException($"名前表に '{name.Name}' がありません");
            w.Write(index);
            w.Write(name.Number);
        }

        private void WriteEngineVersion(BinaryWriter w)
        {
            var parts = _engine.ToString().Replace("VER_UE", "").Split('_'); // VER_UE5_4
            w.Write(parts.Length > 0 && ushort.TryParse(parts[0], out var major) ? major : (ushort) 5);
            w.Write(parts.Length > 1 && ushort.TryParse(parts[1], out var minor) ? minor : (ushort) 0);
            w.Write((ushort) 0);
            w.Write(0u); // changelist
            WriteFString(w, null); // branch
        }

        private static void WriteFString(BinaryWriter w, string value)
        {
            if (value == null)
            {
                w.Write(0);
                return;
            }

            if (value.All(c => c < 128))
            {
                w.Write(value.Length + 1);
                w.Write(Encoding.ASCII.GetBytes(value));
                w.Write((byte) 0);
            }
            else
            {
                w.Write(-(value.Length + 1));
                w.Write(Encoding.Unicode.GetBytes(value));
                w.Write((short) 0);
            }
        }
    }
}
