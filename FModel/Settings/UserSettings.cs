using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Writers.UEFormat.Enums;
using CUE4Parse.UE4.Lua.unluac;
using FModel.Extensions.Themes;
using FModel.Framework;
using FModel.ViewModels;
using FModel.ViewModels.ApiEndpoints.Models;
using FModel.Views.Snooper;
using Newtonsoft.Json;
using OnDeserializedAttribute = System.Runtime.Serialization.OnDeserializedAttribute;
using StreamingContext = System.Runtime.Serialization.StreamingContext;

namespace FModel.Settings;

public sealed class UserSettings : ViewModel
{
    public static UserSettings Default { get; set; }
#if DEBUG
    public static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FModel", "AppSettings_Debug.json");
#else
    public static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FModel", "AppSettings.json");
#endif

    static UserSettings()
    {
        Default = new UserSettings();
    }

    private static string BackupFilePath => FilePath + ".bak";
    private static string TempFilePath => FilePath + ".tmp";

    private static readonly object _saveLock = new();
    private static bool _bSave = true;
    public static void Save()
    {
        if (!_bSave || Default == null) return;
        lock (_saveLock)
        {
            if (Default.CurrentDir?.GameDirectory is { } gameDirectory)
                Default.PerDirectory[gameDirectory] = Default.CurrentDir;

            WriteAtomically(JsonConvert.SerializeObject(Default, Formatting.Indented));
        }
    }

    /// <summary>
    /// 一時ファイルに書いてから置き換える。File.WriteAllText は先にファイルを空にするので、書き込み中に
    /// 再起動後の新しいプロセスが読んだり、強制終了されたりすると壊れたファイルが残り、次回起動時に全設定が初期化されていた。
    /// 置き換え前のファイルは .bak に残し、読み込みに失敗したときの復元元にする。
    /// </summary>
    private static void WriteAtomically(string json)
    {
        using (var stream = new FileStream(TempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(true);
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(FilePath))
                    File.Replace(TempFilePath, FilePath, BackupFilePath, true);
                else
                    File.Move(TempFilePath, FilePath);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                // 別の FModel が設定を読んでいる最中 (共有違反) なので少し待つ
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// 設定を読み込む。壊れていたら .bak から復元し、それも駄目なら壊れたファイルを退避してから初期設定で起動する
    /// (退避せずに初期設定で起動すると、終了時の保存で元の設定が上書きされて消えてしまう)。
    /// </summary>
    /// <param name="messages">ロガーの準備前に読み込むため、ログに残す内容を返す</param>
    public static UserSettings Load(out List<string> messages)
    {
        messages = [];
        // 保存は置き換えなので本体が消えることはない。無いのは初回起動か、ユーザーが初期化のために消したとき
        if (!File.Exists(FilePath)) return new UserSettings();
        if (TryLoad(FilePath, messages, out var settings))
        {
            if (settings.MergedDuplicateDirectories > 0)
            {
                // まとめた側の登録は次の保存で消えるので、元のファイルを残しておく
                var copy = KeepCopy("before-merge", messages);
                messages.Add($"Merged {settings.MergedDuplicateDirectories} duplicated game directory entries (same folder with a different casing or separator)" +
                             (copy is null ? string.Empty : $", the previous settings were kept as \"{copy}\""));
            }

            return settings;
        }

        var corruptPath = KeepCopy("corrupt", messages);
        if (File.Exists(BackupFilePath) && TryLoad(BackupFilePath, messages, out var backup))
        {
            messages.Add($"Restored the settings from \"{BackupFilePath}\"");
            return backup;
        }

        messages.Add(corruptPath is null
            ? "Could not read the settings, starting with the default settings"
            : $"Could not read the settings, starting with the default settings (the unreadable file was kept as \"{corruptPath}\")");
        return new UserSettings();
    }

    private static bool TryLoad(string path, List<string> messages, out UserSettings settings)
    {
        settings = null;
        try
        {
            var json = ReadAllTextShared(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                messages.Add($"\"{path}\" is empty");
                return false;
            }

            var serializerSettings = new JsonSerializerSettings
            {
                NullValueHandling = JsonNetSerializer.SerializerSettings.NullValueHandling,
                MissingMemberHandling = JsonNetSerializer.SerializerSettings.MissingMemberHandling,
                ContractResolver = JsonNetSerializer.SerializerSettings.ContractResolver,
                // 1 つの値が読めない (列挙値の削除など) だけで全設定を捨てない。JSON 自体の破損 (途中で切れている等) は失敗扱いにして .bak へ
                Error = (_, args) =>
                {
                    if (args.ErrorContext.Error is not JsonSerializationException exception) return;
                    messages.Add($"Ignored an unreadable setting at \"{args.ErrorContext.Path}\": {exception.Message}");
                    args.ErrorContext.Handled = true;
                }
            };

            settings = JsonConvert.DeserializeObject<UserSettings>(json, serializerSettings);
            if (settings is null)
            {
                messages.Add($"\"{path}\" does not contain any settings");
                return false;
            }

            return true;
        }
        catch (Exception e)
        {
            messages.Add($"Failed to read \"{path}\": {e.Message}");
            return false;
        }
    }

    private static string ReadAllTextShared(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 10 && File.Exists(path))
            {
                Thread.Sleep(100);
            }
        }
    }

    private static string KeepCopy(string reason, List<string> messages)
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var copyPath = Path.Combine(Path.GetDirectoryName(FilePath)!,
                $"{Path.GetFileNameWithoutExtension(FilePath)}.{reason}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(FilePath, copyPath, true);
            return copyPath;
        }
        catch (Exception e)
        {
            messages.Add($"Could not keep a copy of the settings: {e.Message}");
            return null;
        }
    }

    public static void Delete()
    {
        if (File.Exists(FilePath))
        {
            _bSave = false;
            File.Delete(FilePath);
            // 初期化後に本体が壊れたとき、初期化前の設定が復元されないように
            if (File.Exists(BackupFilePath))
                File.Delete(BackupFilePath);
        }
    }

    public static bool IsEndpointValid(EEndpointType type, out EndpointSettings endpoint)
    {
        endpoint = Default.CurrentDir.Endpoints[(int) type];
        return endpoint.Overwrite || endpoint.IsValid;
    }

    /// <param name="exportMorphTargets">overrides <see cref="SaveMorphTargets"/>, for exports that are about the morph targets</param>
    /// <param name="meshFormat">overrides <see cref="MeshExportFormat"/>, for exports only one format can hold</param>
    public static ExportOptions GetExportOptions(bool? exportMorphTargets = null, EMeshFormat? meshFormat = null)
    {
        return new ExportOptions(
            meshFormat ?? Default.MeshExportFormat,
            Default.NaniteMeshExportFormat,
            Default.MeshQuality,
            Default.CurrentDir.TexturePlatform,
            Default.TextureExportFormat,
            Default.TextureQuality,
            Default.SaveHdrTexturesAsHdr,
            Default.ExportAllTextureMips,
            Default.MaterialExportFormat,
            Default.SaveEmbeddedMaterials,
            exportMorphTargets ?? Default.SaveMorphTargets,
            Default.SocketExportFormat,
            Default.CompressionFormat
        );
    }

    private bool _showChangelog = true;
    public bool ShowChangelog
    {
        get => _showChangelog;
        set => SetProperty(ref _showChangelog, value);
    }

    private string _lastSeenReleaseNotes = string.Empty;
    /// <summary>最後にリリースノートを表示したバージョン。更新後の初回起動で一度だけ開くために使う。</summary>
    public string LastSeenReleaseNotes
    {
        get => _lastSeenReleaseNotes;
        set => SetProperty(ref _lastSeenReleaseNotes, value);
    }

    private string _athenaProfileId = "AthenaProfile";
    public string AthenaProfileId
    {
        get => _athenaProfileId;
        set => SetProperty(ref _athenaProfileId, value);
    }

    private int _athenaBattlePassLevel = 1;
    public int AthenaBattlePassLevel
    {
        get => _athenaBattlePassLevel;
        set => SetProperty(ref _athenaBattlePassLevel, value);
    }

    private string _outputDirectory;
    public string OutputDirectory
    {
        get => _outputDirectory;
        set => SetProperty(ref _outputDirectory, value);
    }

    private string _pakOutputPath;
    /// <summary>
    /// last .pak written from the edited packages, see <see cref="FModel.Services.AssetEditing.PakWriter"/>
    /// </summary>
    public string PakOutputPath
    {
        get => _pakOutputPath;
        set => SetProperty(ref _pakOutputPath, value);
    }

    private int _pakVersion = 3;
    public int PakVersion
    {
        get => _pakVersion;
        set => SetProperty(ref _pakVersion, value);
    }

    private string _pakMountPoint = "../../../";
    public string PakMountPoint
    {
        get => _pakMountPoint;
        set => SetProperty(ref _pakMountPoint, value);
    }

    private bool _mcpServerEnabled;
    /// <summary>
    /// hosts the MCP server inside FModel so AI clients started with "FModel.exe --mcp" can use the loaded game,
    /// see <see cref="FModel.Services.Mcp.McpHost"/>
    /// </summary>
    public bool McpServerEnabled
    {
        get => _mcpServerEnabled;
        set => SetProperty(ref _mcpServerEnabled, value);
    }

    private bool _pakMergeRegistry;
    /// <summary>
    /// merges a cooked AssetRegistry.bin into the game's one and packs it, see <see cref="FModel.Services.AssetEditing.AssetRegistryMerger"/>
    /// </summary>
    public bool PakMergeRegistry
    {
        get => _pakMergeRegistry;
        set => SetProperty(ref _pakMergeRegistry, value);
    }

    private string _pakRegistryBase;
    /// <summary>
    /// the registry to merge into, a file or a path inside the loaded game
    /// </summary>
    public string PakRegistryBase
    {
        get => _pakRegistryBase;
        set => SetProperty(ref _pakRegistryBase, value);
    }

    private string _pakRegistrySource;
    /// <summary>
    /// the cooked AssetRegistry.bin of the modding project
    /// </summary>
    public string PakRegistrySource
    {
        get => _pakRegistrySource;
        set => SetProperty(ref _pakRegistrySource, value);
    }

    private string _pakRegistryFilter = "/Game";
    public string PakRegistryFilter
    {
        get => _pakRegistryFilter;
        set => SetProperty(ref _pakRegistryFilter, value);
    }

    private string _pakRegistryName;
    /// <summary>
    /// file name of the merged registry inside the pak
    /// </summary>
    public string PakRegistryName
    {
        get => _pakRegistryName;
        set => SetProperty(ref _pakRegistryName, value);
    }

    private bool _pakWriteSignature = true;
    /// <summary>
    /// writes a .sig next to the pak
    /// </summary>
    public bool PakWriteSignature
    {
        get => _pakWriteSignature;
        set => SetProperty(ref _pakWriteSignature, value);
    }

    private string _pakSignatureTemplate;
    /// <summary>
    /// a .sig of the game the signed hash is copied from
    /// </summary>
    public string PakSignatureTemplate
    {
        get => _pakSignatureTemplate;
        set => SetProperty(ref _pakSignatureTemplate, value);
    }

    private string _rawDataDirectory;
    public string RawDataDirectory
    {
        get => _rawDataDirectory;
        set => SetProperty(ref _rawDataDirectory, value);
    }

    private string _propertiesDirectory;
    public string PropertiesDirectory
    {
        get => _propertiesDirectory;
        set => SetProperty(ref _propertiesDirectory, value);
    }

    private string _textureDirectory;
    public string TextureDirectory
    {
        get => _textureDirectory;
        set => SetProperty(ref _textureDirectory, value);
    }

    private string _audioDirectory;
    public string AudioDirectory
    {
        get => _audioDirectory;
        set => SetProperty(ref _audioDirectory, value);
    }

    private string _codeDirectory;
    public string CodeDirectory
    {
        get => _codeDirectory;
        set => SetProperty(ref _codeDirectory, value);
    }

    private string _modelDirectory;
    public string ModelDirectory
    {
        get => _modelDirectory;
        set => SetProperty(ref _modelDirectory, value);
    }

    private string _gameDirectory = string.Empty;
    public string GameDirectory
    {
        get => _gameDirectory;
        set => SetProperty(ref _gameDirectory, value);
    }

    private int _lastOpenedSettingTab;
    public int LastOpenedSettingTab
    {
        get => _lastOpenedSettingTab;
        set => SetProperty(ref _lastOpenedSettingTab, value);
    }

    private bool _isLoggerExpanded = true;
    public bool IsLoggerExpanded
    {
        get => _isLoggerExpanded;
        set => SetProperty(ref _isLoggerExpanded, value);
    }

    private GridLength _avalonImageSize = new (200);
    public GridLength AvalonImageSize
    {
        get => _avalonImageSize;
        set => SetProperty(ref _avalonImageSize, value);
    }

    private string _audioDeviceId;
    public string AudioDeviceId
    {
        get => _audioDeviceId;
        set => SetProperty(ref _audioDeviceId, value);
    }

    private float _audioPlayerVolume = 50.0F;
    public float AudioPlayerVolume
    {
        get => _audioPlayerVolume;
        set => SetProperty(ref _audioPlayerVolume, value);
    }

    private ELoadingMode _loadingMode = ELoadingMode.All;
    public ELoadingMode LoadingMode
    {
        get => _loadingMode;
        set => SetProperty(ref _loadingMode, value);
    }

    private DateTime _lastUpdateCheck = DateTime.MinValue;
    public DateTime LastUpdateCheck
    {
        get => _lastUpdateCheck;
        set => SetProperty(ref _lastUpdateCheck, value);
    }

    private DateTime _nextUpdateCheck = DateTime.Now;
    public DateTime NextUpdateCheck
    {
        get => _nextUpdateCheck;
        set => SetProperty(ref _nextUpdateCheck, value);
    }

    private bool _keepDirectoryStructure = true;
    public bool KeepDirectoryStructure
    {
        get => _keepDirectoryStructure;
        set => SetProperty(ref _keepDirectoryStructure, value);
    }

    private bool _showDecompileOption = false;
    public bool ShowDecompileOption
    {
        get => _showDecompileOption;
        set => SetProperty(ref _showDecompileOption, value);
    }

    private ECompressedAudio _compressedAudioMode = ECompressedAudio.PlayDecompressed;
    public ECompressedAudio CompressedAudioMode
    {
        get => _compressedAudioMode;
        set => SetProperty(ref _compressedAudioMode, value);
    }

    private EAesReload _aesReload = EAesReload.OncePerDay;
    public EAesReload AesReload
    {
        get => _aesReload;
        set => SetProperty(ref _aesReload, value);
    }

    private EDiscordRpc _discordRpc = EDiscordRpc.Always;
    public EDiscordRpc DiscordRpc
    {
        get => _discordRpc;
        set => SetProperty(ref _discordRpc, value);
    }

    private ELanguage _assetLanguage = ELanguage.English;
    public ELanguage AssetLanguage
    {
        get => _assetLanguage;
        set => SetProperty(ref _assetLanguage, value);
    }

    private EInterfaceLanguage _interfaceLanguage = EInterfaceLanguage.English;
    public EInterfaceLanguage InterfaceLanguage
    {
        get => _interfaceLanguage;
        set => SetProperty(ref _interfaceLanguage, value);
    }

    private EIconStyle _cosmeticStyle = EIconStyle.Default;
    public EIconStyle CosmeticStyle
    {
        get => _cosmeticStyle;
        set => SetProperty(ref _cosmeticStyle, value);
    }

    private bool _cosmeticDisplayAsset;
    public bool CosmeticDisplayAsset
    {
        get => _cosmeticDisplayAsset;
        set => SetProperty(ref _cosmeticDisplayAsset, value);
    }

    private int _imageMergerMargin = 5;
    public int ImageMergerMargin
    {
        get => _imageMergerMargin;
        set => SetProperty(ref _imageMergerMargin, value);
    }

    private bool _readScriptData;
    public bool ReadScriptData
    {
        get => _readScriptData;
        set => SetProperty(ref _readScriptData, value);
    }

    private bool _readShaderMaps;
    public bool ReadShaderMaps
    {
        get => _readShaderMaps;
        set => SetProperty(ref _readShaderMaps, value);
    }

    private bool _convertAudioOnBulkExport;
    public bool ConvertAudioOnBulkExport
    {
        get => _convertAudioOnBulkExport;
        set => SetProperty(ref _convertAudioOnBulkExport, value);
    }

    private int _maxExportPerPage = 1;
    /// <summary>
    /// how many exports get displayed at once for packages big enough to be paginated
    /// (see <see cref="Extensions.CUE4ParseExtensions.LoadPackageResult"/>)
    /// </summary>
    public int MaxExportPerPage
    {
        get => _maxExportPerPage;
        set => SetProperty(ref _maxExportPerPage, Math.Clamp(value, 1, 1000));
    }

    private bool _mergeEditorOnlyDataExports = false;
    public bool MergeEditorOnlyDataExports
    {
        get => _mergeEditorOnlyDataExports;
        set => SetProperty(ref _mergeEditorOnlyDataExports, value);
    }

    private bool _decompileLua;
    public bool DecompileLua
    {
        get => _decompileLua;
        set => SetProperty(ref _decompileLua, value);
    }

    [JsonIgnore]
    public EUnluacMode UnluacMode
    {
        get => UnluacFlags.HasFlag(EUnluacFlags.Disassemble) ? EUnluacMode.Disassemble : EUnluacMode.Decompile;
        set
        {
            var withoutMode = UnluacFlags & ~(EUnluacFlags.Decompile | EUnluacFlags.Disassemble);
            var modeFlag = value == EUnluacMode.Disassemble ? EUnluacFlags.Disassemble : EUnluacFlags.Decompile;
            UnluacFlags = withoutMode | modeFlag;
        }
    }

    private EUnluacFlags _unluacFlags = EUnluacFlags.Decompile;
    public EUnluacFlags UnluacFlags
    {
        get => _unluacFlags;
        set
        {
            if (!SetProperty(ref _unluacFlags, value)) return;
            RaisePropertyChanged(nameof(UnluacMode));
        }
    }

    private EJsonHighlightTheme _jsonHighlightTheme;
    public EJsonHighlightTheme JsonHighlightTheme
    {
        get => _jsonHighlightTheme;
        set => SetProperty(ref _jsonHighlightTheme, value);
    }

    private IDictionary<string, DirectorySettings> _perDirectory = new Dictionary<string, DirectorySettings>(GameDirectoryComparer.Instance);
    /// <summary>
    /// ゲームフォルダごとの設定。キーは <see cref="GameDirectoryComparer"/> で比較する。
    /// 読み込み時は Replace にして、重複を含んだままの辞書を受け取り <see cref="OnDeserialized"/> でまとめる
    /// (既存の辞書へ追加させると重複キーが後勝ちで黙って上書きされる)。
    /// </summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public IDictionary<string, DirectorySettings> PerDirectory
    {
        get => _perDirectory;
        set => SetProperty(ref _perDirectory, value);
    }

    /// <summary>読み込み時に同じフォルダとしてまとめた重複登録の数 (ログ用)</summary>
    [JsonIgnore]
    public int MergedDuplicateDirectories { get; private set; }

    [OnDeserialized]
    private void OnDeserialized(StreamingContext context)
    {
        var source = _perDirectory ?? new Dictionary<string, DirectorySettings>();
        var merged = new Dictionary<string, DirectorySettings>(GameDirectoryComparer.Instance);
        var entries = source.Where(x => x.Value != null).Select((x, order) => (x.Key, x.Value, Order: order));
        foreach (var group in entries.GroupBy(x => x.Key, GameDirectoryComparer.Instance))
        {
            // 最後に使っていた登録 > 設定済みの項目が多い登録 > 自動検出された登録 > 後から追加された登録
            var best = group
                .OrderByDescending(x => string.Equals(x.Key, GameDirectory, StringComparison.Ordinal) ||
                                        string.Equals(x.Value.GameDirectory, GameDirectory, StringComparison.Ordinal))
                .ThenByDescending(x => ConfigurationScore(x.Value))
                .ThenByDescending(x => !x.Value.IsManual)
                .ThenByDescending(x => x.Order)
                .First();

            merged[best.Key] = best.Value;
            MergedDuplicateDirectories += group.Count() - 1;
        }

        _perDirectory = merged;
    }

    private static int ConfigurationScore(DirectorySettings settings)
    {
        var score = settings.Endpoints?.Count(e => e is { IsValid: true } || e is { Overwrite: true }) ?? 0;
        if (!string.IsNullOrEmpty(settings.AesKeys?.MainKey) || settings.AesKeys?.HasDynamicKeys == true)
            score++;
        return score;
    }

    [JsonIgnore]
    public DirectorySettings CurrentDir { get; set; }

    /// <summary>
    /// TO DELETEEEEEEEEEEEEE
    /// </summary>
    private IDictionary<string, GameSelectorViewModel.DetectedGame> _manualGames = new Dictionary<string, GameSelectorViewModel.DetectedGame>();
    public IDictionary<string, GameSelectorViewModel.DetectedGame> ManualGames
    {
        get => _manualGames;
        set => SetProperty(ref _manualGames, value);
    }

    private AuthResponse _lastAuthResponse = new() {AccessToken = "", ExpiresAt = DateTime.Now};
    public AuthResponse LastAuthResponse
    {
        get => _lastAuthResponse;
        set => SetProperty(ref _lastAuthResponse, value);
    }

    private Hotkey _dirLeftTab = new(Key.A);
    public Hotkey DirLeftTab
    {
        get => _dirLeftTab;
        set => SetProperty(ref _dirLeftTab, value);
    }

    private Hotkey _dirRightTab = new(Key.D);
    public Hotkey DirRightTab
    {
        get => _dirRightTab;
        set => SetProperty(ref _dirRightTab, value);
    }

    private Hotkey _switchAssetExplorer = new(Key.Z);
    public Hotkey SwitchAssetExplorer
    {
        get => _switchAssetExplorer;
        set => SetProperty(ref _switchAssetExplorer, value);
    }

    private Hotkey _assetLeftTab = new(Key.Q);
    public Hotkey AssetLeftTab
    {
        get => _assetLeftTab;
        set => SetProperty(ref _assetLeftTab, value);
    }

    private Hotkey _assetRightTab = new(Key.E);
    public Hotkey AssetRightTab
    {
        get => _assetRightTab;
        set => SetProperty(ref _assetRightTab, value);
    }

    private Hotkey _assetAddTab = new(Key.T, ModifierKeys.Control);
    public Hotkey AssetAddTab
    {
        get => _assetAddTab;
        set => SetProperty(ref _assetAddTab, value);
    }

    private Hotkey _assetRemoveTab = new(Key.W, ModifierKeys.Control);
    public Hotkey AssetRemoveTab
    {
        get => _assetRemoveTab;
        set => SetProperty(ref _assetRemoveTab, value);
    }

    private Hotkey _addAudio = new(Key.N, ModifierKeys.Control);
    public Hotkey AddAudio
    {
        get => _addAudio;
        set => SetProperty(ref _addAudio, value);
    }

    private Hotkey _removeAudio = new(Key.X);
    public Hotkey RemoveAudio
    {
        get => _removeAudio;
        set => SetProperty(ref _removeAudio, value);
    }

    private Hotkey _playPauseAudio = new(Key.K);
    public Hotkey PlayPauseAudio
    {
        get => _playPauseAudio;
        set => SetProperty(ref _playPauseAudio, value);
    }

    private Hotkey _previousAudio = new(Key.J);
    public Hotkey PreviousAudio
    {
        get => _previousAudio;
        set => SetProperty(ref _previousAudio, value);
    }

    private Hotkey _nextAudio = new(Key.L);
    public Hotkey NextAudio
    {
        get => _nextAudio;
        set => SetProperty(ref _nextAudio, value);
    }

    private Hotkey _exportData = new(Key.None);
    public Hotkey ExportData
    {
        get => _exportData;
        set => SetProperty(ref _exportData, value);
    }

    private Hotkey _exportProperties = new(Key.None);
    public Hotkey ExportProperties
    {
        get => _exportProperties;
        set => SetProperty(ref _exportProperties, value);
    }

    private Hotkey _exportTextures = new(Key.None);
    public Hotkey ExportTextures
    {
        get => _exportTextures;
        set => SetProperty(ref _exportTextures, value);
    }

    private Hotkey _exportModels = new(Key.None);
    public Hotkey ExportModels
    {
        get => _exportModels;
        set => SetProperty(ref _exportModels, value);
    }

    private Hotkey _exportWorlds = new(Key.None);
    public Hotkey ExportWorlds
    {
        get => _exportWorlds;
        set => SetProperty(ref _exportWorlds, value);
    }

    private Hotkey _exportAnimations = new(Key.None);
    public Hotkey ExportAnimations
    {
        get => _exportAnimations;
        set => SetProperty(ref _exportAnimations, value);
    }

    private Hotkey _exportAudio = new(Key.None);
    public Hotkey ExportAudio
    {
        get => _exportAudio;
        set => SetProperty(ref _exportAudio, value);
    }

    private Hotkey _exportCode = new(Key.None);
    public Hotkey ExportCode
    {
        get => _exportCode;
        set => SetProperty(ref _exportCode, value);
    }

    private EMeshFormat _meshExportFormat = EMeshFormat.UEFormat;
    public EMeshFormat MeshExportFormat
    {
        get => _meshExportFormat;
        set => SetProperty(ref _meshExportFormat, value);
    }

    private ENaniteMeshFormat _naniteMeshExportFormat = ENaniteMeshFormat.NaniteOnly;
    public ENaniteMeshFormat NaniteMeshExportFormat
    {
        get => _naniteMeshExportFormat;
        set => SetProperty(ref _naniteMeshExportFormat, value);
    }

    private EMeshQuality _meshQuality = EMeshQuality.Highest;
    public EMeshQuality MeshQuality
    {
        get => _meshQuality;
        set => SetProperty(ref _meshQuality, value);
    }

    private EMaterialDepth _materialExportFormat = EMaterialDepth.TopLayerOnly;
    public EMaterialDepth MaterialExportFormat
    {
        get => _materialExportFormat;
        set => SetProperty(ref _materialExportFormat, value);
    }

    private ETextureFormat _textureExportFormat = ETextureFormat.Png;
    public ETextureFormat TextureExportFormat
    {
        get => _textureExportFormat;
        set => SetProperty(ref _textureExportFormat, value);
    }

    private int _textureQuality = 100;
    public int TextureQuality
    {
        get => _textureQuality;
        set => SetProperty(ref _textureQuality, value);
    }

    private ESocketFormat _socketExportFormat = ESocketFormat.Bone;
    public ESocketFormat SocketExportFormat
    {
        get => _socketExportFormat;
        set => SetProperty(ref _socketExportFormat, value);
    }

    private EFileCompressionFormat _compressionFormat = EFileCompressionFormat.ZSTD;
    public EFileCompressionFormat CompressionFormat
    {
        get => _compressionFormat;
        set => SetProperty(ref _compressionFormat, value);
    }

    private bool _showSkybox = true;
    public bool ShowSkybox
    {
        get => _showSkybox;
        set => SetProperty(ref _showSkybox, value);
    }

    private bool _showGrid = true;
    public bool ShowGrid
    {
        get => _showGrid;
        set => SetProperty(ref _showGrid, value);
    }

    private bool _animateWithRotationOnly;
    public bool AnimateWithRotationOnly
    {
        get => _animateWithRotationOnly;
        set => SetProperty(ref _animateWithRotationOnly, value);
    }

    private Camera.WorldMode _cameraMode = Camera.WorldMode.Arcball;
    public Camera.WorldMode CameraMode
    {
        get => _cameraMode;
        set => SetProperty(ref _cameraMode, value);
    }

    private int _previewMaxTextureSize = 1024;
    public int PreviewMaxTextureSize
    {
        get => _previewMaxTextureSize;
        set => SetProperty(ref _previewMaxTextureSize, value);
    }

    private bool _previewStaticMeshes = true;
    public bool PreviewStaticMeshes
    {
        get => _previewStaticMeshes;
        set => SetProperty(ref _previewStaticMeshes, value);
    }

    private bool _previewSkeletalMeshes = true;
    public bool PreviewSkeletalMeshes
    {
        get => _previewSkeletalMeshes;
        set => SetProperty(ref _previewSkeletalMeshes, value);
    }

    private bool _previewAnimations = true;
    public bool PreviewAnimations
    {
        get => _previewAnimations;
        set => SetProperty(ref _previewAnimations, value);
    }

    private bool _previewMaterials = true;
    public bool PreviewMaterials
    {
        get => _previewMaterials;
        set => SetProperty(ref _previewMaterials, value);
    }

    private bool _previewWorlds = true;
    public bool PreviewWorlds
    {
        get => _previewWorlds;
        set => SetProperty(ref _previewWorlds, value);
    }

    private bool _saveMorphTargets = true;
    public bool SaveMorphTargets
    {
        get => _saveMorphTargets;
        set => SetProperty(ref _saveMorphTargets, value);
    }

    private bool _saveEmbeddedMaterials = true;
    public bool SaveEmbeddedMaterials
    {
        get => _saveEmbeddedMaterials;
        set => SetProperty(ref _saveEmbeddedMaterials, value);
    }

    private bool _saveSkeletonAsMesh;
    public bool SaveSkeletonAsMesh
    {
        get => _saveSkeletonAsMesh;
        set => SetProperty(ref _saveSkeletonAsMesh, value);
    }

    private bool _saveHdrTexturesAsHdr = true;
    public bool SaveHdrTexturesAsHdr
    {
        get => _saveHdrTexturesAsHdr;
        set => SetProperty(ref _saveHdrTexturesAsHdr, value);
    }

    private bool _featurePreviewNewAssetExplorer = true;
    public bool FeaturePreviewNewAssetExplorer
    {
        get => _featurePreviewNewAssetExplorer;
        set => SetProperty(ref _featurePreviewNewAssetExplorer, value);
    }

    private bool _previewTexturesAssetExplorer = true;
    public bool PreviewTexturesAssetExplorer
    {
        get => _previewTexturesAssetExplorer;
        set => SetProperty(ref _previewTexturesAssetExplorer, value);
    }

    private EExplorerViewMode _explorerViewMode = EExplorerViewMode.Grid;

    public EExplorerViewMode ExplorerViewMode
    {
        get => _explorerViewMode;
        set => SetProperty(ref _explorerViewMode, value);
    }

    private bool _exportAllTextureMips = false;
    public bool ExportAllTextureMips
    {
        get => _exportAllTextureMips;
        set => SetProperty(ref _exportAllTextureMips, value);
    }

    private bool _exportImmediately;
    public bool ExportImmediately
    {
        get => _exportImmediately;
        set => SetProperty(ref _exportImmediately, value);
    }
}
