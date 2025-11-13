using System.Collections.Generic;

public static class AthenaProfileBuilder
{
    public static AthenaProfile BuildBaseTemplate()
    {
        return new AthenaProfile
        {
            _id = "FmodelJP",
            Created = "2025-06-14T17:07:34.4704107+09:00",
            Updated = "2025-06-14T17:07:34.4704126+09:00",
            Rvn = 100,
            WipeNumber = 1,
            AccountId = "FmodelJP",
            ProfileId = "athena",
            Version = "FmodelJP",
            Items = new Dictionary<string, AthenaItem>
            {
                ["sandbox_loadout"] = new AthenaItem // loadout1を修正
                {
                    TemplateId = "CosmeticLocker:cosmeticlocker_athena",
                    Attributes = new ItemAttributes
                    {
                        LockerSlots = new LockerSlots
                        {
                            Slots = new Dictionary<string, SlotData>
                            {
                                // 正しい.jsonの構造に合わせ、ActiveVariantsを空リストに設定
                                ["MusicPack"] = new SlotData { Items = new List<string> { "" }, ActiveVariants = new List<string?>() },
                                ["Character"] = new SlotData { Items = new List<string> { "AthenaCharacter:CID_001_Athena_Commando_F_Default" }, ActiveVariants = new List<string?>() },
                                ["Backpack"] = new SlotData { Items = new List<string> { "" }, ActiveVariants = new List<string?>() },
                                ["SkyDiveContrail"] = new SlotData { Items = new List<string> { "" }, ActiveVariants = new List<string?>() },
                                ["Dance"] = new SlotData { Items = new List<string> { "" }, ActiveVariants = new List<string?>() },
                                ["LoadingScreen"] = new SlotData { Items = new List<string> { "" }, ActiveVariants = new List<string?>() },
                                ["Pickaxe"] = new SlotData { Items = new List<string> { "" }, ActiveVariants = new List<string?>() },
                                ["Glider"] = new SlotData { Items = new List<string> { "" }, ActiveVariants = new List<string?>() },
                                ["ItemWrap"] = new SlotData { Items = new List<string> { "" }, ActiveVariants = new List<string?>() }
                            }
                        },
                        MaxLevelBonus = 0,
                        Level = 1,
                        ItemSeen = false,
                        Xp = 0,
                        Variants = new List<Variant>(),
                        Favorite = false,
                        UseCount = 1,
                        BannerIconTemplate = "BRS11_Prestige5",
                        BannerColorTemplate = "DefaultColor40",
                        LockerName = "FmodelJP",
                        RndSelCnt = 0, // 新しい属性の初期化
                        Archived = false // 新しい属性の初期化
                    },
                    Quantity = 1
                }
            },
            Stats = new Stats
            {
                Attributes = new StatsAttributes
                {
                    PastSeasons = new List<PastSeasonData>(),
                    SeasonMatchBoost = 999999999,
                    Loadouts = new List<string> { "sandbox_loadout" }, // loadout1を修正
                    LastAppliedLoadout = "sandbox_loadout", // loadout1を修正
                    BannerIcon = "StandardBanner1",
                    BannerColor = "DefaultColor1",
                    Xp = 999,
                    Level = 1,
                    FavoriteCharacter = "AthenaCharacter:CID_001_Athena_Commando_F_Default",
                    LoadoutPresets = new Dictionary<string, SlotData>()
                }
            },
            CommandRevision = 100
        };
    }

    public static List<Variant> BuildVariants(List<ApiVariant> variants)
    {
        var result = new List<Variant>();
        if (variants == null)
            return result;

        foreach (var v in variants)
        {
            result.Add(new Variant
            {
                Channel = v.Channel ?? "",
                Active = v.Options != null && v.Options.Count > 0 ? v.Options[0].Tag ?? "" : "",
                Owned = v.Options?.ConvertAll(o => o.Tag ?? "") ?? new List<string>()
            });
        }
        return result;
    }
}
