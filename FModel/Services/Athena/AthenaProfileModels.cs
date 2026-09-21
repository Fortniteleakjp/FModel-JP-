using System;
using System.Collections.Generic;
using FModel.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace FModel.Services.Athena;

/// <summary>
/// profile_athena.json の構造。djlorenzouasset/Athena (Models/Profiles) の移植。
/// フィールド名は <see cref="AthenaProfileBuilder"/> の camelCase リゾルバでシリアライズされる。
/// </summary>
public class ProfileAthena
{
    public string Id = "aa479043-3602-40be-959b-853003ea8fce";
    public DateTime Created = DateTime.UtcNow;
    public DateTime Updated = DateTime.UtcNow;
    public int Rvn = 100;
    public int WipeNumber = 1;
    public string AccountId = UserSettings.Default.AthenaProfileId;
    public string ProfileId = "athena";
    public string Version = string.Empty;
    public Dictionary<string, IProfileItem> Items = new() { { "sandbox_loadout", new Loadout() } };
    public ProfileStats Stats = new();
    public int CommandRevision = 100;
}

public interface IProfileItem;

public class Loadout : IProfileItem
{
    public string TemplateId = "CosmeticLocker:cosmeticlocker_athena";
    public LockerAttributes Attributes = new();
    public int Quantity = 1;
}

public class LockerAttributes
{
    [JsonProperty("locker_slots_data")] public LockerSlotsData LockerSlotsData = new();
    [JsonProperty("use_count")] public int UseCount = 1;
    [JsonProperty("banner_icon_template")] public string BannerIconTemplate = "BRS11_Prestige5";
    [JsonProperty("locker_name")] public string LockerName = UserSettings.Default.AthenaProfileId;
    [JsonProperty("banner_color_template")] public string BannerColorTemplate = "DefaultColor40";
    [JsonProperty("item_seen")] public bool ItemSeen = false;
    public bool Favorite = false;
}

public class LockerSlotsData
{
    public Slots Slots = new();
}

[JsonObject(NamingStrategyType = typeof(DefaultNamingStrategy))]
public class Slots
{
    public Pickaxe Pickaxe = new();
    public Dance Dance = new();
    public Glider Glider = new();
    public Character Character = new();
    public Backpack Backpack = new();
    public ItemWrap ItemWrap = new();
    public LoadingScreen LoadingScreen = new();
    public MusicPack MusicPack = new();
    public SkydiveContrail SkyDiveContrail = new();
}

public class Pickaxe
{
    public List<string> Items = [""];
    public List<ActiveVariant> ActiveVariants = [];
}

public class Dance
{
    public List<string> Items = [""];
}

public class Glider
{
    public List<string> Items = [""];
}

public class Character
{
    public List<string> Items = ["AthenaCharacter:CID_001_Athena_Commando_F_Default"];
    public List<ActiveVariant> ActiveVariants = [];
}

public class Backpack
{
    public List<string> Items = [""];
    public List<ActiveVariant> ActiveVariants = [];
}

public class ItemWrap
{
    public List<string> Items = [""];
    public List<ActiveVariant> ActiveVariants = [];
}

public class LoadingScreen
{
    public List<string> Items = [""];
    public List<ActiveVariant> ActiveVariants = [];
}

public class MusicPack
{
    public List<string> Items = [""];
    public List<ActiveVariant> ActiveVariants = [];
}

public class SkydiveContrail
{
    public List<string> Items = [""];
    public List<ActiveVariant> ActiveVariants = [];
}

public class ActiveVariant
{
    public List<Variant> Variants = [];
}

public class Variant
{
    public string Channel { get; set; }
    public string Active { get; set; }
    public List<string> Owned { get; set; }
}

public class Cosmetic : IProfileItem
{
    public string TemplateId;
    public CosmeticAttributes Attributes = new();
    public int Quantity = 1;

    public Cosmetic(string cosmeticId, string backendType, List<Variant> variants)
    {
        TemplateId = $"{backendType}:{cosmeticId}";
        if (variants.Count > 0)
            Attributes.Variants.AddRange(variants);
    }
}

public class CosmeticAttributes
{
    // CreationTime は 18.00 以降でバリアントが反映されなくなるため、Athena 側と同じく持たせない
    public int MaxLevelBonus = 0;
    public int Level = 1;
    public bool ItemSeen = true;
    public int RndSelCnt = 0;
    public int Xp = 0;
    public List<Variant> Variants = [];
    public bool Favorite = false;
    public bool Archived = false;
}

public class ProfileStats
{
    public StatAttributes Attributes = new();
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public class StatAttributes
{
    public int SeasonMatchBoost = 999999;
    public List<string> Loadouts = ["sandbox_loadout"];
    public int RestedXpOverflow = 0;
    public bool MfaRewardClaimed = true;
    public QuestManager QuestManager = new();
    public int BookLevel = 1;
    public int SeasonNum = 999999;
    public int SeasonUpdate = 1;
    public int BookXp = 1;
    public List<object> Permissions = [];
    public bool BookPurchased = true;
    public int LifetimeWins = 1;
    public string PartyAssistQuest = string.Empty;
    public List<object> PurchasedBattlePassTierOffers = [];
    public float RestedXpExchange = 1f;
    public int Level = UserSettings.Default.AthenaBattlePassLevel;
    public long XpOverflow = 1;
    public int RestedXp = 1;
    public float RestedXpMult = 1f;
    public int AccountLevel = UserSettings.Default.AthenaBattlePassLevel;
    public CompetitiveIdentity CompetitiveIdentity = new();
    public int InventoryLimitBonus = 0;
    public string LastAppliedLoadout = "sandbox_loadout";
    public DailyRewards DailyRewards = new();
    public int Xp = 10;
    public int SeasonFriendMatchBoost = 1;
    public int ActiveLoadoutIndex = 0;
    public List<PastSeasons> PastSeasons = [];
}

// 中身を持たない (ゲーム側が存在だけを見ている) オブジェクト
public class QuestManager;

public class CompetitiveIdentity;

public class DailyRewards;

public class PastSeasons;
