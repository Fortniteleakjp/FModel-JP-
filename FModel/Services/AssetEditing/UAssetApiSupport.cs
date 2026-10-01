using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Versions;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace FModel.Services.AssetEditing;

/// <summary>
/// bridges what CUE4Parse knows about the game (engine version, mappings) to UAssetAPI
/// </summary>
public static class UAssetApiSupport
{
    // one converted usmap per loaded mappings instance, Fortnite's take a couple of seconds to rebuild
    private static readonly ConditionalWeakTable<TypeMappings, Usmap> _usmaps = new();

    public static EngineVersion ToEngineVersion(EGame game)
    {
        var value = (int) game;
        var major = value >> 24;
        var minor = (value >> 16) & 0xFF;

        switch (major)
        {
            case >= 6:
                return EngineVersion.VER_UE5_8; // newest UAssetAPI knows about, closest layout
            case 5:
            {
                if (game == EGame.GAME_UE5_EA) return EngineVersion.VER_UE5_0EA;
                return Enum.TryParse<EngineVersion>($"VER_UE5_{minor}", out var ue5) ? ue5 : EngineVersion.VER_UE5_8;
            }
            case 4:
                return Enum.TryParse<EngineVersion>($"VER_UE4_{minor}", out var ue4) ? ue4 : EngineVersion.VER_UE4_27;
            default:
                return EngineVersion.UNKNOWN;
        }
    }

    /// <summary>
    /// UAssetAPI's FString reader stackallocs "-length * 2" bytes: a misread length overflows that into a stack overflow,
    /// which kills the process instead of failing the read. Same reading, with the length checked first.
    /// </summary>
    public sealed class SafeAssetBinaryReader(Stream stream, UAsset asset) : AssetBinaryReader(stream, asset)
    {
        private const int MaxLength = 1024 * 1024;

        public override FString ReadFString()
        {
            var length = ReadInt32();
            switch (length)
            {
                case 0:
                    return null;
                case < 0:
                {
                    if (length < -MaxLength) throw new InvalidOperationException($"Invalid FString length: {length}");
                    var data = ReadBytes(-length * 2);
                    if (data.Length != -length * 2) throw new EndOfStreamException();
                    return new FString(System.Text.Encoding.Unicode.GetString(data, 0, data.Length - 2), System.Text.Encoding.Unicode);
                }
                default:
                {
                    if (length > MaxLength) throw new InvalidOperationException($"Invalid FString length: {length}");
                    var data = ReadBytes(length);
                    if (data.Length != length) throw new EndOfStreamException();
                    return new FString(System.Text.Encoding.UTF8.GetString(data, 0, data.Length - 1), System.Text.Encoding.UTF8);
                }
            }
        }
    }

    /// <summary>
    /// a private copy for one asset: UAssetAPI adds the schemas of the structs/classes/functions an asset defines to its mappings
    /// while reading it, sharing one instance would let names from one package (an "ExecuteUbergraph_X", a "Foo_C") leak into the next
    /// </summary>
    public static Usmap GetMappings(TypeMappings mappings)
    {
        if (mappings == null) return null;

        Usmap usmap;
        lock (_usmaps)
        {
            if (!_usmaps.TryGetValue(mappings, out usmap))
            {
                usmap = new Usmap();
                using (var stream = new MemoryStream(UsmapWriter.Write(mappings)))
                {
                    usmap.ReadUSMAP(new UsmapBinaryReader(stream, usmap));
                }
                _usmaps.AddOrUpdate(mappings, usmap);
            }
        }

        var comparer = StringComparer.InvariantCultureIgnoreCase;
        return new Usmap
        {
            Version = usmap.Version,
            FileVersionUE4 = usmap.FileVersionUE4,
            FileVersionUE5 = usmap.FileVersionUE5,
            CustomVersionContainer = usmap.CustomVersionContainer,
            NetCL = usmap.NetCL,
            NameMap = usmap.NameMap,
            EnumMap = new ConcurrentDictionary<string, UsmapEnum>(usmap.EnumMap, comparer),
            Schemas = new ConcurrentDictionary<string, UsmapSchema>(usmap.Schemas, comparer)
        };
    }
}
