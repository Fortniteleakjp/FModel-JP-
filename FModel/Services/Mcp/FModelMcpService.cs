using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Jmap;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.Writers.UEFormat.Enums;
using FModel.Settings;
using FModel.ViewModels;

namespace FModel.Services.Mcp;

/// <summary>
/// MCP ツールの実体。FModel が読み込んでいるゲームを "live" セッションとして公開し、
/// 追加で開いたゲーム (比較用など) を別セッションとして持つ。
/// <para>
/// 構成は plu1337/fmodel-mcp (GPL-3.0) の FModelService を踏襲している。違いは
/// 「別プロセスで自前のプロバイダを開く」代わりに FModel 本体のプロバイダ・鍵・マッピングを共有する点と、
/// FModel の画面操作 (<see cref="FModelMcpService"/>.Desktop) や Verse 復元を足している点。
/// </para>
/// </summary>
public sealed partial class FModelMcpService
{
    public const int MaxSessions = 4;
    public const int MaxJobs = 32;
    public const int MaxBatchAssets = 500;
    public const long MaxReadBytes = 512L * 1024 * 1024;
    public const int MaxResponseChars = 100_000;
    public const int MaxPageSize = 200;

    private readonly ConcurrentDictionary<string, McpSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, McpExportJob> _jobs = new();
    private readonly SemaphoreSlim _sessionCreation = new(1, 1);
    private readonly object _jobsSync = new();
    private McpSession _live;

    private static CUE4ParseViewModel Cue4Parse => ApplicationService.ApplicationView.CUE4Parse;

    public object Capabilities() => new
    {
        name = "FModel JP MCP",
        version = Constants.APP_VERSION,
        transport = "stdio relay -> named pipe -> running FModel",
        liveGame = LiveSummary(),
        outputRoot = McpPathPolicy.OutputRoot,
        knownGameDirectories = McpPathPolicy.KnownGameDirectories(),
        limits = new { MaxSessions, MaxJobs, MaxBatchAssets, MaxReadBytes, MaxResponseChars, maxPageSize = MaxPageSize },
        oodleLoaded = OodleHelper.Instance is not null,
        workflows = new[]
        {
            "the game currently loaded in FModel (sessionId=live, keys/mappings shared with the app)",
            "extra saved FModel games for comparison (open_saved_game / open_game)",
            "browse/search", "package exports/properties/imports/names", "IoStore referencers", "asset registry", "localization",
            "text/binary reads", "texture preview", "Blueprint pseudocode", "Verse source recovery",
            "raw/JSON/texture/audio/mesh/animation/material/world export jobs", "session comparison",
            "FModel window integration (open an asset in a tab or viewer, read what the user is looking at)"
        },
        limitations = new[]
        {
            "Only directories saved in FModel can be opened as extra sessions",
            "The live session's AES keys, mappings and culture are managed by FModel's own UI",
            "No repacking or editing game archives through MCP",
            "Cooked assets do not recover original source code",
            "IoStore referencers use container import metadata; they are not a complete soft-reference graph"
        }
    };

    private object LiveSummary()
    {
        var provider = Cue4Parse.Provider;
        return new
        {
            sessionId = McpSession.LiveId,
            ready = provider.Files.Count > 0,
            name = provider.GameDisplayName ?? provider.ProjectName,
            directory = UserSettings.Default.CurrentDir.GameDirectory,
            game = provider.Versions.Game.ToString(),
            fileCount = provider.Files.Count
        };
    }

    public object EnumValues(string category, string filter, int offset, int limit)
    {
        string[] values = category.ToLowerInvariant() switch
        {
            "game" => Enum.GetNames<EGame>(),
            "textureplatform" => Enum.GetNames<ETexturePlatform>(),
            "meshformat" => Enum.GetNames<EMeshFormat>(),
            "meshquality" => Enum.GetNames<EMeshQuality>(),
            "nanitemeshformat" => Enum.GetNames<ENaniteMeshFormat>(),
            "textureformat" => Enum.GetNames<ETextureFormat>(),
            "materialdepth" => Enum.GetNames<EMaterialDepth>(),
            "socketformat" => Enum.GetNames<ESocketFormat>(),
            "compressionformat" => Enum.GetNames<EFileCompressionFormat>(),
            _ => throw new ArgumentException("Unknown category. Use game, texturePlatform, meshFormat, meshQuality, naniteMeshFormat, textureFormat, materialDepth, socketFormat or compressionFormat.")
        };
        return Paginate(values.Where(x => Contains(x, filter)).ToArray(), offset, limit);
    }

    #region sessions

    public async Task<object> OpenGame(OpenGameOptions request, CancellationToken ct)
    {
        var directory = McpPathPolicy.GameDirectory(request.Directory);
        var game = ParseEnum<EGame>(request.Game);
        var platform = ParseEnum<ETexturePlatform>(request.TexturePlatform);
        var mappingsPath = request.MappingsPath is null ? null : McpPathPolicy.MappingsFile(request.MappingsPath);
        var keys = ParseKeys(request.AesKeys ?? []);
        var versions = new VersionContainer(game, platform,
            customVersions: new FCustomVersionContainer(request.CustomVersions?.Select(v => new FCustomVersion(ParseGuid(v.Key), v.Value))),
            optionOverrides: request.VersionOptions, mapStructTypesOverrides: request.MapStructTypes);

        await _sessionCreation.WaitAsync(ct);
        try
        {
            if (_sessions.Values.Count(s => !s.IsLive) >= MaxSessions)
                throw new InvalidOperationException("Session limit reached. Close a session with fmodel_close_game first.");

            return await Task.Run<object>(() =>
            {
                var comparer = game == EGame.GAME_BlackStigma ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
                var provider = CUE4ParseViewModel.CreateLocalProvider(directory, versions, comparer);
                try
                {
                    provider.MappingsContainer = mappingsPath is null ? null : LoadMappings(mappingsPath);
                    provider.ReadScriptData = request.ReadScriptData;
                    provider.ReadShaderMaps = request.ReadShaderMaps;
                    provider.ReadNaniteData = request.ReadNaniteData;
                    provider.Initialize();
                    provider.Mount();
                    if (keys.Count > 0) provider.SubmitKeys(keys);
                    provider.PostMount();
                    ct.ThrowIfCancellationRequested();
                    provider.LoadVirtualPaths(provider.Versions.Ver, ct);

                    var session = new McpSession(Guid.NewGuid().ToString("N")[..12], directory, provider, false) { MappingsPath = mappingsPath };
                    _sessions[session.Id] = session;
                    return SessionInfo(session);
                }
                catch
                {
                    provider.Dispose();
                    throw;
                }
            }, ct);
        }
        finally
        {
            _sessionCreation.Release();
        }
    }

    public object ListSessions()
    {
        EnsureLive();
        return new
        {
            sessions = _sessions.Values.Where(s => !s.Closed).OrderByDescending(s => s.IsLive)
                .Select(s => new { sessionId = s.Id, live = s.IsLive, directory = s.Directory, project = s.Provider.ProjectName }).ToArray()
        };
    }

    public Task<object> GetSession(string id, CancellationToken ct) => InSession(id, SessionInfo, ct);

    private static object SessionInfo(McpSession s) => new
    {
        sessionId = s.Id,
        live = s.IsLive,
        directory = s.Directory,
        projectName = s.Provider.ProjectName,
        displayName = s.Provider.GameDisplayName,
        game = s.Provider.Versions.Game.ToString(),
        texturePlatform = s.Provider.Versions.Platform.ToString(),
        providerType = s.Provider.GetType().Name,
        fileCount = s.Provider.Files.Count,
        mountedArchives = s.Provider.MountedVfs.Count,
        unloadedArchives = s.Provider.UnloadedVfs.Count,
        requiredKeyGuids = s.Provider.RequiredKeys.Select(k => k.ToString()).ToArray(),
        suppliedKeyCount = s.Provider.Keys.Count,
        mappingsPath = s.IsLive ? (s.Provider.MappingsContainer as FileUsmapTypeMappingsProvider)?.FileName : s.MappingsPath,
        hasMappings = s.Provider.MappingsContainer is not null,
        readScriptData = s.Provider.ReadScriptData,
        culture = s.Provider.Internationalization.Culture,
        localizedStrings = s.Provider.Internationalization.Count,
        virtualPaths = s.Provider.VirtualPaths.Count,
        registryLoaded = s.Registry is not null,
        hasIoStoreGlobalData = s.Provider.GlobalData is not null
    };

    public async Task<object> CloseSession(string id, CancellationToken ct)
    {
        var session = Session(id);
        if (session.IsLive) throw new InvalidOperationException("The live session belongs to the FModel window and cannot be closed. Switch games in FModel instead.");

        lock (_jobsSync)
        {
            if (_jobs.Values.Any(j => j.SessionId == session.Id && !IsTerminal(j)))
                throw new InvalidOperationException("Session has an active export job. Cancel it and wait for terminal status before closing.");
            session.Closed = true;
        }

        // 閉じ始めたら、呼び出し元が切断しても破棄までやり切る
        await session.Gate.WaitAsync(CancellationToken.None);
        try
        {
            _sessions.TryRemove(session.Id, out _);
            session.Provider.Dispose();
            return new { sessionId = session.Id, closed = true };
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public Task<object> Archives(string id, string filter, int offset, int limit, CancellationToken ct) => InSession(id, s =>
    {
        var mounted = s.Provider.MountedVfs.ToHashSet();
        var rows = mounted.Concat(s.Provider.UnloadedVfs).Where(a => Contains(a.Name, filter)).OrderBy(a => a.Name).Select(a => new
        {
            name = a.Name, path = a.Path, mounted = mounted.Contains(a), encrypted = a.IsEncrypted, keyGuid = a.EncryptionKeyGuid.ToString(),
            size = a.Length, fileCount = a.FileCount, mountPoint = a.MountPoint, hasDirectoryIndex = a.HasDirectoryIndex,
            compressionMethods = a.CompressionMethods.Select(x => x.ToString()).ToArray()
        }).ToArray();
        return Paginate(rows, offset, limit);
    }, ct);

    public Task<object> SubmitKeys(string id, Dictionary<string, string> keys, CancellationToken ct)
    {
        var parsed = ParseKeys(keys ?? []);
        return InSession(id, s =>
        {
            RejectLive(s, "Submit AES keys in FModel's AES Manager for the live session.");
            s.Provider.SubmitKeys(parsed);
            s.Provider.PostMount();
            return SessionInfo(s);
        }, ct);
    }

    public Task<object> SetMappings(string id, string path, CancellationToken ct)
    {
        var full = McpPathPolicy.MappingsFile(path);
        return InSession(id, s =>
        {
            RejectLive(s, "Change the live session's mappings in FModel's settings.");
            s.Provider.MappingsContainer = LoadMappings(full);
            s.MappingsPath = full;
            return SessionInfo(s);
        }, ct);
    }

    public Task<object> LoadVirtualPaths(string id, CancellationToken ct) => InSession(id, s =>
    {
        var loaded = s.Provider.VirtualPaths.Count > 0 ? s.Provider.VirtualPaths.Count : s.Provider.LoadVirtualPaths(s.Provider.Versions.Ver, ct);
        return new { loaded, paths = s.Provider.VirtualPaths };
    }, ct);

    private static ITypeMappingsProvider LoadMappings(string path)
    {
        if (new FileInfo(path).Length > MaxReadBytes) throw new ArgumentException("Mappings file exceeds maxReadBytes.");
        return path.EndsWith(".usmap", StringComparison.OrdinalIgnoreCase)
            ? new FileUsmapTypeMappingsProvider(path)
            : new JmapTypeMappingsProvider(path);
    }

    /// <summary>
    /// FModel のメインウィンドウのプロバイダを "live" として登録する。
    /// プロバイダは FModel の起動中ずっと同じインスタンス (ゲーム切替は再起動) なので一度作れば足りる。
    /// </summary>
    private McpSession EnsureLive()
    {
        if (_live is not null) return _live;

        var provider = Cue4Parse.Provider;
        var live = new McpSession(McpSession.LiveId, UserSettings.Default.CurrentDir.GameDirectory, provider, true);
        _sessions.TryAdd(McpSession.LiveId, live);
        return _live = _sessions[McpSession.LiveId];
    }

    private McpSession Session(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Equals(McpSession.LiveId, StringComparison.OrdinalIgnoreCase))
            return EnsureLive();

        return _sessions.TryGetValue(id, out var s) && !s.Closed
            ? s
            : throw new KeyNotFoundException("Unknown or closed sessionId. Use fmodel_list_sessions, or omit sessionId for the game loaded in FModel.");
    }

    private async Task<object> InSession(string id, Func<McpSession, object> action, CancellationToken ct)
    {
        var s = Session(id);
        if (s.IsLive && s.Provider.Files.Count == 0)
            throw new InvalidOperationException("FModel has not mounted any game file yet. Wait for loading to finish, or check FModel's AES keys (fmodel_list_archives shows what is unloaded).");

        await s.Gate.WaitAsync(ct);
        try
        {
            if (s.Closed) throw new InvalidOperationException("Session is closed.");
            return await Task.Run(() => action(s), ct);
        }
        finally
        {
            s.Gate.Release();
        }
    }

    private static void RejectLive(McpSession s, string message)
    {
        if (s.IsLive) throw new InvalidOperationException(message);
    }

    #endregion

    #region helpers

    private static Dictionary<FGuid, FAesKey> ParseKeys(Dictionary<string, string> keys)
    {
        if (keys.Count > 256) throw new ArgumentException("At most 256 AES keys per call.");
        try
        {
            return keys.ToDictionary(k => ParseGuid(k.Key), k => new FAesKey(k.Value));
        }
        catch
        {
            throw new ArgumentException("Invalid AES key map. Use GUID strings (all zeros for the main key) and 64 hexadecimal key digits. Key values are never returned.");
        }
    }

    /// <summary>FModel の設定と同じ 32 桁 16 進 (ハイフン可) の GUID</summary>
    private static FGuid ParseGuid(string value)
    {
        var hex = value?.Replace("-", "").Trim('{', '}', ' ');
        return hex is { Length: 32 } && hex.All(Uri.IsHexDigit) ? new FGuid(hex) : throw new ArgumentException("Invalid GUID.");
    }

    internal static T ParseEnum<T>(string value) where T : struct, Enum =>
        Enum.GetNames<T>().FirstOrDefault(n => n.Equals(value, StringComparison.OrdinalIgnoreCase)) is { } name
            ? Enum.Parse<T>(name)
            : throw new ArgumentException($"Invalid {typeof(T).Name}: {value}. Use fmodel_list_options for supported values.");

    internal static bool Contains(string text, string query) =>
        string.IsNullOrEmpty(query) || (text?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

    internal static McpPage<T> Paginate<T>(IReadOnlyList<T> rows, int offset, int limit)
    {
        ValidatePage(offset, limit);
        var items = rows.Skip(offset).Take(limit).ToArray();
        return new McpPage<T>(items, offset, rows.Count, (long) offset + items.Length < rows.Count ? offset + items.Length : null);
    }

    internal static void ValidatePage(int offset, int limit)
    {
        if (offset < 0 || limit is < 1 or > MaxPageSize) throw new ArgumentException($"offset must be nonnegative; limit must be 1..{MaxPageSize}.");
    }

    internal static McpAssetInfo Info(GameFile f) =>
        new(f.Path, f.Size, f.Extension, f.IsUePackage, f.IsEncrypted, f.CompressionMethod.ToString(), (f as VfsEntry)?.Vfs.Name);

    private static GameFile File(McpSession s, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is required.");
        // "/Game/Foo/Bar.Bar:SubObject" のようなオブジェクトパスも受け付ける
        var colon = path.IndexOf(':');
        if (colon > 1) path = path[..colon];
        path = path.Trim();
        if (s.Provider.TryGetGameFile(path, out var f)) return f;

        // FixPath は "Pkg.Pkg" の形だと拡張子を補わないので、オブジェクト名を外して引き直す
        var dot = path.IndexOf('.', path.LastIndexOf('/') + 1);
        if (dot > 0 && s.Provider.TryGetGameFile(path[..dot], out f)) return f;

        throw new KeyNotFoundException("Asset not found. Search first and reuse the exact returned path.");
    }

    private static void CheckSize(GameFile f)
    {
        if (f.Size < 0 || f.Size > MaxReadBytes) throw new InvalidOperationException("Asset exceeds maxReadBytes.");
    }

    /// <summary>例外メッセージから AES 鍵らしき 16 進文字列を伏せ、長さを抑える (スタックトレースは返さない)</summary>
    internal static string ErrorMessage(Exception ex)
    {
        var message = Regex.Replace(ex.GetBaseException().Message, @"(?:0x)?[0-9a-fA-F]{64}", "[redacted key/hash]");
        return message.Length <= 1200 ? message : message[..1200];
    }

    #endregion
}
