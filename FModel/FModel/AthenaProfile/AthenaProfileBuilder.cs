using System.Collections.Generic;

public static class AthenaProfileBuilder
{
    public static AthenaProfile BuildBaseTemplate()
    {
        return new AthenaProfile
        {
            Created = "",
            Updated = "",
            Rvn = 1,
            WipeNumber = 1,
            AccountId = "",
            ProfileId = "athena",
            Version = "no_version",
            Items = new Dictionary<string, AthenaItem>
            {
                ["loadout1"] = new AthenaItem
                {
                    TemplateId = "CosmeticLocker:cosmeticlocker_athena",
                    Attributes = new ItemAttributes
                    {
                        LockerSlots = new LockerSlots
                        {
                            Slots = new Dictionary<string, SlotData>
                            {
                                ["MusicPack"] = new SlotData { Items = new List<string> { "" } },
                                ["Character"] = new SlotData { Items = new List<string> { "" }, ActiveVariants = new List<string?> { null } },
                                ["Backpack"] = new SlotData { Items = new List<string> { "" }, ActiveVariants = new List<string?> { null } },
                                ["SkyDiveContrail"] = new SlotData { Items = new List<string> { "" }, ActiveVariants = new List<string?> { null } },
                                ["Dance"] = new SlotData { Items = new List<string> { "", "", "", "", "", "" } },
                                ["LoadingScreen"] = new SlotData { Items = new List<string> { "" } },
                                ["Pickaxe"] = new SlotData { Items = new List<string> { "" }, ActiveVariants = new List<string?> { null } },
                                ["Glider"] = new SlotData { Items = new List<string> { "" }, ActiveVariants = new List<string?> { null } },
                                ["ItemWrap"] = new SlotData { Items = new List<string> { "", "", "", "", "", "", "" }, ActiveVariants = new List<string?> { null, null, null, null, null, null, null } }
                            }
                        },
                        UseCount = 0,
                        BannerIconTemplate = "StandardBanner1",
                        BannerColorTemplate = "DefaultColor1",
                        LockerName = "",
                        ItemSeen = false,
                        Favorite = false
                    },
                    Quantity = 1
                }
            },
            Stats = new Stats
            {
                Attributes = new StatsAttributes
                {
                    PastSeasons = new List<string>(),
                    SeasonMatchBoost = 999999999,
                    Loadouts = new List<string> { "loadout1" },
                    LastAppliedLoadout = "loadout1",
                    BannerIcon = "StandardBanner1",
                    BannerColor = "DefaultColor1",
                    Xp = 999
                }
            },
            CommandRevision = 0
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
