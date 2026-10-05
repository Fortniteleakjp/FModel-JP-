using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.AssetRegistry;

namespace FModel.Services.Mcp;

/// <summary>fmodel_open_game の引数。保存済みゲームから開くときもこの形に組み立てる。</summary>
public sealed class OpenGameOptions
{
    [Description("Absolute OS directory of a game FModel already knows (one of fmodel_list_saved_games), or a folder inside it.")]
    public required string Directory { get; set; }
    [Description("Explicit CUE4Parse GAME_* engine/game profile. Discover with fmodel_list_options; do not guess.")]
    public required string Game { get; set; }
    public string TexturePlatform { get; set; } = "DesktopMobile";
    [Description("Absolute local .usmap/.jmap/.jmap.gz file, if required by the game.")]
    public string MappingsPath { get; set; }
    [Description("Authorized AES key map: GUID to 64 hexadecimal digits. All-zero GUID denotes the main key.")]
    public Dictionary<string, string> AesKeys { get; set; }
    public Dictionary<string, int> CustomVersions { get; set; }
    public Dictionary<string, bool> VersionOptions { get; set; }
    public Dictionary<string, KeyValuePair<string, string>> MapStructTypes { get; set; }
    public bool ReadScriptData { get; set; } = true;
    public bool ReadShaderMaps { get; set; }
    public bool ReadNaniteData { get; set; } = true;
}

/// <summary>fmodel_start_export の引数。省略した書き出し設定は FModel の設定画面の値を使う。</summary>
public sealed class ExportRequest
{
    [Description("Explicit virtual asset paths returned by search. At most 500, no duplicates.")]
    public string[] Paths { get; set; } = [];
    [Description("raw, properties, converted or audio. Converted uses the matching CUE4Parse exporter.")]
    public string Mode { get; set; } = "converted";
    public string ObjectName { get; set; }
    [Description("Gltf2, ActorX, UEFormat or USD. Worlds require USD; animations support ActorX/UEFormat/USD. Default: FModel's setting.")]
    public string MeshFormat { get; set; }
    public string MeshQuality { get; set; }
    public string NaniteMeshFormat { get; set; }
    [Description("Png, Jpeg, Tga, Dds... Default: FModel's setting.")]
    public string TextureFormat { get; set; }
    public int? TextureQuality { get; set; }
    public bool? ExportAllTextureMips { get; set; }
    public bool? ExportHdrTexturesAsHdr { get; set; }
    public bool? ExportMaterials { get; set; }
    public string MaterialDepth { get; set; }
    public bool? ExportMorphTargets { get; set; }
    public string SocketFormat { get; set; }
    public string CompressionFormat { get; set; }
    public bool DecompressAudio { get; set; } = true;
    public bool IncludeStreamingLevels { get; set; }
}

/// <summary>
/// MCP から見た 1 つのゲーム。<c>live</c> は FModel のメインウィンドウが読み込んでいるゲームそのもので、
/// それ以外は fmodel_open_game / fmodel_open_saved_game で MCP 専用に開いた読み取り専用プロバイダ。
/// </summary>
internal sealed class McpSession(string id, string directory, AbstractVfsFileProvider provider, bool isLive)
{
    public const string LiveId = "live";

    public string Id { get; } = id;
    public string Directory { get; } = directory;
    public AbstractVfsFileProvider Provider { get; } = provider;
    /// <summary>FModel 本体と共有しているプロバイダ。閉じたり鍵・カルチャを差し替えたりしない</summary>
    public bool IsLive { get; } = isLive;
    /// <summary>セッション内の操作を直列化する (CUE4Parse のプロバイダ状態を同時に触らせない)</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public volatile bool Closed;
    public string MappingsPath { get; set; }
    public FAssetRegistryState Registry { get; set; }

    private readonly object _filesLock = new();
    private GameFile[] _files = [];
    private int _filesCount = -1;

    /// <summary>
    /// パス順に並べた全ファイル。live では FModel が後からアーカイブをマウントする (AES 再取得など) ので、
    /// 件数が変わっていたら並べ直す。
    /// </summary>
    public GameFile[] Files
    {
        get
        {
            lock (_filesLock)
            {
                var count = Provider.Files.Count;
                if (count != _filesCount)
                {
                    _files = Provider.Files.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
                    _filesCount = count;
                }

                return _files;
            }
        }
    }
}

public sealed record McpAssetInfo(string Path, long Size, string Extension, bool IsPackage, bool Encrypted, string Compression, string Archive);
public sealed record McpPage<T>(IReadOnlyList<T> Items, int Offset, int Total, int? NextOffset);
public sealed record McpExportItem(string Path, bool Success, string[] Files, string Error = null);
internal sealed record McpTexturePreview(byte[] Bytes, int Width, int Height, string Format);

internal sealed class McpExportJob(string id, string sessionId, string outputDirectory, int total)
{
    public string Id { get; } = id;
    public string SessionId { get; } = sessionId;
    public string OutputDirectory { get; } = outputDirectory;
    public int Total { get; } = total;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public CancellationTokenSource Cancellation { get; } = new();
    public Task Task { get; set; }
    public object Sync { get; } = new();
    public string State { get; set; } = "queued";
    public string CurrentPath { get; set; }
    public List<McpExportItem> Results { get; } = [];
    public string Error { get; set; }
    public bool Terminal => State is "completed" or "completed_with_errors" or "cancelled" or "failed";

    public object Snapshot(string stateOverride = null)
    {
        lock (Sync)
        {
            return new
            {
                jobId = Id, sessionId = SessionId, state = stateOverride ?? State, total = Total, completed = Results.Count,
                succeeded = Results.Count(r => r.Success), failed = Results.Count(r => !r.Success), currentPath = CurrentPath,
                outputDirectory = OutputDirectory, createdAt = CreatedAt, error = Error
            };
        }
    }
}
