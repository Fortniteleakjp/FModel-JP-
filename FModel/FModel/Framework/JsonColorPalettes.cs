using System.Collections.Generic;

namespace FModel.Framework;

/// <summary>
/// JSON シンタックスハイライト配色プリセットの定義（真実の源）。
/// キーは Json.xshd の &lt;Color name="..."&gt; に対応する6ロール:
/// Bool / Number / String / Null / FieldName(キー) / Punctuation(記号)。
/// 各色は暗いエディタ背景での可読性・ロール間の識別性を検証済み。
/// </summary>
public static class JsonColorPalettes
{
    /// <summary>プレビュー表示用の役割の並び順（左から）。</summary>
    public static readonly string[] Roles = { "FieldName", "String", "Number", "Bool", "Null", "Punctuation" };

    /// <summary>役割の日本語ラベル（プレビューのツールチップ用）。</summary>
    public static readonly IReadOnlyDictionary<string, string> RoleLabels = new Dictionary<string, string>
    {
        ["FieldName"] = "キー名",
        ["String"] = "文字列",
        ["Number"] = "数値",
        ["Bool"] = "真偽値",
        ["Null"] = "null",
        ["Punctuation"] = "記号 { } [ ] : ,",
    };

    public static IReadOnlyDictionary<string, string> Get(EJsonColorScheme scheme)
        => All.TryGetValue(scheme, out var p) ? p : All[EJsonColorScheme.Default];

    public static readonly IReadOnlyDictionary<EJsonColorScheme, IReadOnlyDictionary<string, string>> All =
        new Dictionary<EJsonColorScheme, IReadOnlyDictionary<string, string>>
        {
            [EJsonColorScheme.Default] = new Dictionary<string, string>
            { ["Bool"] = "#4FC1FF", ["Number"] = "#FF9E64", ["String"] = "#A7E37E", ["Null"] = "#8B93A7", ["FieldName"] = "#FFD166", ["Punctuation"] = "#7DD3FC" },

            [EJsonColorScheme.PurpleCyan] = new Dictionary<string, string>
            { ["Bool"] = "#C792EA", ["Number"] = "#F78C6C", ["String"] = "#89DDFF", ["Null"] = "#7E839E", ["FieldName"] = "#C3A6FF", ["Punctuation"] = "#8B86B8" },

            [EJsonColorScheme.Dracula] = new Dictionary<string, string>
            { ["Bool"] = "#BD93F9", ["Number"] = "#FFB86C", ["String"] = "#50FA7B", ["Null"] = "#6272A4", ["FieldName"] = "#8BE9FD", ["Punctuation"] = "#FF79C6" },

            [EJsonColorScheme.Nord] = new Dictionary<string, string>
            { ["Bool"] = "#9FC6E0", ["Number"] = "#B48EAD", ["String"] = "#A3BE8C", ["Null"] = "#7B89A6", ["FieldName"] = "#88C0D0", ["Punctuation"] = "#EBCB8B" },

            [EJsonColorScheme.OneDark] = new Dictionary<string, string>
            { ["Bool"] = "#56B6C2", ["Number"] = "#D19A66", ["String"] = "#98C379", ["Null"] = "#5C6370", ["FieldName"] = "#E06C75", ["Punctuation"] = "#C678DD" },

            [EJsonColorScheme.ColorblindSafe] = new Dictionary<string, string>
            { ["Bool"] = "#56B4E9", ["Number"] = "#E69F00", ["String"] = "#9FE0A0", ["Null"] = "#7A7F99", ["FieldName"] = "#F0E442", ["Punctuation"] = "#B7C4DC" },

            [EJsonColorScheme.OceanTeal] = new Dictionary<string, string>
            { ["Bool"] = "#5AC8E8", ["Number"] = "#FFA552", ["String"] = "#A3D977", ["Null"] = "#6E7490", ["FieldName"] = "#7AB8FF", ["Punctuation"] = "#5EEAD4" },

            [EJsonColorScheme.SunsetTeal] = new Dictionary<string, string>
            { ["Bool"] = "#4ECDC4", ["Number"] = "#E94F37", ["String"] = "#FFC15E", ["Null"] = "#737994", ["FieldName"] = "#FF6B8A", ["Punctuation"] = "#6FBFE0" },

            [EJsonColorScheme.IndigoGold] = new Dictionary<string, string>
            { ["Bool"] = "#5FC9E8", ["Number"] = "#E8B04B", ["String"] = "#F0D98C", ["Null"] = "#7A80A0", ["FieldName"] = "#A6B4FF", ["Punctuation"] = "#7C8AD6" },
        };
}
