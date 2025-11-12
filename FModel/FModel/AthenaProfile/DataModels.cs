using System.Collections.Generic;
using System.Text.Json.Serialization;

// データモデル
public class AthenaProfile
{
    public string Created { get; set; }
    public string Updated { get; set; }
    public int Rvn { get; set; }
    public int WipeNumber { get; set; }
    public string AccountId { get; set; }
    public string ProfileId { get; set; }
    public string Version { get; set; }
    public Dictionary<string, AthenaItem> Items { get; set; }
    public Stats Stats { get; set; }
    public int CommandRevision { get; set; }
}

public class AthenaItem
{
    public string TemplateId { get; set; }
    public ItemAttributes Attributes { get; set; }
    public int Quantity { get; set; }
}

public class ItemAttributes
{
    [JsonPropertyName("locker_slots_data")]
    public LockerSlots LockerSlots { get; set; }
    public int MaxLevelBonus { get; set; }
    public int Level { get; set; }
    public bool ItemSeen { get; set; }
    public int Xp { get; set; }
    public List<Variant> Variants { get; set; }
    public bool Favorite { get; set; }
    public int UseCount { get; set; }
    public string BannerIconTemplate { get; set; }
    public string BannerColorTemplate { get; set; }
    public string LockerName { get; set; }
}

public class LockerSlots
{
    public Dictionary<string, SlotData> Slots { get; set; }
}

public class SlotData
{
    public List<string> Items { get; set; }
    public List<string?> ActiveVariants { get; set; }
}

public class Variant
{
    public string Channel { get; set; }
    public string Active { get; set; }
    public List<string> Owned { get; set; }
}

public class Stats
{
    public StatsAttributes Attributes { get; set; }
}

public class StatsAttributes
{
    public List<string> PastSeasons { get; set; }
    public long SeasonMatchBoost { get; set; }
    public List<string> Loadouts { get; set; }
    public string LastAppliedLoadout { get; set; }
    public string BannerIcon { get; set; }
    public string BannerColor { get; set; }
    public int Xp { get; set; }
}
