using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog;

namespace FModel.Services.ReleaseNotes;

/// <summary>
/// リリースノートの読み込み。アプリ同梱の ReleaseNotes.json を基本とし、
/// ネットワークが使える場合は GitHub Releases 側の新しいリリースで補完する。
/// </summary>
public static class ReleaseNotesService
{
    private const string RESOURCE_NAME = "Resources.ReleaseNotes.json";

    /// <summary>
    /// CI が push ごとに上書きする配布用リリース。中身は導入手順でノートではないため一覧に載せない。
    /// </summary>
    private static readonly string[] _excludedTags = ["qa"];

    private static ReleaseNote[] _bundled;

    /// <summary>同梱分だけを読む。オフラインでも必ず成功する。</summary>
    public static ReleaseNote[] GetBundled()
    {
        if (_bundled is not null) return _bundled;

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream($"{assembly.GetName().Name}.{RESOURCE_NAME}");
            if (stream is null)
            {
                Log.Warning("The bundled release notes could not be found");
                return _bundled = [];
            }

            using var reader = new StreamReader(stream);
            var collection = JsonConvert.DeserializeObject<ReleaseNoteCollection>(reader.ReadToEnd());
            return _bundled = Sort(collection?.Releases ?? []);
        }
        catch (Exception e)
        {
            Log.Warning("Failed to read the bundled release notes: {msg}", e.Message);
            return _bundled = [];
        }
    }

    /// <summary>一番新しい同梱リリースのバージョン。初回起動時のポップアップ判定に使う。</summary>
    public static string LatestBundledVersion => GetBundled().FirstOrDefault()?.Key ?? string.Empty;

    /// <summary>同梱分と GitHub Releases をまとめて返す。GitHub 側が落ちても同梱分は返る。</summary>
    public static async Task<ReleaseNote[]> LoadAsync()
    {
        var notes = new List<ReleaseNote>(GetBundled());
        var known = notes.Select(n => n.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            var releases = await ApplicationService.ApiEndpointView.GitHubApi.GetJpReleasesAsync().ConfigureAwait(false);
            foreach (var release in releases ?? [])
            {
                if (release.Draft || string.IsNullOrEmpty(release.TagName)) continue;
                if (_excludedTags.Contains(release.TagName, StringComparer.OrdinalIgnoreCase)) continue;

                var note = ReleaseNote.FromGitHub(release.TagName, release.Name, release.Body, release.HtmlUrl, release.PublishedAt);
                if (!known.Add(note.Key)) continue; // 同梱分が優先

                notes.Add(note);
            }
        }
        catch (Exception e)
        {
            Log.Warning("Failed to fetch the release notes from GitHub: {msg}", e.Message);
        }

        return Sort(notes);
    }

    private static ReleaseNote[] Sort(IEnumerable<ReleaseNote> notes) =>
        notes.OrderByDescending(n => n.Date ?? DateTime.MinValue).ToArray();
}
