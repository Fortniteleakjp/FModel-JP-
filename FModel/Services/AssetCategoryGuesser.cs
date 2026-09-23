using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace FModel.Services;

/// <summary>
/// Guesses an asset's base category from its path alone: extension, then UE naming conventions
/// (name prefix/suffix), then the nearest well-known folder name, then Fortnite item definition names.
/// The exact category needs the package header, which is far too slow and memory hungry for the
/// millions of files the search lists. Nothing is stored per file, the guess is recomputed on every
/// filter pass, and only spans of the path are used: GameFile caches Name/Extension on first access,
/// so touching them here would keep millions of extra strings alive.
/// </summary>
public static class AssetCategoryGuesser
{
    /// <summary>Returned when nothing matched ("other").</summary>
    public const EAssetCategory Unknown = EAssetCategory.All;

    private static readonly FrozenDictionary<string, EAssetCategory> _extensions = Build(StringComparer.OrdinalIgnoreCase,
        (EAssetCategory.Level, ["umap"]),
        (EAssetCategory.Texture, ["png", "jpg", "jpeg", "tga", "dds", "bmp", "psd", "exr", "hdr", "svg"]),
        (EAssetCategory.Media, ["wem", "bnk", "wav", "ogg", "mp3", "flac", "awb", "acb", "bank", "xvag", "at9", "binka", "opus",
            "bk2", "bik", "mp4", "webm", "usm", "ufont", "ttf", "otf"]),
        (EAssetCategory.Data, ["json", "ini", "locres", "locmeta", "csv", "txt", "xml", "uplugin", "uproject", "bin", "usmap",
            "res", "license", "tps", "po", "udn", "uefnproject", "archive"]),
        (EAssetCategory.Blueprints, ["verse"]));

    // Anything else that is not a package extension stays unknown
    private static readonly FrozenSet<string> _packageExtensions =
        new[] { "uasset", "uexp", "ubulk", "uptnl", "uondemand" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Checked in order, the first match wins
    private static readonly (string Suffix, EAssetCategory Category)[] _suffixes =
    [
        ("_BuiltData", EAssetCategory.Level),
        ("_Skeleton", EAssetCategory.Animation),
        ("_Montage", EAssetCategory.Animation),
        ("_PhysicsAsset", EAssetCategory.Data),
        ("_Physics", EAssetCategory.Data),
        ("_AnimBP", EAssetCategory.Blueprints),
        ("_AnimBlueprint", EAssetCategory.Blueprints),
    ];

    // Case-sensitive on purpose: "T_Foo" is a texture, "t_foo" is just a name
    private static readonly (string Prefix, EAssetCategory Category)[][] _prefixesByFirstChar = BuildPrefixes(
        (EAssetCategory.Texture, ["T_", "T-", "TX_", "Tex_", "TEX_", "RT_", "TC_", "HDRI_", "LUT_"]),
        (EAssetCategory.Materials, ["M_", "MI_", "MIC_", "MID_", "MF_", "MFA_", "MPC_", "MM_", "ML_", "MLB_", "Mat_", "MAT_", "Material_"]),
        (EAssetCategory.Mesh, ["SM_", "SK_", "SKM_", "CO_", "Mesh_"]),
        (EAssetCategory.Animation, ["AM_", "AS_", "ANIM_", "Anim_", "BS_", "AO_", "SKEL_", "CR_"]),
        (EAssetCategory.Blueprints, ["BP_", "B_", "WBP_", "W_", "ABP_", "BPI_", "BPC_", "BFL_", "GA_", "GE_", "GC_", "GCN_", "GCNL_",
            "Enum_", "ENUM_", "Struct_", "STRUCT_", "GAB_", "BGA_", "PBW_", "PBWA_", "Prj_", "Device_"]),
        (EAssetCategory.Data, ["DT_", "DA_", "CT_", "ST_", "Curve_", "CRV_", "PA_", "PHYS_", "PM_", "PhysMat_", "BT_", "BB_", "TD_", "DAv2_"]),
        (EAssetCategory.Media, ["SW_", "SC_", "SFX_", "Sound_", "Snd_", "MS_", "MSS_", "Music_", "Mus_", "Play_", "Stop_", "VO_",
            "Font_", "FNT_", "Movie_", "MP_", "Ambience_"]),
        (EAssetCategory.Particle, ["P_", "PS_", "NS_", "NE_", "NPS_", "FX_"]),
        (EAssetCategory.Level, ["LS_", "SEQ_", "Seq_", "FT_"]));

    // Fortnite item definition names, only trusted when no folder said otherwise: an icon texture is often named after
    // its item ("PPIDs/Icons/PPID_...", "Glider_Foo/Textures/Glider_Foo_Wings")
    private static readonly string[] _itemDefinitionPrefixes =
    [
        "CP_", "Pickaxe_", "Glider_", "MusicPack_", "Character_", "Backpack_", "LoadingScreen_", "Spray_", "Emoji_", "Wrap_",
        "Toy_", "TOY_", "PetCarrier_", "Contrail_", "Trails_", "Quest_", "Playlist_", "Wheel_", "CarSkin_", "CarBody_"
    ];

    private static readonly FrozenDictionary<string, EAssetCategory> _folders = Build(StringComparer.OrdinalIgnoreCase,
        (EAssetCategory.Texture, ["Textures", "Texture", "Tex", "Icons", "GeneratedThumbnails", "PreviewImages", "2dAssets"]),
        (EAssetCategory.Materials, ["Materials", "Material", "MaterialFunctions", "MaterialInstances", "MaterialParameterCollections", "MaterialLibrary"]),
        (EAssetCategory.Mesh, ["Meshes", "Mesh", "StaticMeshes", "SkeletalMeshes", "StaticMesh", "SkeletalMesh", "Geometry",
            "NaniteDisplacement", "Mutable"]),
        (EAssetCategory.Animation, ["Animation", "Animations", "Anims", "Anim", "Skeletons", "Montages", "Poses"]),
        (EAssetCategory.Blueprints, ["Blueprints", "Blueprint", "BP", "Widgets", "UMG"]),
        (EAssetCategory.Data, ["DataTables", "DataTable", "DataAssets", "Data", "Curves", "CurveTables", "StringTables", "Cosmetics",
            "Items", "PlaysetProps", "TextureData", "Texture_Data", "CosmeticVariantTokens", "NewDisplayAssets", "QuestDisplayData",
            "Playlists", "PPIDs"]),
        (EAssetCategory.Media, ["Sounds", "Sound", "Audio", "Music", "SFX", "WwiseAudio", "Wwise", "Movies", "Movie", "Video", "Videos",
            "Fonts", "Font", "VO", "Dialogue", "MetaSounds", "SongParts", "Songs", "Wavs", "Waves", "OneShots", "EngineAudio", "VehicleAudio"]),
        (EAssetCategory.Particle, ["Particles", "Particle", "FX", "VFX", "Niagara", "Effects", "ParticleSystems"]),
        // World Partition stores every placed actor of a level as its own package there
        (EAssetCategory.Level, ["Maps", "Levels", "Sequences", "Cinematics", "Foliage", "__ExternalActors__", "__ExternalObjects__"]));

    private static readonly FrozenDictionary<string, EAssetCategory>.AlternateLookup<ReadOnlySpan<char>> _extensionLookup =
        _extensions.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> _packageExtensionLookup =
        _packageExtensions.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly FrozenDictionary<string, EAssetCategory>.AlternateLookup<ReadOnlySpan<char>> _folderLookup =
        _folders.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>The guessed base category, or <see cref="Unknown"/>.</summary>
    public static EAssetCategory Guess(string path)
    {
        var span = path.AsSpan();
        var slash = span.LastIndexOf('/');
        var directory = slash >= 0 ? span[..slash] : ReadOnlySpan<char>.Empty;
        var name = span[(slash + 1)..];

        var dot = name.LastIndexOf('.');
        var extension = dot >= 0 ? name[(dot + 1)..] : ReadOnlySpan<char>.Empty;
        var stem = dot >= 0 ? name[..dot] : name;
        if (stem.EndsWith(".o")) // optional package: "Foo.o.uasset"
            stem = stem[..^2];

        if (_extensionLookup.TryGetValue(extension, out var category))
            return category;
        if (!_packageExtensionLookup.Contains(extension))
            return Unknown;

        foreach (var (suffix, suffixCategory) in _suffixes)
        {
            if (stem.EndsWith(suffix, StringComparison.Ordinal))
                return suffixCategory;
        }

        if (stem.Length > 0 && stem[0] < _prefixesByFirstChar.Length && _prefixesByFirstChar[stem[0]] is { } prefixes)
        {
            foreach (var (prefix, prefixCategory) in prefixes)
            {
                if (stem.StartsWith(prefix, StringComparison.Ordinal))
                    return prefixCategory;
            }
        }

        // The nearest known folder wins: ".../Sounds/Emotes/Foo" is audio even though "Emotes" means nothing
        while (directory.Length > 0)
        {
            slash = directory.LastIndexOf('/');
            if (_folderLookup.TryGetValue(directory[(slash + 1)..], out category))
                return category;
            directory = slash >= 0 ? directory[..slash] : ReadOnlySpan<char>.Empty;
        }

        return IsItemDefinitionName(stem) ? EAssetCategory.Data : Unknown;
    }

    // Fortnite item definitions: "CID_", "PPID_", "VTID_", "JBPID_", "AccoladeId_", "Glider_"...
    private static bool IsItemDefinitionName(ReadOnlySpan<char> stem)
    {
        var underscore = stem.IndexOf('_');
        if (underscore is >= 2 and <= 12 && stem[..underscore].EndsWith("ID", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var prefix in _itemDefinitionPrefixes)
        {
            if (stem.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static FrozenDictionary<string, EAssetCategory> Build(StringComparer comparer, params (EAssetCategory Category, string[] Keys)[] groups)
        => groups.SelectMany(g => g.Keys.Select(k => KeyValuePair.Create(k, g.Category))).ToFrozenDictionary(comparer);

    private static (string, EAssetCategory)[][] BuildPrefixes(params (EAssetCategory Category, string[] Prefixes)[] groups)
    {
        var buckets = new (string, EAssetCategory)[128][];
        foreach (var bucket in groups
                     .SelectMany(g => g.Prefixes.Select(p => (Prefix: p, g.Category)))
                     .GroupBy(x => x.Prefix[0]))
        {
            // Longest first so "MIC_" is not taken by "MI_", and "BPI_" not by "BP_"
            buckets[bucket.Key] = bucket.OrderByDescending(x => x.Prefix.Length).Select(x => (x.Prefix, x.Category)).ToArray();
        }

        return buckets;
    }
}
