using System;
using System.Linq;
using FModel.Settings;
using Newtonsoft.Json;
using J = Newtonsoft.Json.JsonPropertyAttribute;

namespace FModel.Services.ReleaseNotes;

public enum EReleaseNoteSource
{
    /// <summary>アプリに同梱された ReleaseNotes.json 由来</summary>
    Bundled,

    /// <summary>GitHub Releases から取得したもの</summary>
    GitHub
}

/// <summary>
/// UI 表示言語に追従する文字列。日本語が未設定なら英語にフォールバックする。
/// </summary>
public class LocalizedText
{
    [J("en")] public string English { get; set; }
    [J("ja")] public string Japanese { get; set; }

    [JsonIgnore]
    public string Value => UserSettings.Default.InterfaceLanguage == EInterfaceLanguage.Japanese
        ? Japanese ?? English ?? string.Empty
        : English ?? Japanese ?? string.Empty;

    public override string ToString() => Value;
}

public class ReleaseNoteChange
{
    /// <summary>Added / Changed / Fixed / Removed</summary>
    [J("type")] public string Type { get; set; } = "Changed";

    [J("text")] public LocalizedText Text { get; set; }

    [JsonIgnore] public string Display => Text?.Value ?? string.Empty;
}

public class ReleaseNote
{
    [J("version")] public string Version { get; set; }
    [J("tag")] public string Tag { get; set; }
    [J("date")] public DateTime? Date { get; set; }
    [J("title")] public LocalizedText Title { get; set; }
    [J("changes")] public ReleaseNoteChange[] Changes { get; set; } = [];

    [JsonIgnore] public EReleaseNoteSource Source { get; set; } = EReleaseNoteSource.Bundled;

    /// <summary>GitHub のリリース本文 (Markdown)。同梱分では使わない。</summary>
    [JsonIgnore] public string Body { get; set; }

    [JsonIgnore] public string HtmlUrl { get; set; }

    [JsonIgnore] public string DisplayVersion => !string.IsNullOrEmpty(Tag) ? Tag : Version;
    [JsonIgnore] public string DisplayTitle => Title?.Value is { Length: > 0 } t ? t : DisplayVersion;
    [JsonIgnore] public string DisplayDate => Date?.ToString("yyyy/MM/dd") ?? string.Empty;
    [JsonIgnore] public bool HasChanges => Changes is { Length: > 0 };
    [JsonIgnore] public bool HasBody => !string.IsNullOrWhiteSpace(Body);
    [JsonIgnore] public bool IsFromGitHub => Source == EReleaseNoteSource.GitHub;

    /// <summary>同梱分と GitHub 分を突き合わせるためのキー</summary>
    [JsonIgnore]
    public string Key => (Tag ?? Version ?? string.Empty).TrimStart('v', 'V');

    public static ReleaseNote FromGitHub(string tag, string name, string body, string htmlUrl, DateTime? publishedAt) => new()
    {
        Source = EReleaseNoteSource.GitHub,
        Tag = tag,
        Version = tag,
        Date = publishedAt,
        Title = new LocalizedText { English = string.IsNullOrWhiteSpace(name) ? tag : name },
        Body = body,
        HtmlUrl = htmlUrl,
        Changes = []
    };
}

public class ReleaseNoteCollection
{
    [J("releases")] public ReleaseNote[] Releases { get; set; } = [];

    [JsonIgnore]
    public ReleaseNote Latest => Releases.OrderByDescending(r => r.Date ?? DateTime.MinValue).FirstOrDefault();
}
