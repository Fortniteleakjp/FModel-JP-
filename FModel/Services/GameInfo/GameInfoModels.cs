using System.Collections.Generic;
using CUE4Parse.UE4.Versions;

namespace FModel.Services.GameInfo;

public enum InfoSeverity
{
    Normal,
    Good,
    Warning
}

/// <summary>情報ウィンドウの 1 行 (項目名と値)</summary>
public sealed record InfoRow(string Label, string Value, InfoSeverity Severity = InfoSeverity.Normal, string Note = null);

public sealed class InfoSection
{
    public string Title { get; }
    public List<InfoRow> Rows { get; } = [];

    public InfoSection(string title)
    {
        Title = title;
    }

    public void Add(string label, string value, InfoSeverity severity = InfoSeverity.Normal, string note = null)
    {
        if (string.IsNullOrEmpty(value)) return;
        Rows.Add(new InfoRow(label, value, severity, note));
    }
}

/// <summary>UE のメジャー・マイナー (5.4 など)。EGame や実行ファイル、マッピングの版を同じ物差しで比べるため</summary>
public readonly record struct EngineVersion(int Major, int Minor)
{
    public override string ToString() => $"{Major}.{Minor}";

    /// <summary>EGame は (メジャー &lt;&lt; 24) + (マイナー &lt;&lt; 16) + ゲーム固有の番号 で並んでいる</summary>
    public static EngineVersion? FromGame(EGame game)
    {
        var value = (int) game;
        var major = value >> 24;
        if (major < 3) return null;
        return new EngineVersion(major, (value >> 16) & 0xFF);
    }

    /// <summary>ゲーム固有の設定 (GAME_Valorant 等) なら汎用の GAME_UEx_y と異なる</summary>
    public static bool IsGameSpecific(EGame game) => ((int) game & 0xFFFF) != 0;

    public static EngineVersion? FromNumbers(int major, int minor) =>
        major is >= 4 and <= 6 && minor is >= 0 and < 100 ? new EngineVersion(major, minor) : null;
}

/// <summary>根拠の確かさ。小さいほど信頼できる</summary>
public enum EvidenceReliability
{
    /// <summary>実行ファイルのバージョン (今のビルドそのもの)</summary>
    Executable,
    /// <summary>マッピングに記録されたエンジン (そのビルドからダンプしたもの)</summary>
    Mappings,
    /// <summary>パッケージを保存したエンジン。古いビルドで保存されたまま残っていることがある</summary>
    SavedPackage,
    /// <summary>パッケージバージョンからの逆引き。複数の版が同じ値のことがある</summary>
    PackageVersion
}

/// <summary>バージョンを推定した根拠 (比較・警告に使う)</summary>
public sealed record EngineVersionEvidence(string Source, EngineVersion Version, EvidenceReliability Reliability, bool IsRange = false);

public sealed class GameInfoReport
{
    public List<InfoSection> Sections { get; } = [];
    public List<string> Warnings { get; } = [];
}
