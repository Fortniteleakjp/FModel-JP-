using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FModel.Framework;
using FModel.Settings;

namespace FModel.Services.Mcp;

public sealed partial class FModelMcpService
{
    private sealed record SavedGame(string Name, bool Selected, OpenGameOptions Options, string[] MappingCandidates, bool ExplicitMapping)
    {
        public object Summary() => new
        {
            name = Name,
            selected = Selected,
            directory = Options.Directory,
            game = Options.Game,
            texturePlatform = Options.TexturePlatform,
            directoryExists = Directory.Exists(Options.Directory),
            mappingsPath = Options.MappingsPath,
            mappingCandidates = MappingCandidates,
            savedKeyCount = Options.AesKeys?.Count ?? 0
        };
    }

    /// <summary>
    /// FModel に保存されたゲーム一覧。参考実装は AppSettings.json を外から読むが、
    /// ここでは FModel 自身が持っている <see cref="UserSettings.PerDirectory"/> をそのまま使う (未保存の変更も反映される)。
    /// 鍵の値は返さない。
    /// </summary>
    public object ListSavedGames() => new
    {
        liveSessionDirectory = UserSettings.Default.CurrentDir.GameDirectory,
        games = ReadSavedGames().Select(g => g.Summary()).ToArray()
    };

    public async Task<object> OpenSavedGame(string selector, string mappingsPath, bool readScriptData, CancellationToken ct)
    {
        var games = ReadSavedGames();
        var matches = games.Where(g => string.IsNullOrWhiteSpace(selector)
            ? g.Selected
            : string.Equals(g.Name, selector, StringComparison.OrdinalIgnoreCase) || SameDirectory(g.Options.Directory, selector)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("Select exactly one saved game by name or directory using fmodel_list_saved_games.");

        var saved = matches[0];
        if (saved.Selected && string.IsNullOrWhiteSpace(mappingsPath))
        {
            // 既に FModel が開いているゲームなら、マウントし直さずに live を使えばよい
            EnsureLive();
            return new { sessionId = McpSession.LiveId, reused = true, note = "This game is already loaded in FModel; use the live session." };
        }

        var request = saved.Options;
        if (mappingsPath is not null) request.MappingsPath = mappingsPath;
        else if (!saved.ExplicitMapping && saved.MappingCandidates.Length > 1)
            throw new InvalidOperationException("Several cached mappings match this game. Choose the correct build from mappingCandidates and pass mappingsPath; the server will not guess.");
        request.ReadScriptData = readScriptData;
        return await OpenGame(request, ct);
    }

    private static SavedGame[] ReadSavedGames()
    {
        var current = UserSettings.Default.CurrentDir.GameDirectory;
        var cache = CacheManager.MappingsDirectory;
        // 同じフォルダが大文字小文字や区切り文字違いで何度も保存されていることがあるので、正規化して 1 つにする
        // (今開いているゲームの登録を優先)
        var directories = McpPathPolicy.KnownGameDirectories()
            .GroupBy(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.FirstOrDefault(d => string.Equals(d, current, StringComparison.OrdinalIgnoreCase)) ?? g.Last())
            .ToArray();

        return directories.Select(directory =>
        {
            var settings = UserSettings.Default.PerDirectory[directory];
            var name = settings.GameName ?? directory;

            var endpoint = settings.Endpoints?.ElementAtOrDefault((int) EEndpointType.Mapping);
            var explicitMapping = endpoint is { Overwrite: true } && System.IO.File.Exists(endpoint.FilePath);
            var mappingPath = explicitMapping ? endpoint.FilePath : null;
            string[] candidates = [];
            if (!explicitMapping && Directory.Exists(cache))
            {
                candidates = Directory.EnumerateFiles(cache)
                    .Where(path => Path.GetFileName(path).Contains(name, StringComparison.OrdinalIgnoreCase) &&
                                   (path.EndsWith(".usmap", StringComparison.OrdinalIgnoreCase) ||
                                    path.EndsWith(".jmap", StringComparison.OrdinalIgnoreCase) ||
                                    path.EndsWith(".jmap.gz", StringComparison.OrdinalIgnoreCase)))
                    .Order(StringComparer.OrdinalIgnoreCase).Take(200).ToArray();
                if (candidates.Length == 1) mappingPath = candidates[0];
            }

            var keys = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(settings.AesKeys?.MainKey))
                keys[new string('0', 32)] = settings.AesKeys.MainKey;
            foreach (var key in settings.AesKeys?.DynamicKeys ?? [])
            {
                if (!string.IsNullOrWhiteSpace(key.Guid) && !string.IsNullOrWhiteSpace(key.Key))
                    keys[key.Guid] = key.Key;
            }

            var versioning = settings.Versioning;
            var selected = string.Equals(Path.GetFullPath(current), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
            return new SavedGame(name, selected, new OpenGameOptions
            {
                Directory = directory,
                Game = settings.UeVersion.ToString(),
                TexturePlatform = settings.TexturePlatform.ToString(),
                MappingsPath = mappingPath,
                AesKeys = keys,
                CustomVersions = versioning?.CustomVersions?.ToDictionary(v => v.Key.ToString(), v => v.Version),
                VersionOptions = versioning?.Options is null ? null : new Dictionary<string, bool>(versioning.Options),
                MapStructTypes = versioning?.MapStructTypes is null ? null : new Dictionary<string, KeyValuePair<string, string>>(versioning.MapStructTypes)
            }, candidates, explicitMapping);
        }).ToArray();
    }

    private static bool SameDirectory(string a, string b)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
