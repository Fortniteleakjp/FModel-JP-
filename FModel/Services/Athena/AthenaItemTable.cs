using System;
using System.Linq;

namespace FModel.Services.Athena;

/// <summary>
/// アセットのクラス名 / アイテム ID から Athena プロファイルの backend type を解決するテーブル。
/// djlorenzouasset/Athena (Services/AssetsService.cs, Utils/Utils.cs) の定義を移植したもの。
/// </summary>
public static class AthenaItemTable
{
    public sealed class ItemEntry
    {
        public string BackendType { get; init; } = "TBD";
        public string[] ClassNames { get; init; } = [];
        public string[] Prefixes { get; init; } = [];
        public string[] IncludeNames { get; init; } = [];
    }

    private static readonly StringComparer _comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// コスメティクスが置かれているディレクトリ。右クリックされたアセットの絞り込みに使う。
    /// </summary>
    public static readonly string[] CosmeticDirectories =
    [
        "Athena/Items/Cosmetics/",
        "GameFeatures/DefaultCosmeticsAthena/",
        "GameFeatures/CosmeticCompanions/",
        "GameFeatures/MeshCosmetics/",
        "GameFeatures/CosmeticShoes/",
        "GameFeatures/SparksCosmetics/",
        "GameFeatures/FM/SparksSongTemplates/",
        "GameFeatures/FM/SparksCosmetics/",
        "GameFeatures/FM/SparksCharacterCommon/",
        "GameFeatures/VehicleCosmetics/",
        "GameFeatures/Juno/"
    ];

    public static readonly ItemEntry[] Items =
    [
        new()
        {
            BackendType = "AthenaCharacter",
            ClassNames = ["AthenaCharacterItemDefinition"],
            Prefixes = ["CID_", "Character_"]
        },
        new()
        {
            BackendType = "AthenaBackpack",
            ClassNames = ["AthenaBackpackItemDefinition", "AthenaPetCarrierItemDefinition"],
            Prefixes = ["BID_", "Backpack_", "PetCarrier_"],
            IncludeNames =
            [
                "Gadget_AlienSignalDetector",
                "Gadget_DetectorGadget",
                "Gadget_DetectorGadget_Ch4S2",
                "Gadget_HighTechBackpack",
                "Gadget_RealityBloom",
                "Gadget_SpiritVessel"
            ]
        },
        new()
        {
            BackendType = "AthenaPickaxe",
            ClassNames = ["AthenaPickaxeItemDefinition"],
            Prefixes = ["Pickaxe_"],
            IncludeNames =
            [
                "DefaultPickaxe",
                "DefaultPickaxe_Placeholder",
                "BoltonPickaxe",
                "Dev_Test_Pickaxe",
                "HalloweenScythe",
                "HappyPickaxe",
                "SickleBatPickaxe",
                "SkiIcePickaxe",
                "SpikyPickaxe"
            ]
        },
        new()
        {
            BackendType = "AthenaDance",
            ClassNames =
            [
                "AthenaDanceItemDefinition",
                "AthenaSprayItemDefinition",
                "AthenaToyItemDefinition",
                "AthenaEmojiItemDefinition"
            ],
            Prefixes = ["EID_", "Spray_", "Spid_", "Toy_", "Emoji_", "Emoticon_"]
        },
        new()
        {
            BackendType = "AthenaGlider",
            ClassNames = ["AthenaGliderItemDefinition"],
            Prefixes = ["Glider_", "Umbrella_"],
            IncludeNames =
            [
                "DefaultGlider",
                "DefaultGlider_Placeholder",
                "Duo_Umbrella",
                "FounderGlider",
                "FounderUmbrella",
                "PreSeasonGlider",
                "PreSeasonGlider_Elite",
                "Solo_Umbrella",
                "Solo_Umbrella_MarkII",
                "Squad_Umbrella"
            ]
        },
        new()
        {
            BackendType = "AthenaItemWrap",
            ClassNames = ["AthenaItemWrapDefinition"],
            Prefixes = ["Wrap_"],
            IncludeNames = ["ChillyFabric"]
        },
        new()
        {
            BackendType = "AthenaSkyDiveContrail",
            ClassNames = ["AthenaSkyDiveContrailItemDefinition"],
            Prefixes = ["Contrail_", "Trails_"],
            IncludeNames = ["DefaultContrail"]
        },
        new()
        {
            BackendType = "AthenaMusicPack",
            ClassNames = ["AthenaMusicPackItemDefinition"],
            Prefixes = ["MusicPack_"]
        },
        new()
        {
            BackendType = "AthenaLoadingScreen",
            ClassNames = ["AthenaLoadingScreenItemDefinition"],
            Prefixes = ["LoadingScreen_", "LSID_"]
        },
        new()
        {
            BackendType = "CosmeticShoes",
            ClassNames = ["CosmeticShoesItemDefinition"],
            Prefixes = ["Shoes_"]
        },
        new()
        {
            BackendType = "CosmeticMimosa",
            ClassNames = ["CosmeticCompanionItemDefinition"],
            Prefixes = ["Companion_"],
            IncludeNames = ["Mimosa_Random"]
        },
        new()
        {
            BackendType = "CosmeticMimosaC",
            ClassNames = ["CosmeticCompanionReactFXItemDefinition"],
            Prefixes = ["Companion_ReactFx_"]
        },
        new()
        {
            BackendType = "CosmeticVariantToken",
            ClassNames = [],
            Prefixes = ["VTID_"]
        },
        new()
        {
            BackendType = "SparksMicrophone",
            ClassNames = ["SparksMicItemDefinition"],
            Prefixes = ["Mic_", "Sparks_"]
        },
        new()
        {
            BackendType = "SparksKeyboard",
            ClassNames = ["SparksKeyboardItemDefinition"],
            Prefixes = ["Keytar_", "Sparks_"]
        },
        new()
        {
            BackendType = "SparksGuitar",
            ClassNames = ["SparksGuitarItemDefinition"],
            Prefixes = ["Guitar_", "Sparks_"]
        },
        new()
        {
            BackendType = "SparksDrums",
            ClassNames = ["SparksDrumItemDefinition"],
            Prefixes = ["Drum_", "DrumKit_", "Sparks_"]
        },
        new()
        {
            BackendType = "SparksBass",
            ClassNames = ["SparksBassItemDefinition"],
            Prefixes = ["Bass_", "Sparks_"]
        },
        new()
        {
            BackendType = "SparksAura",
            ClassNames = ["SparksAuraItemDefinition"],
            Prefixes = ["Aura_", "Sparks_"]
        },
        new()
        {
            BackendType = "SparksSong",
            ClassNames = ["SparksSongItemDefinition"],
            Prefixes = ["SID_", "Sparks_"]
        },
        new()
        {
            BackendType = "VehicleCosmetics_Body",
            ClassNames = ["FortVehicleCosmeticsItemDefinition_Body"],
            Prefixes = ["Body_", "CarBody_"]
        },
        new()
        {
            BackendType = "VehicleCosmetics_Skin",
            ClassNames = ["FortVehicleCosmeticsItemDefinition_Skin"],
            Prefixes = ["CarSkin_", "ID_Skin_"]
        },
        new()
        {
            BackendType = "VehicleCosmetics_Booster",
            ClassNames = ["FortVehicleCosmeticsItemDefinition_Booster"],
            Prefixes = ["Booster_", "ID_Booster_"]
        },
        new()
        {
            BackendType = "VehicleCosmetics_Wheel",
            ClassNames = ["FortVehicleCosmeticsItemDefinition_Wheel"],
            Prefixes = ["Wheel_", "ID_Wheel_"]
        },
        new()
        {
            BackendType = "VehicleCosmetics_DriftTrail",
            ClassNames = ["FortVehicleCosmeticsItemDefinition_DriftTrail"],
            Prefixes = ["DriftTrail_", "ID_DriftTrail_"]
        },
        new()
        {
            BackendType = "JunoBuildingProp",
            ClassNames = ["JunoBuildingPropAccountItemDefinition"],
            Prefixes = ["JBPID_"]
        },
        new()
        {
            BackendType = "JunoBuildingSet",
            ClassNames = ["JunoBuildingSetAccountItemDefinition"],
            Prefixes = ["JBSID_"]
        },
        new()
        {
            // Athena 側でも backend type が未確定 (TBD) 扱いのもの
            BackendType = "TBD",
            ClassNames = ["JunoBuildInstructionsItemDefinition"],
            Prefixes = ["JBID_"]
        }
    ];

    public static bool IsValidClass(string exportClass)
        => !string.IsNullOrEmpty(exportClass) && Items.Any(item => item.ClassNames.Contains(exportClass, _comparer));

    public static bool IsValidItemId(string itemId)
        => !string.IsNullOrEmpty(itemId) && Items.Any(item => item.IncludeNames.Contains(itemId, _comparer));

    public static bool IsValidPrefix(string itemId)
        => !string.IsNullOrEmpty(itemId) && Items.Any(item => item.Prefixes.Any(p => itemId.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// アセット名だけを見てコスメティクスの可能性があるか判定する。
    /// フォルダを丸ごと指定されたときに、パッケージを読み込む前の足切りに使う。
    /// </summary>
    public static bool IsCosmeticName(string itemId) => IsValidPrefix(itemId) || IsValidItemId(itemId);

    public static string GetBackendTypeByClass(string exportClass)
        => Items.FirstOrDefault(item => item.ClassNames.Contains(exportClass, _comparer))?.BackendType ?? "TBD";

    public static string GetBackendTypeByPrefix(string prefix)
        => Items.FirstOrDefault(item => item.Prefixes.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))?.BackendType ?? "TBD";

    public static string GetBackendTypeByIncludedName(string name)
        => Items.FirstOrDefault(item => item.IncludeNames.Contains(name, _comparer))?.BackendType ?? "TBD";

    /// <summary>
    /// アイテム ID (アセット名) だけから backend type を推測する。クラス名で解決できなかった場合のフォールバック。
    /// </summary>
    public static string GetBackendTypeByItemId(string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return "TBD";
        if (IsValidItemId(itemId)) return GetBackendTypeByIncludedName(itemId);

        string[] instruments = ["Mic", "Keytar", "Guitar", "Drum", "DrumKit", "Bass"];

        string prefix;
        if (itemId.StartsWith("Companion_ReactFX", StringComparison.OrdinalIgnoreCase))
        {
            prefix = "Companion_ReactFx";
        }
        else if (itemId.StartsWith("SparksAura", StringComparison.OrdinalIgnoreCase))
        {
            prefix = "Aura";
        }
        else if (itemId.StartsWith("Sparks", StringComparison.OrdinalIgnoreCase))
        {
            var parts = itemId.Split('_');
            var last = parts[^1];
            prefix = instruments.Contains(last, _comparer) ? last : parts.Length > 1 ? parts[1] : itemId;
        }
        else
        {
            var separator = itemId.IndexOf('_');
            if (separator < 1) return "TBD";
            prefix = itemId[..separator];
        }

        if (prefix.Equals("ID", StringComparison.OrdinalIgnoreCase))
        {
            var parts = itemId.Split('_');
            if (parts.Length < 2) return "TBD";
            prefix = parts[1];
        }

        return GetBackendTypeByPrefix(prefix);
    }
}
