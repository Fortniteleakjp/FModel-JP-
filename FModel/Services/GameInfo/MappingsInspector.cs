using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Jmap;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;

namespace FModel.Services.GameInfo;

/// <summary>マッピングファイルのヘッダー (中身の型は読まない)</summary>
public sealed class MappingsHeader
{
    public string Path { get; init; }
    public long FileSize { get; init; }
    public DateTime LastWriteTime { get; init; }
    public bool IsJmap { get; init; }

    public EUsmapVersion? UsmapVersion { get; init; }
    public EUsmapCompressionMethod? Compression { get; init; }
    public uint CompressedSize { get; init; }
    public uint DecompressedSize { get; init; }

    public bool HasVersioning { get; init; }
    public FEngineVersion EngineVersion { get; init; }
    public FPackageFileVersion PackageVersion { get; init; }
    public int CustomVersionCount { get; init; }
    public uint NetCL { get; init; }

    /// <summary>ヘッダーの CL、無ければファイル名の "CL-12345"</summary>
    public uint? Changelist => NetCL > 0 ? NetCL
        : EngineVersion is { Changelist: > 0 } engine ? engine.Changelist
        : MappingsInspector.ChangelistFromText(System.IO.Path.GetFileName(Path));
}

public sealed record MappingTypeEntry(string Name, bool IsEnum, int Count, string SuperType)
{
    public string Kind => IsEnum ? "enum" : "struct";
    public string Summary => IsEnum ? $"{Count} 値" : SuperType is null ? $"{Count} プロパティ" : $"{Count} プロパティ : {SuperType}";
}

public sealed record MappingsStatistics(int StructCount, int EnumCount, long PropertyCount, long EnumValueCount);

public static partial class MappingsInspector
{
    private const ushort UsmapMagic = 0x30C4;

    [GeneratedRegex(@"CL-(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ChangelistRegex();

    /// <summary>エンジンバージョン表記 "5.8.0-49241099+++Fortnite+Release-39.11" の CL</summary>
    [GeneratedRegex(@"\b\d+\.\d+\.\d+-(\d{5,})\+")]
    private static partial Regex EngineVersionChangelistRegex();

    public static uint? ChangelistFromText(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var match = ChangelistRegex().Match(text);
        if (!match.Success) match = EngineVersionChangelistRegex().Match(text);
        return match.Success && uint.TryParse(match.Groups[1].Value, out var changelist) ? changelist : null;
    }

    public static bool IsJmap(string path) =>
        path.EndsWith(".jmap", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jmap.gz", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// usmap のヘッダーだけ読む。<see cref="UsmapParser"/> と同じ順序 (magic, 版, バージョン情報, 圧縮方式, サイズ) で、
    /// 型の本体は展開しないので Fortnite の大きなマッピングでもすぐ終わる
    /// </summary>
    public static MappingsHeader ReadHeader(string path)
    {
        var file = new FileInfo(path);
        if (IsJmap(path))
            return new MappingsHeader { Path = file.FullName, FileSize = file.Length, LastWriteTime = file.LastWriteTime, IsJmap = true };

        if (file.Length < 2)
            throw new InvalidDataException("The file is empty (the download may have failed)");

        using var archive = new FStreamArchive(file.Name, file.OpenRead());
        if (archive.Read<ushort>() != UsmapMagic)
            throw new InvalidDataException("This is not a .usmap file (invalid magic)");

        var version = archive.Read<EUsmapVersion>();
        if (version > EUsmapVersion.Latest)
            throw new InvalidDataException($"Unsupported usmap version {(byte) version}");

        var Ar = new FUsmapReader(archive, version);
        var hasVersioning = version >= EUsmapVersion.PackageVersioning && Ar.ReadBoolean();
        FEngineVersion engineVersion = null;
        FPackageFileVersion packageVersion = default;
        var customVersionCount = 0;
        uint netCl = 0;
        if (hasVersioning)
        {
            if (version >= EUsmapVersion.EngineVersioning)
                engineVersion = new FEngineVersion(Ar);

            packageVersion = new FPackageFileVersion(Ar.Read<int>(), Ar.Read<int>());
            customVersionCount = new FCustomVersionContainer(Ar).Versions.Length;
            netCl = Ar.Read<uint>();
        }

        var compression = Ar.Read<EUsmapCompressionMethod>();
        var compressedSize = Ar.Read<uint>();
        var decompressedSize = Ar.Read<uint>();

        return new MappingsHeader
        {
            Path = file.FullName,
            FileSize = file.Length,
            LastWriteTime = file.LastWriteTime,
            UsmapVersion = version,
            Compression = compression,
            CompressedSize = compressedSize,
            DecompressedSize = decompressedSize,
            HasVersioning = hasVersioning,
            EngineVersion = engineVersion,
            PackageVersion = packageVersion,
            CustomVersionCount = customVersionCount,
            NetCL = netCl
        };
    }

    /// <summary>ヘッダーの内容を情報ウィンドウの行にする (ゲーム情報タブとマッピングタブで共通)</summary>
    public static void AddHeaderRows(InfoSection section, MappingsHeader header)
    {
        section.Add("ファイル", System.IO.Path.GetFileName(header.Path), note: header.Path);
        section.Add("サイズ", GameInfoCollector.FormatSize(header.FileSize));
        section.Add("更新日時", header.LastWriteTime.ToString("yyyy/MM/dd HH:mm:ss"));
        if (header.IsJmap)
        {
            section.Add("形式", "jmap (JSON マッピング)");
            return;
        }

        section.Add("形式", $"usmap {header.UsmapVersion} ({(byte) header.UsmapVersion})");
        section.Add("圧縮", $"{header.Compression} ({GameInfoCollector.FormatSize(header.CompressedSize)} → {GameInfoCollector.FormatSize(header.DecompressedSize)})");
        if (!header.HasVersioning)
        {
            section.Add("バージョン情報", "記録なし");
            return;
        }

        if (header.EngineVersion is { Major: > 0 } engine)
            section.Add("記録されたエンジン", engine.ToString());
        section.Add("記録されたパッケージバージョン", GameInfoCollector.FormatPackageVersion(header.PackageVersion));
        section.Add("カスタムバージョン", $"{header.CustomVersionCount} 個");
        if (header.NetCL > 0) section.Add("NetCL", header.NetCL.ToString());
    }

    public static void AddStatisticsRows(InfoSection section, MappingsStatistics statistics)
    {
        section.Add("型", $"構造体・クラス {statistics.StructCount:N0} 個 / 列挙型 {statistics.EnumCount:N0} 個");
        section.Add("プロパティ", $"{statistics.PropertyCount:N0} 個 / 列挙値 {statistics.EnumValueCount:N0} 個");
    }

    /// <summary>読み込み済みでないファイルの型を読む (ファイルを指定して開いたとき)</summary>
    public static TypeMappings LoadMappings(string path) => IsJmap(path)
        ? new JmapParser(path).Mappings
        : new UsmapParser(path).Mappings;

    public static MappingsStatistics GetStatistics(TypeMappings mappings)
    {
        if (mappings is null) return new MappingsStatistics(0, 0, 0, 0);
        return new MappingsStatistics(
            mappings.Types.Count,
            mappings.Enums.Count,
            mappings.Types.Values.Sum(t => (long) t.PropertyCount),
            mappings.Enums.Values.Sum(e => (long) e.Count));
    }

    public static List<MappingTypeEntry> BuildEntries(TypeMappings mappings)
    {
        var entries = new List<MappingTypeEntry>(mappings.Types.Count + mappings.Enums.Count);
        entries.AddRange(mappings.Types.Values.Select(t => new MappingTypeEntry(t.Name, false, t.PropertyCount, t.SuperType)));
        entries.AddRange(mappings.Enums.Select(e => new MappingTypeEntry(e.Key, true, e.Value.Count, null)));
        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return entries;
    }

    /// <summary>型の中身を C++ 風のテキストにする</summary>
    public static string Describe(TypeMappings mappings, MappingTypeEntry entry)
    {
        var sb = new StringBuilder();
        if (entry.IsEnum)
        {
            if (!mappings.Enums.TryGetValue(entry.Name, out var values)) return string.Empty;

            sb.AppendLine($"enum {entry.Name}");
            sb.AppendLine("{");
            foreach (var (value, name) in values.OrderBy(v => v.Key))
                sb.AppendLine($"    {name} = {value},");
            sb.AppendLine("};");
            return sb.ToString();
        }

        if (!mappings.Types.TryGetValue(entry.Name, out var type)) return string.Empty;

        // 継承チェーン (循環に備えて回数を制限)
        var chain = new List<string>();
        for (var super = type.Super?.Value; super != null && chain.Count < 64; super = super.Super?.Value)
            chain.Add(super.Name);
        if (type.SuperType != null && chain.Count == 0)
            chain.Add(type.SuperType + " (マッピングに無し)");

        sb.AppendLine(type.SuperType is null ? $"struct {type.Name}" : $"struct {type.Name} : {type.SuperType}");
        if (chain.Count > 1)
            sb.AppendLine($"// 継承: {string.Join(" → ", chain)}");
        sb.AppendLine($"// プロパティ {type.PropertyCount} 個 (継承込み {type.CountProperties(true)} 個)");
        sb.AppendLine("{");
        // キーがシリアル化の番号。固定長配列は要素ごとに同じ PropertyInfo が並ぶので先頭だけ出す (PropertyInfo.Index は配列内の位置)
        var written = new HashSet<PropertyInfo>(ReferenceEqualityComparer.Instance);
        foreach (var (index, property) in type.Properties.OrderBy(p => p.Key))
        {
            if (!written.Add(property)) continue;
            var arraySuffix = property.ArraySize is > 1 ? $"[{property.ArraySize}]" : string.Empty;
            sb.AppendLine($"    /* {index,4} */ {FormatType(property.MappingType)} {property.Name}{arraySuffix};");
        }
        sb.AppendLine("};");
        return sb.ToString();
    }

    public static string FormatType(PropertyType type)
    {
        if (type is null) return "?";
        return type.Type switch
        {
            "StructProperty" => type.StructType ?? "struct",
            "ArrayProperty" => $"TArray<{FormatType(type.InnerType)}>",
            "SetProperty" => $"TSet<{FormatType(type.InnerType)}>",
            "MapProperty" => $"TMap<{FormatType(type.InnerType)}, {FormatType(type.ValueType)}>",
            "OptionalProperty" => $"TOptional<{FormatType(type.InnerType)}>",
            "EnumProperty" => type.EnumName ?? FormatType(type.InnerType),
            "ByteProperty" when type.EnumName != null => $"TEnumAsByte<{type.EnumName}>",
            "ByteProperty" => "uint8",
            "BoolProperty" => "bool",
            "Int8Property" => "int8",
            "Int16Property" => "int16",
            "IntProperty" => "int32",
            "Int64Property" => "int64",
            "UInt16Property" => "uint16",
            "UInt32Property" => "uint32",
            "UInt64Property" => "uint64",
            "FloatProperty" => "float",
            "DoubleProperty" => "double",
            "StrProperty" => "FString",
            "NameProperty" => "FName",
            "TextProperty" => "FText",
            "ObjectProperty" => "UObject*",
            "WeakObjectProperty" => "TWeakObjectPtr<UObject>",
            "LazyObjectProperty" => "TLazyObjectPtr<UObject>",
            "SoftObjectProperty" => "TSoftObjectPtr<UObject>",
            "ClassProperty" => "UClass*",
            "SoftClassProperty" => "TSoftClassPtr<UObject>",
            "InterfaceProperty" => "TScriptInterface<IInterface>",
            "DelegateProperty" => "FScriptDelegate",
            "MulticastDelegateProperty" or "MulticastInlineDelegateProperty" or "MulticastSparseDelegateProperty" => "FMulticastScriptDelegate",
            "FieldPathProperty" => "TFieldPath<FProperty>",
            _ => type.Type.EndsWith("Property", StringComparison.Ordinal) ? type.Type[..^"Property".Length] : type.Type
        };
    }
}
