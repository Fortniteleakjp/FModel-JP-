using System.Collections.Generic;
using System.Text.Json.Serialization;

// データモデル

// Root object
public class AthenaProfile
{
    [JsonPropertyName("_id")]
    public string _id { get; set; }

    [JsonPropertyName("created")]
    public string Created { get; set; }

    [JsonPropertyName("updated")]
    public string Updated { get; set; }

    [JsonPropertyName("rvn")]
    public int Rvn { get; set; }

    [JsonPropertyName("wipeNumber")]
    public int WipeNumber { get; set; }

    [JsonPropertyName("accountId")]
    public string AccountId { get; set; }

    [JsonPropertyName("profileId")]
    public string ProfileId { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; }

    [JsonPropertyName("items")]
    public Dictionary<string, AthenaItem> Items { get; set; }

    [JsonPropertyName("stats")]
    public Stats Stats { get; set; }

    [JsonPropertyName("commandRevision")]
    public int CommandRevision { get; set; }
}

public class AthenaItem
{
    [JsonPropertyName("templateId")]
    public string TemplateId { get; set; }

    [JsonPropertyName("attributes")]
    public ItemAttributes Attributes { get; set; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }
}

public class ItemAttributes
{
    [JsonPropertyName("locker_slots_data")]
    public LockerSlots LockerSlots { get; set; }

    [JsonPropertyName("max_level_bonus")]
    public int MaxLevelBonus { get; set; }

    [JsonPropertyName("level")]
    public int Level { get; set; }

    [JsonPropertyName("item_seen")]
    public bool ItemSeen { get; set; }

    [JsonPropertyName("xp")]
    public int Xp { get; set; }

    [JsonPropertyName("variants")]
    public List<Variant> Variants { get; set; }

    [JsonPropertyName("favorite")]
    public bool Favorite { get; set; }

    [JsonPropertyName("use_count")]
    public int UseCount { get; set; }

    [JsonPropertyName("banner_icon_template")]
    public string BannerIconTemplate { get; set; }

    [JsonPropertyName("banner_color_template")]
    public string BannerColorTemplate { get; set; }

    [JsonPropertyName("locker_name")]
    public string LockerName { get; set; }

    // 正しい.jsonに存在するが、元のコードにない属性を追加
    [JsonPropertyName("rnd_sel_cnt")]
    public int RndSelCnt { get; set; }

    [JsonPropertyName("archived")]
    public bool Archived { get; set; }
}

public class LockerSlots
{
    [JsonPropertyName("slots")]
    public Dictionary<string, SlotData> Slots { get; set; }
}

public class SlotData
{
    [JsonPropertyName("items")]
    public List<string> Items { get; set; }

    [JsonPropertyName("activeVariants")]
    public List<string?> ActiveVariants { get; set; }
}

public class Variant
{
    [JsonPropertyName("channel")]
    public string Channel { get; set; }

    [JsonPropertyName("active")]
    public string Active { get; set; }

    [JsonPropertyName("owned")]
    public List<string> Owned { get; set; }
}

public class Stats
{
    [JsonPropertyName("attributes")]
    public StatsAttributes Attributes { get; set; }
}

public class StatsAttributes
{
    [JsonPropertyName("past_seasons")]
    public List<PastSeasonData> PastSeasons { get; set; }

    [JsonPropertyName("season_match_boost")]
    public long SeasonMatchBoost { get; set; }

    [JsonPropertyName("loadouts")]
    public List<string> Loadouts { get; set; }

    [JsonPropertyName("last_applied_loadout")]
    public string LastAppliedLoadout { get; set; }

    [JsonPropertyName("banner_icon")]
    public string BannerIcon { get; set; }

    [JsonPropertyName("banner_color")]
    public string BannerColor { get; set; }

    [JsonPropertyName("xp")]
    public int Xp { get; set; }

    [JsonPropertyName("level")]
    public int Level { get; set; }

    [JsonPropertyName("favorite_character")]
    public string FavoriteCharacter { get; set; }

    [JsonPropertyName("loadout_presets")]
    public Dictionary<string, SlotData> LoadoutPresets { get; set; }
}

// past_seasons の配列要素のデータモデル
public class PastSeasonData
{
    [JsonPropertyName("seasonNumber")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("numWins")]
    public int NumWins { get; set; }

    [JsonPropertyName("seasonXp")]
    public int SeasonXp { get; set; }

    [JsonPropertyName("seasonLevel")]
    public int SeasonLevel { get; set; }

    [JsonPropertyName("bookXp")]
    public int BookXp { get; set; }

    [JsonPropertyName("bookLevel")]
    public int BookLevel { get; set; }

    [JsonPropertyName("purchasedVIP")]
    public bool PurchasedVIP { get; set; }
}
