using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FModel.Services.AssetEditing;

/// <summary>
/// merges the assets of a cooked AssetRegistry.bin (the modding project's) into the game's one, like AssetRegistryHelper -Merge -Filter=
/// the base registry is kept as is, assets of the other registry under the filtered paths that the base doesn't know yet are appended
/// works with the name table registries (versions 4 to 6, UE4.19 to UE4.25 / Fortnite up to season 14)
/// </summary>
public static class AssetRegistryMerger
{
    // FAssetRegistryVersion::GUID
    private static readonly byte[] VersionGuid = [0xE7, 0x9E, 0x7F, 0x71, 0x3A, 0x49, 0xB0, 0xE9, 0x32, 0x91, 0xB3, 0x88, 0x07, 0x81, 0x38, 0x1B];
    private const int RemovedMD5Hash = 4, AddedHardManage = 5, AddedCookedMD5Hash = 6;
    public const int MinVersion = RemovedMD5Hash, MaxVersion = AddedCookedMD5Hash;

    public readonly record struct Name(string Text, int Number)
    {
        public override string ToString() => Number == 0 ? Text : $"{Text}_{Number - 1}";
    }

    public sealed class AssetData
    {
        public Name ObjectPath, PackagePath, AssetClass, PackageName, AssetName;
        public List<(Name key, string value)> Tags = [];
        public int[] ChunkIds = [];
        public uint PackageFlags;
    }

    public sealed class DependsNode
    {
        public byte FieldBits;
        public Name[] Identifier = new Name[4];
        public int[] Counts = new int[6]; // hard, soft, name, soft manage, hard manage, referencers
        public int[] Indices = [];
    }

    public sealed class PackageData
    {
        public Name PackageName;
        public long DiskSize;
        public byte[] PackageGuid = new byte[16];
        public byte[] CookedHash; // null when the package has none
    }

    public sealed class Registry
    {
        public int Version;
        public List<AssetData> Assets = [];
        public List<DependsNode> DependsNodes = [];
        public List<PackageData> Packages = [];
    }

    public sealed class Result
    {
        public byte[] Data;
        public int Version;
        public int BaseAssets;
        public List<string> Added = [];
        public List<string> AlreadyInBase = [];
        public int OutsideFilter;
        public List<string> VerificationErrors = [];
    }

    /// <param name="filters">package path prefixes ("/Game/Athena/Items/Cosmetics"), empty to take every asset</param>
    public static Result Merge(byte[] baseData, byte[] mergeData, IReadOnlyList<string> filters)
    {
        var target = Read(baseData, "元の AssetRegistry");
        var source = Read(mergeData, "マージする AssetRegistry");
        var result = new Result { Version = target.Version, BaseAssets = target.Assets.Count };

        var known = new HashSet<string>(target.Assets.Select(a => a.ObjectPath.ToString()), StringComparer.OrdinalIgnoreCase);
        foreach (var asset in source.Assets)
        {
            var path = asset.ObjectPath.ToString();
            if (!Matches(asset.PackageName.ToString(), filters))
            {
                result.OutsideFilter++;
                continue;
            }
            if (!known.Add(path))
            {
                result.AlreadyInBase.Add(path);
                continue;
            }
            target.Assets.Add(asset);
            result.Added.Add(path);
        }

        result.Data = Write(target);
        Verify(result, target);
        return result;
    }

    public static IReadOnlyList<string> ParseFilters(string text) =>
        (text ?? "").Split([';', ',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => "/" + f.Replace('\\', '/').Trim('/')).Where(f => f.Length > 1).ToList();

    private static bool Matches(string packageName, IReadOnlyList<string> filters)
    {
        if (filters.Count == 0) return true;
        foreach (var filter in filters)
        {
            if (packageName.Length == filter.Length ? packageName.Equals(filter, StringComparison.OrdinalIgnoreCase)
                    : packageName.StartsWith(filter, StringComparison.OrdinalIgnoreCase) && packageName[filter.Length] == '/')
                return true;
        }
        return false;
    }

    /// <summary>reads the written registry back and makes sure it is the merged one</summary>
    private static void Verify(Result result, Registry expected)
    {
        try
        {
            var reloaded = Read(result.Data, "書き出した AssetRegistry");
            if (reloaded.Assets.Count != expected.Assets.Count) result.VerificationErrors.Add($"アセット数が一致しません（{reloaded.Assets.Count} / {expected.Assets.Count}）");
            if (reloaded.DependsNodes.Count != expected.DependsNodes.Count) result.VerificationErrors.Add("依存ノード数が一致しません");
            if (reloaded.Packages.Count != expected.Packages.Count) result.VerificationErrors.Add("パッケージ情報の数が一致しません");
            for (var i = 0; i < Math.Min(reloaded.Assets.Count, expected.Assets.Count) && result.VerificationErrors.Count < 10; i++)
            {
                var a = reloaded.Assets[i];
                var b = expected.Assets[i];
                if (a.ObjectPath != b.ObjectPath || a.PackagePath != b.PackagePath || a.AssetClass != b.AssetClass || a.PackageName != b.PackageName ||
                    a.AssetName != b.AssetName || a.PackageFlags != b.PackageFlags || !a.ChunkIds.SequenceEqual(b.ChunkIds) || !a.Tags.SequenceEqual(b.Tags))
                    result.VerificationErrors.Add($"{b.ObjectPath}: 読み直した内容が一致しません");
            }

            // and the way the game reads it
            using var archive = new CUE4Parse.UE4.Readers.FByteArchive("AssetRegistry.bin", result.Data);
            var state = new CUE4Parse.UE4.AssetRegistry.FAssetRegistryState(archive);
            if (state.PreallocatedAssetDataBuffers.Length != expected.Assets.Count)
                result.VerificationErrors.Add($"CUE4Parse で読んだアセット数が一致しません（{state.PreallocatedAssetDataBuffers.Length} / {expected.Assets.Count}）");
        }
        catch (Exception e)
        {
            result.VerificationErrors.Add($"書き出した AssetRegistry を読み込めませんでした: {e.Message}");
        }
    }

    public static Registry Read(byte[] data, string label)
    {
        using var reader = new BinaryReader(new MemoryStream(data, false), Encoding.UTF8);
        if (data.Length < 28 || !data.AsSpan(0, 16).SequenceEqual(VersionGuid))
            throw new InvalidDataException($"{label}: AssetRegistry のファイルではありません");
        reader.BaseStream.Position = 16;

        var registry = new Registry { Version = reader.ReadInt32() };
        if (registry.Version is < MinVersion or > MaxVersion)
            throw new NotSupportedException($"{label}: バージョン {registry.Version} の AssetRegistry には対応していません（対応は名前表形式の {MinVersion}〜{MaxVersion}、UE4.19〜4.25 頃）");

        var nameOffset = reader.ReadInt64();
        if (nameOffset <= 0 || nameOffset >= data.Length) throw new InvalidDataException($"{label}: 名前表の位置が壊れています");
        var bodyStart = reader.BaseStream.Position;
        reader.BaseStream.Position = nameOffset;
        var names = new string[CheckCount(reader.ReadInt32(), data.Length)];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = ReadString(reader);
            reader.BaseStream.Position += 4; // hashes
        }
        reader.BaseStream.Position = bodyStart;

        Name ReadName()
        {
            var index = reader.ReadInt32();
            var number = reader.ReadInt32();
            if ((uint) index >= names.Length) throw new InvalidDataException($"{label}: 名前の番号 {index} が名前表（{names.Length}）の外です");
            return new Name(names[index], number);
        }

        var assetCount = CheckCount(reader.ReadInt32(), data.Length);
        for (var i = 0; i < assetCount; i++)
        {
            var asset = new AssetData
            {
                ObjectPath = ReadName(), PackagePath = ReadName(), AssetClass = ReadName(), PackageName = ReadName(), AssetName = ReadName()
            };
            var tagCount = CheckCount(reader.ReadInt32(), data.Length);
            for (var t = 0; t < tagCount; t++) asset.Tags.Add((ReadName(), ReadString(reader)));
            asset.ChunkIds = ReadInts(reader, CheckCount(reader.ReadInt32(), data.Length));
            asset.PackageFlags = reader.ReadUInt32();
            registry.Assets.Add(asset);
        }

        var nodeCount = CheckCount(reader.ReadInt32(), data.Length);
        for (var i = 0; i < nodeCount; i++)
        {
            var node = new DependsNode { FieldBits = reader.ReadByte() };
            for (var bit = 0; bit < 4; bit++)
                if ((node.FieldBits & (1 << bit)) != 0) node.Identifier[bit] = ReadName();

            node.Counts[0] = reader.ReadInt32();
            node.Counts[1] = reader.ReadInt32();
            node.Counts[2] = reader.ReadInt32();
            node.Counts[3] = reader.ReadInt32();
            node.Counts[4] = registry.Version >= AddedHardManage ? reader.ReadInt32() : 0;
            node.Counts[5] = reader.ReadInt32();
            node.Indices = ReadInts(reader, CheckCount(node.Counts.Sum(), data.Length));
            registry.DependsNodes.Add(node);
        }

        var packageCount = CheckCount(reader.ReadInt32(), data.Length);
        for (var i = 0; i < packageCount; i++)
        {
            var package = new PackageData { PackageName = ReadName(), DiskSize = reader.ReadInt64(), PackageGuid = reader.ReadBytes(16) };
            if (registry.Version >= AddedCookedMD5Hash && reader.ReadUInt32() != 0) package.CookedHash = reader.ReadBytes(16);
            registry.Packages.Add(package);
        }

        if (reader.BaseStream.Position != nameOffset)
            throw new InvalidDataException($"{label}: 読み終わりの位置（{reader.BaseStream.Position}）が名前表（{nameOffset}）と合いません");
        return registry;
    }

    /// <summary>FAssetRegistryState::Save with FNameTableArchiveWriter, names listed in the order they are first used</summary>
    public static byte[] Write(Registry registry)
    {
        var nameIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new List<string>();
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Encoding.UTF8, true);

        void WriteName(Name name)
        {
            if (!nameIndices.TryGetValue(name.Text, out var index))
            {
                index = names.Count;
                names.Add(name.Text);
                nameIndices[name.Text] = index;
            }
            w.Write(index);
            w.Write(name.Number);
        }

        w.Write(VersionGuid);
        w.Write(registry.Version);
        var nameOffsetPosition = stream.Position;
        w.Write(0L);

        w.Write(registry.Assets.Count);
        foreach (var asset in registry.Assets)
        {
            WriteName(asset.ObjectPath);
            WriteName(asset.PackagePath);
            WriteName(asset.AssetClass);
            WriteName(asset.PackageName);
            WriteName(asset.AssetName);
            w.Write(asset.Tags.Count);
            foreach (var (key, value) in asset.Tags)
            {
                WriteName(key);
                WriteString(w, value);
            }
            w.Write(asset.ChunkIds.Length);
            foreach (var id in asset.ChunkIds) w.Write(id);
            w.Write(asset.PackageFlags);
        }

        w.Write(registry.DependsNodes.Count);
        foreach (var node in registry.DependsNodes)
        {
            w.Write(node.FieldBits);
            for (var bit = 0; bit < 4; bit++)
                if ((node.FieldBits & (1 << bit)) != 0) WriteName(node.Identifier[bit]);
            w.Write(node.Counts[0]);
            w.Write(node.Counts[1]);
            w.Write(node.Counts[2]);
            w.Write(node.Counts[3]);
            if (registry.Version >= AddedHardManage) w.Write(node.Counts[4]);
            w.Write(node.Counts[5]);
            foreach (var index in node.Indices) w.Write(index);
        }

        w.Write(registry.Packages.Count);
        foreach (var package in registry.Packages)
        {
            WriteName(package.PackageName);
            w.Write(package.DiskSize);
            w.Write(package.PackageGuid);
            if (registry.Version >= AddedCookedMD5Hash)
            {
                w.Write(package.CookedHash != null ? 1u : 0u);
                if (package.CookedHash != null) w.Write(package.CookedHash);
            }
        }

        var nameOffset = stream.Position;
        w.Write(names.Count);
        foreach (var name in names)
        {
            WriteString(w, name);
            w.Write((ushort) (NameHashes.Strihash(name) & 0xFFFF));
            w.Write((ushort) (NameHashes.StrCrc32(name) & 0xFFFF));
        }

        stream.Position = nameOffsetPosition;
        w.Write(nameOffset);
        w.Flush();
        return stream.ToArray();
    }

    private static int CheckCount(int count, long limit)
    {
        if (count < 0 || count > limit) throw new InvalidDataException($"要素数 {count} が壊れています");
        return count;
    }

    private static int[] ReadInts(BinaryReader reader, int count)
    {
        var values = new int[count];
        for (var i = 0; i < count; i++) values[i] = reader.ReadInt32();
        return values;
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length == 0) return "";
        if (length > 0)
        {
            var bytes = reader.ReadBytes(length);
            return Encoding.Latin1.GetString(bytes, 0, length - 1);
        }
        var wide = reader.ReadBytes(-length * 2);
        return Encoding.Unicode.GetString(wide, 0, wide.Length - 2);
    }

    private static void WriteString(BinaryWriter w, string value)
    {
        if (value.Length == 0)
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

    /// <summary>the two deprecated name hashes UE4 still saves next to every name table entry</summary>
    private static class NameHashes
    {
        private static readonly uint[] DeprecatedTable = BuildDeprecated();
        private static readonly uint[] Table = BuildReflected();

        private static uint[] BuildDeprecated()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var crc = i << 24;
                for (var j = 0; j < 8; j++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
                table[i] = crc;
            }
            return table;
        }

        private static uint[] BuildReflected()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var crc = i;
                for (var j = 0; j < 8; j++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
                table[i] = crc;
            }
            return table;
        }

        // FCrc::Strihash_DEPRECATED, the ansi version for ansi names and the wide one (two bytes per character) otherwise
        public static uint Strihash(string value)
        {
            var wide = value.Any(c => c >= 128);
            uint hash = 0;
            foreach (var c in value)
            {
                var upper = c is >= 'a' and <= 'z' ? (char) (c - 32) : c; // TChar::ToUpper only maps a-z
                hash = ((hash >> 8) & 0x00FFFFFF) ^ DeprecatedTable[(hash ^ (byte) upper) & 0xFF];
                if (wide) hash = ((hash >> 8) & 0x00FFFFFF) ^ DeprecatedTable[(hash ^ (byte) (upper >> 8)) & 0xFF];
            }
            return hash;
        }

        // FCrc::StrCrc32, every character as four bytes
        public static uint StrCrc32(string value)
        {
            var crc = 0xFFFFFFFFu;
            foreach (uint c in value)
            {
                var ch = c;
                for (var i = 0; i < 4; i++)
                {
                    crc = (crc >> 8) ^ Table[(crc ^ ch) & 0xFF];
                    ch >>= 8;
                }
            }
            return ~crc;
        }
    }
}
