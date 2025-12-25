using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Reflection;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.VirtualFileCache.Manifest;
using System.Windows.Input; // For ICommand
using System.Windows.Media; // For Brush
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.UEFormat.Enums;
using CUE4Parse.UE4.Assets.Exports.Nanite;
using EpicManifestParser;
using EpicManifestParser.Api;
using EpicManifestParser.UE;
using EpicManifestParser.ZlibngDotNetDecompressor;
using CUE4Parse.Compression;
using FModel.Framework;
using FModel.ViewModels.ApiEndpoints.Models;
using FModel.Services;
using FModel.Settings;
using FModel.Views;
using FModel.Views.Resources.Controls;
using Serilog;

namespace FModel.ViewModels;

public partial class SettingsViewModel : ViewModel
{
    // Private snapshot fields to track changes for restart logic
    private string _outputSnapshot;
    private string _rawDataSnapshot;
    private string _propertiesSnapshot;
    private string _textureSnapshot;
    private string _audioSnapshot;
    private string _modelSnapshot;
    private string _gameSnapshot;
    private ETexturePlatform _uePlatformSnapshot;
    private EGame _ueGameSnapshot;
    private IList<FCustomVersion> _customVersionsSnapshot;
    private IDictionary<string, bool> _optionsSnapshot;
    private IDictionary<string, KeyValuePair<string, string>> _mapStructTypesSnapshot;
    private string _diffGameSnapshot;
    private EGame? _diffUeGameSnapshot;
    private ELanguage _assetLanguageSnapshot;
    private ECompressedAudio _compressedAudioSnapshot;
    private EIconStyle _cosmeticStyleSnapshot;
    private EMeshFormat _meshExportFormatSnapshot;
    private ESocketFormat _socketExportFormatSnapshot;
    private EFileCompressionFormat _compressionFormatSnapshot;
    private ELodFormat _lodExportFormatSnapshot;
    private ENaniteMeshFormat _naniteMeshExportFormatSnapshot;
    private EMaterialFormat _materialExportFormatSnapshot;
    private ETextureFormat _textureExportFormatSnapshot;
    private bool _mappingsUpdate;
    private bool _restoreTabsOnStartupSnapshot;

    private readonly EpicGamesAuthService _epicGamesAuthService;
    private readonly DiscordHandler _discordHandler = DiscordService.DiscordHandler;

    private bool _useCustomOutputFolders;
    public bool UseCustomOutputFolders
    {
        get => _useCustomOutputFolders;
        set => SetProperty(ref _useCustomOutputFolders, value);
    }

    private ETexturePlatform _selectedUePlatform;
    public ETexturePlatform SelectedUePlatform
    {
        get => _selectedUePlatform;
        set => SetProperty(ref _selectedUePlatform, value);
    }

    private EGame _selectedUeGame;
    public EGame SelectedUeGame
    {
        get => _selectedUeGame;
        set => SetProperty(ref _selectedUeGame, value);
    }
    private EGame? _selectedDiffUeGame;
    public EGame? SelectedDiffUeGame
    {
        get => _selectedDiffUeGame;
        set => SetProperty(ref _selectedDiffUeGame, value);
    }
    private IList<FCustomVersion> _selectedCustomVersions;
    public IList<FCustomVersion> SelectedCustomVersions
    {
        get => _selectedCustomVersions;
        set => SetProperty(ref _selectedCustomVersions, value);
    }

    private IDictionary<string, bool> _selectedOptions;
    public IDictionary<string, bool> SelectedOptions
    {
        get => _selectedOptions;
        set => SetProperty(ref _selectedOptions, value);
    }

    private IDictionary<string, KeyValuePair<string, string>> _selectedMapStructTypes;
    public IDictionary<string, KeyValuePair<string, string>> SelectedMapStructTypes
    {
        get => _selectedMapStructTypes;
        set => SetProperty(ref _selectedMapStructTypes, value);
    }

    private EndpointSettings _aesEndpoint;
    public EndpointSettings AesEndpoint
    {
        get => _aesEndpoint;
        set => SetProperty(ref _aesEndpoint, value);
    }

    private EndpointSettings _mappingEndpoint;
    public EndpointSettings MappingEndpoint
    {
        get => _mappingEndpoint;
        set => SetProperty(ref _mappingEndpoint, value);
    }

    private EndpointSettings _diffAesEndpoint;
    public EndpointSettings DiffAesEndpoint
    {
        get => _diffAesEndpoint;
        set => SetProperty(ref _diffAesEndpoint, value);
    }

    private EndpointSettings _diffMappingEndpoint;
    public EndpointSettings DiffMappingEndpoint
    {
        get => _diffMappingEndpoint;
        set => SetProperty(ref _diffMappingEndpoint, value);
    }
    private ELanguage _selectedAssetLanguage;
    public ELanguage SelectedAssetLanguage
    {
        get => _selectedAssetLanguage;
        set => SetProperty(ref _selectedAssetLanguage, value);
    }

    private EAesReload _selectedAesReload;
    public EAesReload SelectedAesReload
    {
        get => _selectedAesReload;
        set => SetProperty(ref _selectedAesReload, value);
    }

    private EDiscordRpc _selectedDiscordRpc;
    public EDiscordRpc SelectedDiscordRpc
    {
        get => _selectedDiscordRpc;
        set => SetProperty(ref _selectedDiscordRpc, value);
    }

    private ECompressedAudio _selectedCompressedAudio;
    public ECompressedAudio SelectedCompressedAudio
    {
        get => _selectedCompressedAudio;
        set => SetProperty(ref _selectedCompressedAudio, value);
    }

    private EIconStyle _selectedCosmeticStyle;
    public EIconStyle SelectedCosmeticStyle
    {
        get => _selectedCosmeticStyle;
        set => SetProperty(ref _selectedCosmeticStyle, value);
    }

    private EMeshFormat _selectedMeshExportFormat;
    public EMeshFormat SelectedMeshExportFormat
    {
        get => _selectedMeshExportFormat;
        set
        {
            SetProperty(ref _selectedMeshExportFormat, value);
            RaisePropertyChanged(nameof(SocketSettingsEnabled));
            RaisePropertyChanged(nameof(CompressionSettingsEnabled));
        }
    }

    private ESocketFormat _selectedSocketExportFormat;
    public ESocketFormat SelectedSocketExportFormat
    {
        get => _selectedSocketExportFormat;
        set => SetProperty(ref _selectedSocketExportFormat, value);
    }

    private EFileCompressionFormat _selectedCompressionFormat;
    public EFileCompressionFormat SelectedCompressionFormat
    {
        get => _selectedCompressionFormat;
        set => SetProperty(ref _selectedCompressionFormat, value);
    }

    private ELodFormat _selectedLodExportFormat;
    public ELodFormat SelectedLodExportFormat
    {
        get => _selectedLodExportFormat;
        set => SetProperty(ref _selectedLodExportFormat, value);
    }

    private ENaniteMeshFormat _selectedNaniteMeshExportFormat;
    public ENaniteMeshFormat SelectedNaniteMeshExportFormat
    {
        get => _selectedNaniteMeshExportFormat;
        set => SetProperty(ref _selectedNaniteMeshExportFormat, value);
    }

    private EMaterialFormat _selectedMaterialExportFormat;
    public EMaterialFormat SelectedMaterialExportFormat
    {
        get => _selectedMaterialExportFormat;
        set => SetProperty(ref _selectedMaterialExportFormat, value);
    }

    private ETextureFormat _selectedTextureExportFormat;
    public ETextureFormat SelectedTextureExportFormat
    {
        get => _selectedTextureExportFormat;
        set => SetProperty(ref _selectedTextureExportFormat, value);
    }

    private bool _selectedRestoreTabsOnStartup;
    public bool SelectedRestoreTabsOnStartup
    {
        get => _selectedRestoreTabsOnStartup;
        set => SetProperty(ref _selectedRestoreTabsOnStartup, value);
    }


    public bool SocketSettingsEnabled => SelectedMeshExportFormat == EMeshFormat.ActorX;
    public bool CompressionSettingsEnabled => SelectedMeshExportFormat == EMeshFormat.UEFormat;

    public ReadOnlyObservableCollection<EGame> UeGames { get; private set; }
    public ReadOnlyObservableCollection<EGame> DiffUeGames { get; private set; }
    public ReadOnlyObservableCollection<ELanguage> AssetLanguages { get; private set; }
    public ReadOnlyObservableCollection<EAesReload> AesReloads { get; private set; }
    public ReadOnlyObservableCollection<EDiscordRpc> DiscordRpcs { get; private set; }
    public ReadOnlyObservableCollection<ECompressedAudio> CompressedAudios { get; private set; }
    public ReadOnlyObservableCollection<EIconStyle> CosmeticStyles { get; private set; }
    public ReadOnlyObservableCollection<EMeshFormat> MeshExportFormats { get; private set; }
    public ReadOnlyObservableCollection<ESocketFormat> SocketExportFormats { get; private set; }
    public ReadOnlyObservableCollection<EFileCompressionFormat> CompressionFormats { get; private set; }
    public ReadOnlyObservableCollection<ELodFormat> LodExportFormats { get; private set; }
    public ReadOnlyObservableCollection<ENaniteMeshFormat> NaniteMeshExportFormats { get; private set; }
    public ReadOnlyObservableCollection<EMaterialFormat> MaterialExportFormats { get; private set; }
    public ReadOnlyObservableCollection<ETextureFormat> TextureExportFormats { get; private set; }
    public ReadOnlyObservableCollection<ETexturePlatform> Platforms { get; private set; }

    // New properties for Epic Games API authentication and AES Key retrieval
    private string _epicAuthStatusText = "Not Authenticated";
    public string EpicAuthStatusText
    {
        get => _epicAuthStatusText;
        set => SetProperty(ref _epicAuthStatusText, value);
    }

    private Brush _epicAuthStatusForeground = Brushes.Red;
    public Brush EpicAuthStatusForeground
    {
        get => _epicAuthStatusForeground;
        set => SetProperty(ref _epicAuthStatusForeground, value);
    }

    private string _mapCodeInput;
    public string MapCodeInput
    {
        get => _mapCodeInput;
        set => SetProperty(ref _mapCodeInput, value);
    }

    private string _retrievedAesKey;
    public string RetrievedAesKey
    {
        get => _retrievedAesKey;
        set => SetProperty(ref _retrievedAesKey, value);
    }

    // Commands
    public ICommand AuthenticateEpicGamesCommand { get; }
    public ICommand GetAesKeyCommand { get; }
    public ICommand CopyAesKeyCommand { get; }
    public ICommand SaveAesKeyCommand { get; }

    // Pac化
    public ICommand BrowseInstalledBundlesCommand { get; private set; }
    public ICommand ExecutePacCommand { get; private set; }



    public void Initialize()
    {
        _outputSnapshot = UserSettings.Default.OutputDirectory;
        _rawDataSnapshot = UserSettings.Default.RawDataDirectory; // _rawDataSnapshot の初期化
        _propertiesSnapshot = UserSettings.Default.PropertiesDirectory; // _propertiesSnapshot の初期化
        _textureSnapshot = UserSettings.Default.TextureDirectory; // _textureSnapshot の初期化
        _audioSnapshot = UserSettings.Default.AudioDirectory; // _audioSnapshot の初期化
        _modelSnapshot = UserSettings.Default.ModelDirectory;
        _gameSnapshot = UserSettings.Default.GameDirectory;
        _uePlatformSnapshot = UserSettings.Default.CurrentDir.TexturePlatform;
        _ueGameSnapshot = UserSettings.Default.CurrentDir.UeVersion;
        _customVersionsSnapshot = UserSettings.Default.CurrentDir.Versioning.CustomVersions;
        _optionsSnapshot = UserSettings.Default.CurrentDir.Versioning.Options;
        _mapStructTypesSnapshot = UserSettings.Default.CurrentDir.Versioning.MapStructTypes;

        _diffGameSnapshot = UserSettings.Default.DiffGameDirectory;
        if (UserSettings.Default.DiffDir != null) _diffUeGameSnapshot = UserSettings.Default.DiffDir.UeVersion;

        AesEndpoint = UserSettings.Default.CurrentDir.Endpoints[0];
        MappingEndpoint = UserSettings.Default.CurrentDir.Endpoints[1];
        MappingEndpoint.PropertyChanged += (_, args) =>
        {
            if (!_mappingsUpdate)
                _mappingsUpdate = args.PropertyName is "Overwrite" or "FilePath";
        };

        if (UserSettings.Default.DiffDir != null)
        {
            DiffAesEndpoint = UserSettings.Default.DiffDir.Endpoints[0];
            DiffMappingEndpoint = UserSettings.Default.DiffDir.Endpoints[1];
            DiffMappingEndpoint.PropertyChanged += (_, args) =>
            {
                if (!_mappingsUpdate)
                    _mappingsUpdate = args.PropertyName is "Overwrite" or "FilePath";
            };
        }

        _assetLanguageSnapshot = UserSettings.Default.AssetLanguage;
        _compressedAudioSnapshot = UserSettings.Default.CompressedAudioMode;
        _cosmeticStyleSnapshot = UserSettings.Default.CosmeticStyle;
        _meshExportFormatSnapshot = UserSettings.Default.MeshExportFormat;
        _socketExportFormatSnapshot = UserSettings.Default.SocketExportFormat;
        _compressionFormatSnapshot = UserSettings.Default.CompressionFormat;
        _lodExportFormatSnapshot = UserSettings.Default.LodExportFormat;
        _naniteMeshExportFormatSnapshot = UserSettings.Default.NaniteMeshExportFormat;
        _materialExportFormatSnapshot = UserSettings.Default.MaterialExportFormat;
        _textureExportFormatSnapshot = UserSettings.Default.TextureExportFormat;
        _restoreTabsOnStartupSnapshot = UserSettings.Default.RestoreTabsOnStartup;

        SelectedUePlatform = _uePlatformSnapshot;
        SelectedUeGame = _ueGameSnapshot;
        SelectedDiffUeGame = _diffUeGameSnapshot;
        SelectedCustomVersions = _customVersionsSnapshot;
        SelectedOptions = _optionsSnapshot;
        SelectedMapStructTypes = _mapStructTypesSnapshot;
        SelectedAssetLanguage = _assetLanguageSnapshot;
        SelectedCompressedAudio = _compressedAudioSnapshot;
        SelectedCosmeticStyle = _cosmeticStyleSnapshot;
        SelectedMeshExportFormat = _meshExportFormatSnapshot;
        SelectedSocketExportFormat = _socketExportFormatSnapshot;
        SelectedCompressionFormat = _selectedCompressionFormat;
        SelectedLodExportFormat = _lodExportFormatSnapshot;
        SelectedNaniteMeshExportFormat = _naniteMeshExportFormatSnapshot;
        SelectedMaterialExportFormat = _materialExportFormatSnapshot;
        SelectedTextureExportFormat = _textureExportFormatSnapshot;
        SelectedAesReload = UserSettings.Default.AesReload;
        SelectedRestoreTabsOnStartup = UserSettings.Default.RestoreTabsOnStartup;
        SelectedDiscordRpc = UserSettings.Default.DiscordRpc;

        var ueGames = new ObservableCollection<EGame>(EnumerateUeGames());
        UeGames = new ReadOnlyObservableCollection<EGame>(ueGames);
        DiffUeGames = new ReadOnlyObservableCollection<EGame>(ueGames); // Can't reuse UeGames because FilterableComboBox would share the same ItemsSource
        AssetLanguages = new ReadOnlyObservableCollection<ELanguage>(new ObservableCollection<ELanguage>(EnumerateAssetLanguages()));
        AesReloads = new ReadOnlyObservableCollection<EAesReload>(new ObservableCollection<EAesReload>(EnumerateAesReloads()));
        DiscordRpcs = new ReadOnlyObservableCollection<EDiscordRpc>(new ObservableCollection<EDiscordRpc>(EnumerateDiscordRpcs()));
        CompressedAudios = new ReadOnlyObservableCollection<ECompressedAudio>(new ObservableCollection<ECompressedAudio>(EnumerateCompressedAudios()));
        CosmeticStyles = new ReadOnlyObservableCollection<EIconStyle>(new ObservableCollection<EIconStyle>(EnumerateCosmeticStyles()));
        MeshExportFormats = new ReadOnlyObservableCollection<EMeshFormat>(new ObservableCollection<EMeshFormat>(EnumerateMeshExportFormat()));
        SocketExportFormats = new ReadOnlyObservableCollection<ESocketFormat>(new ObservableCollection<ESocketFormat>(EnumerateSocketExportFormat()));
        CompressionFormats = new ReadOnlyObservableCollection<EFileCompressionFormat>(new ObservableCollection<EFileCompressionFormat>(EnumerateCompressionFormat()));
        LodExportFormats = new ReadOnlyObservableCollection<ELodFormat>(new ObservableCollection<ELodFormat>(EnumerateLodExportFormat()));
        NaniteMeshExportFormats = new ReadOnlyObservableCollection<ENaniteMeshFormat>(new ObservableCollection<ENaniteMeshFormat>(EnumerateNaniteMeshExportFormat()));
        MaterialExportFormats = new ReadOnlyObservableCollection<EMaterialFormat>(new ObservableCollection<EMaterialFormat>(EnumerateMaterialExportFormat()));
        TextureExportFormats = new ReadOnlyObservableCollection<ETextureFormat>(new ObservableCollection<ETextureFormat>(EnumerateTextureExportFormat()));
        Platforms = new ReadOnlyObservableCollection<ETexturePlatform>(new ObservableCollection<ETexturePlatform>(EnumerateUePlatforms()));
    }

    public SettingsViewModel()
    {
        _epicGamesAuthService = new EpicGamesAuthService(new HttpClient());

        // Initialize settings and UI elements first
        Initialize();

        // Initialize commands
        AuthenticateEpicGamesCommand = new RelayCommand(AuthenticateEpicGames, CanAuthenticateEpicGames);
        GetAesKeyCommand = new RelayCommand(GetAesKey, CanGetAesKey);
        BrowseInstalledBundlesCommand = new RelayCommand(BrowseInstalledBundles);
        ExecutePacCommand = new RelayCommand(ExecutePac, CanExecutePac);
        CopyAesKeyCommand = new RelayCommand(CopyAesKey, CanCopyAesKey);
        SaveAesKeyCommand = new RelayCommand(SaveAesKey, CanSaveAesKey);

        // This needs to run after commands are initialized
        InitializeAuthStatus();
    }


    public bool Save(out List<SettingsOut> whatShouldIDo)
    {
        var restart = false;
        whatShouldIDo = [];

        if (_assetLanguageSnapshot != SelectedAssetLanguage)
            whatShouldIDo.Add(SettingsOut.ReloadLocres);
        if (_mappingsUpdate)
            whatShouldIDo.Add(SettingsOut.ReloadMappings);

        if (_ueGameSnapshot != SelectedUeGame || _diffUeGameSnapshot != SelectedDiffUeGame || _customVersionsSnapshot != SelectedCustomVersions ||
            _uePlatformSnapshot != SelectedUePlatform || _optionsSnapshot != SelectedOptions || // combobox
            _mapStructTypesSnapshot != SelectedMapStructTypes ||
            _outputSnapshot != UserSettings.Default.OutputDirectory || // textbox
            _rawDataSnapshot != UserSettings.Default.RawDataDirectory || // textbox
            _propertiesSnapshot != UserSettings.Default.PropertiesDirectory || // textbox
            _textureSnapshot != UserSettings.Default.TextureDirectory || // textbox
            _audioSnapshot != UserSettings.Default.AudioDirectory || // textbox
            _modelSnapshot != UserSettings.Default.ModelDirectory || // textbox
            _gameSnapshot != UserSettings.Default.GameDirectory ||
            _diffGameSnapshot != UserSettings.Default.DiffGameDirectory) // textbox
            restart = true;

        if (UserSettings.Default.DiffDir != null)
        {
            UserSettings.Default.DiffDir.UeVersion = SelectedDiffUeGame ?? default;
            UserSettings.Default.DiffDir.TexturePlatform = SelectedUePlatform;
            UserSettings.Default.DiffDir.Versioning.CustomVersions = SelectedCustomVersions;
            UserSettings.Default.DiffDir.Versioning.Options = SelectedOptions;
            UserSettings.Default.DiffDir.Versioning.MapStructTypes = SelectedMapStructTypes;
        }

        UserSettings.Default.CurrentDir.UeVersion = SelectedUeGame;
        UserSettings.Default.CurrentDir.TexturePlatform = SelectedUePlatform;
        UserSettings.Default.CurrentDir.Versioning.CustomVersions = SelectedCustomVersions;
        UserSettings.Default.CurrentDir.Versioning.Options = SelectedOptions;
        UserSettings.Default.CurrentDir.Versioning.MapStructTypes = SelectedMapStructTypes;

        UserSettings.Default.AssetLanguage = SelectedAssetLanguage;
        UserSettings.Default.CompressedAudioMode = SelectedCompressedAudio;
        UserSettings.Default.CosmeticStyle = SelectedCosmeticStyle;
        UserSettings.Default.MeshExportFormat = SelectedMeshExportFormat;
        UserSettings.Default.SocketExportFormat = SelectedSocketExportFormat;
        UserSettings.Default.CompressionFormat = SelectedCompressionFormat;
        UserSettings.Default.LodExportFormat = SelectedLodExportFormat;
        UserSettings.Default.NaniteMeshExportFormat = SelectedNaniteMeshExportFormat;
        UserSettings.Default.MaterialExportFormat = SelectedMaterialExportFormat;
        UserSettings.Default.TextureExportFormat = SelectedTextureExportFormat;
        UserSettings.Default.AesReload = SelectedAesReload;
        UserSettings.Default.RestoreTabsOnStartup = SelectedRestoreTabsOnStartup;
        UserSettings.Default.DiscordRpc = SelectedDiscordRpc;

        if (SelectedDiscordRpc == EDiscordRpc.Never)
            _discordHandler.Shutdown();

        return restart;
    }

    private IEnumerable<EGame> EnumerateUeGames()
        => Enum.GetValues<EGame>()
            .GroupBy(value => (int)value)
            .Select(group => group.First())
            .OrderBy(value => ((int)value & 0xFF) == 0);
    private IEnumerable<ELanguage> EnumerateAssetLanguages() => Enum.GetValues<ELanguage>();
    private IEnumerable<EAesReload> EnumerateAesReloads() => Enum.GetValues<EAesReload>();
    private IEnumerable<EDiscordRpc> EnumerateDiscordRpcs() => Enum.GetValues<EDiscordRpc>();
    private IEnumerable<ECompressedAudio> EnumerateCompressedAudios() => Enum.GetValues<ECompressedAudio>();
    private IEnumerable<EIconStyle> EnumerateCosmeticStyles() => Enum.GetValues<EIconStyle>();
    private IEnumerable<EMeshFormat> EnumerateMeshExportFormat() => Enum.GetValues<EMeshFormat>();
    private IEnumerable<ESocketFormat> EnumerateSocketExportFormat() => Enum.GetValues<ESocketFormat>();
    private IEnumerable<EFileCompressionFormat> EnumerateCompressionFormat() => Enum.GetValues<EFileCompressionFormat>();
    private IEnumerable<ELodFormat> EnumerateLodExportFormat() => Enum.GetValues<ELodFormat>();
    private IEnumerable<ENaniteMeshFormat> EnumerateNaniteMeshExportFormat() => Enum.GetValues<ENaniteMeshFormat>();
    private IEnumerable<EMaterialFormat> EnumerateMaterialExportFormat() => Enum.GetValues<EMaterialFormat>();
    private IEnumerable<ETextureFormat> EnumerateTextureExportFormat() => Enum.GetValues<ETextureFormat>();
    private IEnumerable<ETexturePlatform> EnumerateUePlatforms() => Enum.GetValues<ETexturePlatform>();
}

// Placeholder implementations for Epic Games API authentication and AES Key retrieval
public partial class SettingsViewModel // partial 修飾子を追加
{
    private async void InitializeAuthStatus()
    {
        var authData = await EpicGamesAuthService.LoadDeviceAuthAsync();
        if (authData != null)
        {
            if (authData.ExpiresAt > DateTime.Now)
            {
                UserSettings.Default.LastAuthResponse = new AuthResponse
                {
                    AccessToken = authData.AccessToken,
                    ExpiresAt = authData.ExpiresAt
                };
                EpicAuthStatusText = $"{authData.DisplayName}でログイン中";
                EpicAuthStatusForeground = Brushes.Green;
                ((RelayCommand)AuthenticateEpicGamesCommand).RaiseCanExecuteChanged();
                ((RelayCommand)GetAesKeyCommand).RaiseCanExecuteChanged();
                return;
            }

            // Token expired, try to refresh
            var refreshedAuth = await _epicGamesAuthService.RefreshTokenAsync(authData);
            if (refreshedAuth != null)
            {
                UserSettings.Default.LastAuthResponse = new AuthResponse
                {
                    AccessToken = refreshedAuth.AccessToken,
                    ExpiresAt = refreshedAuth.ExpiresAt
                };
                EpicAuthStatusText = $"{refreshedAuth.DisplayName}でログイン中";
                EpicAuthStatusForeground = Brushes.Green;
                ((RelayCommand)AuthenticateEpicGamesCommand).RaiseCanExecuteChanged();
                ((RelayCommand)GetAesKeyCommand).RaiseCanExecuteChanged();
                return;
            }
        }

        // No valid auth found
        UserSettings.Default.LastAuthResponse = null;
        EpicAuthStatusText = "Not Authenticated";
        EpicAuthStatusForeground = Brushes.Red;
        ((RelayCommand)AuthenticateEpicGamesCommand).RaiseCanExecuteChanged();
        ((RelayCommand)GetAesKeyCommand).RaiseCanExecuteChanged();
    }

    private async void AuthenticateEpicGames(object parameter)
    {
        FLogger.Append(ELog.Information, () => FLogger.Text("AuthenticateEpicGames command executed.", Constants.WHITE, true));
        EpicAuthStatusText = "Authenticating...";
        EpicAuthStatusForeground = Brushes.Orange;

        try
        {
            FLogger.Append(ELog.Information, () => FLogger.Text("Attempting to call _epicGamesAuthService.LoginAsync()", Constants.WHITE, true)); // Added log
            var authData = await _epicGamesAuthService.LoginAsync();
            if (authData != null)
            {
                UserSettings.Default.LastAuthResponse = new AuthResponse
                {
                    AccessToken = authData.AccessToken,
                    ExpiresAt = authData.ExpiresAt
                };
                EpicAuthStatusText = $"{authData.DisplayName}でログイン中";
                EpicAuthStatusForeground = Brushes.Green;
            }
            else
            {
                EpicAuthStatusText = "Authentication Failed";
                EpicAuthStatusForeground = Brushes.Red;
            }
        }
        catch (Exception ex)
        {
            EpicAuthStatusText = "Authentication Error";
            EpicAuthStatusForeground = Brushes.Red;
            FLogger.Append(ELog.Error, () => FLogger.Text($"Epic Games authentication failed: {ex}", Constants.RED, true));
        }
        finally
        {
            ((RelayCommand)AuthenticateEpicGamesCommand).RaiseCanExecuteChanged();
            ((RelayCommand)GetAesKeyCommand).RaiseCanExecuteChanged();
        }
    }

    private bool CanAuthenticateEpicGames(object parameter)
    {
        // Always allow re-authentication attempt
        return true;
    }

    private async void GetAesKey(object parameter)
    {
        RetrievedAesKey = "Retrieving AES Key...";
        var mapCode = MapCodeInput?.Trim() ?? "";
        Log.Information("AESキー取得開始: MapCode={MapCode}", mapCode);
        FLogger.Append(ELog.Information, () => FLogger.Text($"Attempting to get AES key for map code: {MapCodeInput}", Constants.WHITE, true));

        if (UserSettings.Default.LastAuthResponse?.AccessToken is null)
        {
            RetrievedAesKey = "Authentication is required.";
            Log.Warning("認証が必要です: AccessTokenがありません");
            FLogger.Append(ELog.Warning, () => FLogger.Text(RetrievedAesKey, Constants.RED, true));
            return;
        }
        try
        {
            using var httpClient = new HttpClient();

            // Get latest build version
            Log.Information("最新ビルドバージョンを取得中...");
            var mappingsData = await httpClient.GetFromJsonAsync<JsonElement?>("https://fortnitecentral.genxgames.gg/api/v1/mappings");
            string versionStr = mappingsData?.GetProperty("version").GetString() ?? throw new Exception("Failed to get version from mappings data.");
            Log.Information("最新バージョン取得完了: {Version}", versionStr);
            FLogger.Append(ELog.Information, () => FLogger.Text($"Latest version: {versionStr}", Constants.WHITE, true));

            var match = Regex.Match(versionStr, @"Release-(\d+)\.(\d+)-CL-(\d+)");
            if (!match.Success) 
            {
                var error = $"バージョン文字列の解析に失敗: {versionStr}";
                Log.Error(error);
                throw new Exception(error);
            }

            var major = match.Groups[1].Value;
            var minor = match.Groups[2].Value;
            var cl = match.Groups[3].Value;
            Log.Information("バージョン情報解析完了: Major={Major}, Minor={Minor}, CL={CL}", major, minor, cl);

            // Get map content info
            var contentUrl = $"https://content-service.bfda.live.use1a.on.epicgames.com/api/content/v2/link/{MapCodeInput}/cooked-content-package?role=client&platform=windows&major={major}&minor={minor}&patch={cl}";
            Log.Information("マップコンテンツ情報を取得中: {ContentUrl}", contentUrl);
            
            var request = new HttpRequestMessage(HttpMethod.Get, contentUrl);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", UserSettings.Default.LastAuthResponse.AccessToken);
            var contentResponse = await httpClient.SendAsync(request);
            contentResponse.EnsureSuccessStatusCode();
            var contentData = await contentResponse.Content.ReadFromJsonAsync<JsonElement?>();
            Log.Information("マップコンテンツ情報取得完了");

            if (contentData.HasValue && contentData.Value.TryGetProperty("errorCode", out var errorCode) &&
                errorCode.GetString() == "errors.com.epicgames.content-service.unexpected_link_type")
            {
                RetrievedAesKey = "1.0 maps have no encryption.";
                Log.Information("1.0マップは暗号化されていません: {MapCode}", mapCode);
                FLogger.Append(ELog.Warning, () => FLogger.Text(RetrievedAesKey, Constants.YELLOW, true));
                return;
            }

            if (contentData?.GetProperty("isEncrypted").GetBoolean() == true)
            {
                Log.Information("マップが暗号化されています。AESキーを取得中...");
                var moduleId = contentData.Value.GetProperty("resolved").GetProperty("root").GetProperty("moduleId").ToString();
                var version = contentData.Value.GetProperty("resolved").GetProperty("root").GetProperty("version").ToString();
                Log.Information("ModuleID={ModuleId}, Version={Version}", moduleId, version);

                var payload = new[] { new { moduleId, version } };
                var keyReq = new HttpRequestMessage(HttpMethod.Post, "https://content-service.bfda.live.use1a.on.epicgames.com/api/content/v4/module/key/batch")
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
                };
                keyReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", UserSettings.Default.LastAuthResponse.AccessToken);
                var keyResponse = await httpClient.SendAsync(keyReq);
                keyResponse.EnsureSuccessStatusCode();
                var keyData = await keyResponse.Content.ReadFromJsonAsync<JsonElement[]>();
                var key = keyData?[0].GetProperty("key").GetProperty("Key").GetString() ?? throw new Exception("Failed to get key from key data.");
                RetrievedAesKey = "0x" + BitConverter.ToString(Convert.FromBase64String(key)).Replace("-", "");
                Log.Information("AESキー取得完了: {AESKey}", RetrievedAesKey);
                FLogger.Append(ELog.Information, () => FLogger.Text($"AES Key retrieved: {RetrievedAesKey}", Constants.GREEN, true));

                // マニフェストのダウンロードとPAK化処理
                await DownloadAndCreatePak(contentData.Value, MapCodeInput);
            }
            else
            {
                RetrievedAesKey = "マップは暗号化されていません";
                Log.Information("マップは暗号化されていません: {MapCode}", mapCode);
                FLogger.Append(ELog.Information, () => FLogger.Text(RetrievedAesKey, Constants.WHITE, true));

                // 暗号化されていない場合でもPAK化を試みる
                await DownloadAndCreatePak(contentData.Value, MapCodeInput);
            }
        }
        catch (Exception ex)
        {
            RetrievedAesKey = "AES キーの取得中にエラーが発生しました。";
            Log.Error(ex, "AESキーの取得中にエラーが発生しました: MapCode={MapCode}", mapCode);
            FLogger.Append(ELog.Error, () => FLogger.Text($"{RetrievedAesKey} Details: {ex}", Constants.RED, true));
        }
        finally
        {
            ((RelayCommand)CopyAesKeyCommand).RaiseCanExecuteChanged();
            ((RelayCommand)SaveAesKeyCommand).RaiseCanExecuteChanged();
        }
    }

    private static string GetStringOrNumberValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }

    private async Task DownloadAndCreatePak(JsonElement contentData, string mapCode)
    {
        try
        {
            FLogger.Append(ELog.Information, () => FLogger.Text("マニフェストをダウンロードしてPAKファイルを作成しています...", Constants.WHITE, true));
            
            // デバッグ用: contentDataの構造をログ出力
            Log.Information("ContentData構造: {ContentData}", contentData.ToString());

            // rootModuleIdを安全に取得
            string rootModuleId = null;
            if (contentData.TryGetProperty("resolved", out var resolvedProp) && 
                resolvedProp.TryGetProperty("root", out var rootProp) &&
                rootProp.TryGetProperty("moduleId", out var moduleIdProp))
            {
                rootModuleId = moduleIdProp.GetStringOrNumberValue();
            }
            
            // rootModuleIdが取得できない場合、contentから最初のmoduleIdを使用
            if (string.IsNullOrEmpty(rootModuleId) && contentData.TryGetProperty("content", out var contentArrayProp))
            {
                var firstContent = contentArrayProp.EnumerateArray().FirstOrDefault();
                if (!firstContent.Equals(default(JsonElement)) && firstContent.TryGetProperty("moduleId", out var firstModuleId))
                {
                    rootModuleId = firstModuleId.GetStringOrNumberValue();
                    Log.Information("Fallback: 最初のコンテンツからmoduleIdを使用: {ModuleId}", rootModuleId);
                }
            }
            
            if (string.IsNullOrEmpty(rootModuleId))
            {
                FLogger.Append(ELog.Error, () => FLogger.Text("rootModuleIdが取得できませんでした。", Constants.RED, true));
                Log.Error("rootModuleIdが取得できませんでした。");
                return;
            }
            
            Log.Information("使用するrootModuleId: {ModuleId}", rootModuleId);

            var moduleInfo = contentData.GetProperty("content").EnumerateArray()
                .FirstOrDefault(x => x.TryGetProperty("moduleId", out var modId) && 
                                   modId.GetStringOrNumberValue() == rootModuleId);

            if (moduleInfo.Equals(default(JsonElement)))
            {
                FLogger.Append(ELog.Error, () => FLogger.Text("コンテンツデータにルートモジュールが見つかりません。", Constants.RED, true));
                return;
            }

            var binaries = moduleInfo.GetProperty("binaries");
            var baseUrl = binaries.TryGetProperty("baseUrl", out var baseUrlProperty) ? 
                baseUrlProperty.GetStringOrNumberValue() : null;
            var manifestUrl = binaries.TryGetProperty("manifest", out var manifestProperty) ? 
                manifestProperty.GetStringOrNumberValue() : null;

            // チャンクダウンロード用のパラメータを安全に取得
            string cookJobId = null;
            string version = null;
            
            if (contentData.TryGetProperty("resolved", out var resolvedProp2) && 
                resolvedProp2.TryGetProperty("root", out var rootProp2))
            {
                if (rootProp2.TryGetProperty("cookJobId", out var cookJobIdProp))
                    cookJobId = GetStringOrNumberValue(cookJobIdProp);
                if (rootProp2.TryGetProperty("version", out var versionProp))
                    version = GetStringOrNumberValue(versionProp);
            }
            
            // もし resolved からパラメータが取得できない場合、他の場所から探す
            if (string.IsNullOrEmpty(cookJobId) || string.IsNullOrEmpty(version))
            {
                if (moduleInfo.TryGetProperty("cookJobId", out var cookJobIdProp))
                    cookJobId = GetStringOrNumberValue(cookJobIdProp);
                if (moduleInfo.TryGetProperty("version", out var versionProp))
                    version = GetStringOrNumberValue(versionProp);
            }
            
            Log.Information("チャンクダウンロード用パラメータ: ModuleId={ModuleId}, Version={Version}, CookJobId={CookJobId}", 
                rootModuleId, version ?? "unknown", cookJobId ?? "unknown");

            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(manifestUrl))
            {
                // Try to find the correct binaries object which contains the manifest
                var contentArray2 = contentData.GetProperty("content").EnumerateArray();
                foreach (var element in contentArray2)
                {
                    if (element.TryGetProperty("binaries", out var tempBinaries) &&
                        tempBinaries.TryGetProperty("manifest", out var tempManifestProp))
                    {
                        var tempManifestUrl = tempManifestProp.GetStringOrNumberValue();
                        if (!string.IsNullOrEmpty(tempManifestUrl))
                        {
                            baseUrl = tempBinaries.TryGetProperty("baseUrl", out var tempBaseUrlProp) ? 
                                tempBaseUrlProp.GetStringOrNumberValue() : null;
                            manifestUrl = tempManifestUrl;
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(manifestUrl))
                {
                    FLogger.Append(ELog.Error, () => FLogger.Text("マニフェストURLまたはベースURLが見つかりません。", Constants.RED, true));
                    return;
                }
            }

            // キャッシュディレクトリを作成
            var cacheDir = Directory.CreateDirectory(Path.Combine(UserSettings.Default.OutputDirectory, ".data")).FullName;
            
            // 出力ディレクトリを準備
            var outputDir = Path.Combine(UserSettings.Default.OutputDirectory, "MapAES", mapCode);
            Directory.CreateDirectory(outputDir);
            
            // チャンクベースURLを構築（パラメータが取得できない場合はデフォルト値を使用）
            var versionToUse = version ?? "1";
            var cookJobIdToUse = cookJobId ?? "default";
            var chunkBaseUrl = $"https://cooked-content-live-cdn.epicgames.com/valkyrie/cooked-content/{rootModuleId}/39.0.48801071/v{versionToUse}/{cookJobIdToUse}/alt/ChunksV4";
            
            Log.Information("チャンクベースURL: {ChunkBaseUrl} (version: {Version}, cookJobId: {CookJobId})", 
                chunkBaseUrl, versionToUse, cookJobIdToUse);
            
            // チャンクダウンロード用HttpClient（Authorizationヘッダーなし）
            using var httpClient = new HttpClient();
            // Authorizationヘッダーは追加しない（Epic公式仕様）
            
            // Fortnite User-Agentを設定
            var userAgent = "FortniteGame/++Fortnite+Release-39.00-CL-48801071 (http-eventloop) Windows/10.0.26100.1.768.64bit";
            httpClient.DefaultRequestHeaders.UserAgent.Clear();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            httpClient.DefaultRequestHeaders.Accept.Clear();
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("*/*");
            httpClient.DefaultRequestHeaders.AcceptEncoding.Clear();
            httpClient.DefaultRequestHeaders.AcceptEncoding.ParseAdd("deflate, gzip");
            
            Log.Information("カスタムHttpClient設定完了: UserAgent={UserAgent}", userAgent);
            
            // ManifestParseOptionsを設定（CUE4ParseViewModel.csの実装を参考）
            var manifestOptions = new ManifestParseOptions
            {
                ChunkCacheDirectory = cacheDir,
                ManifestCacheDirectory = cacheDir,
                ChunkBaseUrl = chunkBaseUrl,
                Decompressor = ManifestZlibngDotNetDecompressor.Decompress,
                DecompressorState = ZlibHelper.Instance,
                CacheChunksAsIs = false
            };
            
            // 認証付きでマニフェストをダウンロード

            var fullManifestUrl = baseUrl.TrimEnd('/') + "/" + manifestUrl.TrimStart('/');
            FLogger.Append(ELog.Information, () => FLogger.Text($"マニフェストURL: {fullManifestUrl}", Constants.WHITE, true));
            FLogger.Append(ELog.Information, () => FLogger.Text($"チャンクベースURL: {chunkBaseUrl}", Constants.WHITE, true));

            try
            {
                FLogger.Append(ELog.Information, () => FLogger.Text("マニフェストをダウンロード中...", Constants.WHITE, true));
                Log.Information("マニフェストをダウンロード中: {ManifestUrl}", fullManifestUrl);

                // 直接FBuildPatchAppManifestを使用してマニフェストを解析
                FBuildPatchAppManifest manifest;
                try
                {
                    var startTs = Stopwatch.GetTimestamp();
                    
                    // 上で作成した認証付きHttpClientでマニフェストをダウンロード
                    var manifestBytes = await httpClient.GetByteArrayAsync(fullManifestUrl);
                    Log.Information("マニフェストダウンロード完了。サイズ: {Size} bytes", manifestBytes.Length);
                    
                    // FBuildPatchAppManifestを直接デシリアライズ
                    try
                    {                        
                        // チャンクベースURLを設定するオプション
                        var parseOptions = new ManifestParseOptions
                        {
                            ChunkCacheDirectory = cacheDir,
                            ChunkBaseUrl = chunkBaseUrl,
                            Decompressor = ManifestZlibngDotNetDecompressor.Decompress,
                            DecompressorState = ZlibHelper.Instance,
                            CacheChunksAsIs = false
                        };
                        
                        manifest = FBuildPatchAppManifest.Deserialize(manifestBytes, parseOptions);
                        Log.Information("マニフェスト解析成功: {FileCount} ファイル", manifest.Files.Count());
                    }
                    catch (Exception deserializeEx)
                    {
                        Log.Error(deserializeEx, "FBuildPatchAppManifest.Deserializeに失敗しました。ファイルとして保存して再試行します。");
                        
                        // マニフェストファイルを一時保存
                        var tempManifestPath = Path.Combine(cacheDir, "yT2K18gbDOV9bQ-0EUiiPzEzKaxMxQ.manifest");
                        await File.WriteAllBytesAsync(tempManifestPath, manifestBytes);
                        Log.Information("マニフェストファイルを保存しました: {Path}", tempManifestPath);
                        
                        // ファイルから読み込みを試行
                        
                        var fallbackOptions = new ManifestParseOptions
                        {
                            ChunkCacheDirectory = cacheDir,
                            ChunkBaseUrl = chunkBaseUrl,
                            Decompressor = ManifestZlibngDotNetDecompressor.Decompress,
                            DecompressorState = ZlibHelper.Instance,
                            CacheChunksAsIs = false
                        };
                        
                        manifest = FBuildPatchAppManifest.Deserialize(await File.ReadAllBytesAsync(tempManifestPath), fallbackOptions);
                        Log.Information("ファイルからのマニフェスト読み込み成功");
                    }
                    var elapsedTime = Stopwatch.GetElapsedTime(startTs);
                    Log.Information("マニフェスト解析完了: {FileCount} ファイル ({ElapsedMs:F1}ms)", 
                        manifest.Files.Count(), elapsedTime.TotalMilliseconds);
                    FLogger.Append(ELog.Information, () => FLogger.Text($"マニフェスト解析完了: {manifest.Files.Count()} ファイル ({elapsedTime.TotalMilliseconds:F1}ms)", Constants.GREEN, true));
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "マニフェストの解析に失敗しました");
                    // フォールバック: 直接HTTPでダウンロードして手動解析を試行
                    FLogger.Append(ELog.Warning, () => FLogger.Text("フォールバック: 直接HTTPダウンロードを試行します...", Constants.YELLOW, true));
                    using var fallbackHttpClient = new HttpClient();
                    if (UserSettings.Default.LastAuthResponse?.AccessToken != null)
                    {
                        fallbackHttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserSettings.Default.LastAuthResponse.AccessToken);
                    }
                    var manifestBytes = await fallbackHttpClient.GetByteArrayAsync(fullManifestUrl);
                    Log.Information("マニフェストダウンロード完了: {Size} bytes", manifestBytes.Length);
                    FLogger.Append(ELog.Information, () => FLogger.Text($"マニフェストダウンロード完了: {manifestBytes.Length} bytes", Constants.WHITE, true));
                    // マニフェストを一時ファイルとして保存
                    var tempManifestPath = Path.Combine(cacheDir, "yT2K18gbDOV9bQ-0EUiiPzEzKaxMxQ.manifest");
                    await File.WriteAllBytesAsync(tempManifestPath, manifestBytes);
                    // マニフェストファイルを出力ディレクトリにもコピー
                    var outputManifestPath = Path.Combine(outputDir, "yT2K18gbDOV9bQ-0EUiiPzEzKaxMxQ.manifest");
                    File.Copy(tempManifestPath, outputManifestPath, true);
                    FLogger.Append(ELog.Information, () => FLogger.Text($"マニフェストファイルが保存されました: {outputManifestPath}", Constants.GREEN, true));
                    FLogger.Append(ELog.Warning, () => FLogger.Text("注意: マニフェスト解析に失敗したため、チャンクファイルの抽出はできません。", Constants.YELLOW, true));
                    return;
                }
                // ファイルを出力ディレクトリに抽出
                FLogger.Append(ELog.Information, () => FLogger.Text($"ファイルを抽出中: {outputDir}", Constants.WHITE, true));
                await ExtractFilesFromManifest(manifest, outputDir, mapCode, chunkBaseUrl);
                FLogger.Append(ELog.Information, () => FLogger.Text($"マニフェスト処理完了: {outputDir}", Constants.GREEN, true));
            }
            catch (Exception ex)
            {
                var errorMsg = $"マニフェストの処理に失敗しました: {ex.Message}";
                Log.Error(ex, errorMsg);
                FLogger.Append(ELog.Error, () => FLogger.Text($"{errorMsg}\n{ex.StackTrace}", Constants.RED, true));
                return;
            }
        }
        catch (Exception ex)
        {
            FLogger.Append(ELog.Error, () => FLogger.Text($"PAKファイルの作成に失敗しました: {ex.Message}\n{ex.StackTrace}", Constants.RED, true));
        }
    }

    private async Task ExtractFilesFromManifest(FBuildPatchAppManifest manifest, string outputDir, string mapCode, string chunkBaseUrl)
    {
        try
        {
            Log.Information("マニフェストからファイルを抽出中...");
            FLogger.Append(ELog.Information, () => FLogger.Text("マニフェストからファイルを抽出中...", Constants.WHITE, true));
            var processedFiles = 0;
            var totalFiles = manifest.Files.Count();
            var startTime = DateTime.Now;
            FLogger.Append(ELog.Information, () => FLogger.Text($"総ファイル数: {totalFiles}", Constants.WHITE, true));
            Log.Information("チャンクベースURLが設定されたマニフェストを使用: {ChunkBaseUrl}", chunkBaseUrl);
            
            // グローバルHttpClient設定を試行
            SetGlobalHttpClientDefaults();
            
            // カスタムチャンクダウンロードを試行
            var useCustomDownload = true; // テスト用フラグ
            
            if (useCustomDownload)
            {
                Log.Information("カスタムチャンクダウンロードを使用します");
                await ExtractFilesUsingCustomDownload(manifest, outputDir, chunkBaseUrl);
            }
            else
            {
                    foreach (var fileManifest in manifest.Files)
                {
                    try
                    {
                        var fileName = fileManifest.FileName;
                        var outputPath = Path.Combine(outputDir, fileName.Replace('/', Path.DirectorySeparatorChar));
                        // ディレクトリを作成
                        var fileDir = Path.GetDirectoryName(outputPath);
                        if (!string.IsNullOrEmpty(fileDir))
                        {
                            Directory.CreateDirectory(fileDir);
                        }
                        Log.Information("ファイル抽出開始: {FileName}", fileName);
                        
                        // 元のGetStreamメソッドを使用 (Epic Manifest Parserに認証を委任)
                        using var fileStream = fileManifest.GetStream();
                        using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                        await fileStream.CopyToAsync(outputStream);
                        
                        Log.Information("ファイル抽出完了: {FileName} ({Size} bytes)", fileName, new FileInfo(outputPath).Length);
                        processedFiles++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning("ファイルの抽出に失敗: {FileName} - {Error}", fileManifest.FileName, ex.Message);
                        FLogger.Append(ELog.Warning, () => FLogger.Text($"ファイルの処理に失敗: {fileManifest.FileName} - {ex.Message}", Constants.YELLOW, true));
                    }
                }
            }
            var totalElapsed = DateTime.Now - startTime;
            var successMsg = $"抽出完了: {processedFiles}/{totalFiles} ファイルが {outputDir} に保存されました (所要時間: {totalElapsed:mm\\:ss})";
            Log.Information(successMsg);
            FLogger.Append(ELog.Information, () => FLogger.Text(successMsg, Constants.GREEN, true));
        }
        catch (Exception ex)
        {
            var errorMsg = $"ファイル抽出エラー: {ex.Message}";
            Log.Error(ex, errorMsg);
            FLogger.Append(ELog.Error, () => FLogger.Text(errorMsg, Constants.RED, true));
        }
    }

    private async Task ExtractFilesUsingCustomDownload(FBuildPatchAppManifest manifest, string outputDir, string chunkBaseUrl)
    {
        try
        {
            Log.Information("カスタムチャンクダウンロードでファイルを抽出中...");
            
            // 認証付きHttpClientを作成
            using var httpClient = new HttpClient();
            var accessToken = UserSettings.Default.LastAuthResponse?.AccessToken;
            
            // 必要なヘッダーを設定
            if (!string.IsNullOrEmpty(accessToken))
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                Log.Information("認証ヘッダーを設定: Bearer {Token}", accessToken.Substring(0, Math.Min(10, accessToken.Length)) + "...");
            }
            
            // Fortnite User-Agentを設定
            var userAgent = "FortniteGame/++Fortnite+Release-39.00-CL-48801071 (http-eventloop) Windows/10.0.26100.1.768.64bit";
            httpClient.DefaultRequestHeaders.UserAgent.Clear();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            httpClient.DefaultRequestHeaders.Accept.Clear();
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("*/*");
            httpClient.DefaultRequestHeaders.AcceptEncoding.Clear();
            httpClient.DefaultRequestHeaders.AcceptEncoding.ParseAdd("deflate, gzip");
            
            Log.Information("カスタムHttpClient設定完了: UserAgent={UserAgent}", userAgent);
            
            // Epic Manifest Parserの内部HttpClientを徹底的に置き換え
            ReplaceAllEpicManifestParserHttpClients(httpClient, manifest);
            
            var processedFiles = 0;
            var totalFiles = manifest.Files.Count();
            
            foreach (var fileManifest in manifest.Files)
            {
                try
                {
                    var fileName = fileManifest.FileName;
                    var outputPath = Path.Combine(outputDir, fileName.Replace('/', Path.DirectorySeparatorChar));
                    
                    // ディレクトリを作成
                    var fileDir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(fileDir))
                    {
                        Directory.CreateDirectory(fileDir);
                    }
                    
                    Log.Information("カスタムダウンロード開始: {FileName}", fileName);
                    
                    // まずGetStreamメソッドを試す
                    try
                    {
                        Log.Information("Epic Manifest ParserのGetStreamメソッドでファイルを抽出します: {FileName}", fileName);
                        FLogger.Append(ELog.Information, () => FLogger.Text($"GetStreamメソッドでファイルを抽出: {fileName}", Constants.WHITE, true));
                        
                        // GetStream実行直前に追加のHttpClient置換を実行
                        ReplaceAllEpicManifestParserHttpClients(httpClient, manifest);
                        
                        using var fileStream = fileManifest.GetStream();
                        using var outputFileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                        await fileStream.CopyToAsync(outputFileStream);
                        
                        var fileInfo = new FileInfo(outputPath);
                        Log.Information("GetStreamでファイル抽出成功: {FileName} ({Size} bytes)", fileName, fileInfo.Length);
                        FLogger.Append(ELog.Information, () => FLogger.Text($"GetStream成功: {fileName} ({fileInfo.Length} bytes)", Constants.GREEN, true));
                        
                        processedFiles++;
                        
                        if (processedFiles % 1 == 0) // 全ファイルの進行状況を表示
                        {
                            FLogger.Append(ELog.Information, () => FLogger.Text(
                                $"進行状況: {processedFiles}/{totalFiles} ファイル ({processedFiles * 100.0 / totalFiles:F1}%)", 
                                Constants.WHITE, true));
                        }
                        continue;
                    }
                    catch (Exception getStreamEx)
                    {
                        Log.Error(getStreamEx, "GetStreamでのファイル抽出に失敗、カスタムチャンクダウンロードを試します: {FileName}", fileName);
                        FLogger.Append(ELog.Error, () => FLogger.Text($"GetStream失敗: {fileName} - {getStreamEx.Message}", Constants.RED, true));
                    }
                    
                    // チャンク情報を取得
                    var chunkParts = GetChunkParts(fileManifest);
                    var fileSize = GetFileSize(fileManifest);
                    
                    Log.Information("ファイル情報: {FileName}, Size={Size}, Chunks={ChunkCount}", fileName, fileSize, chunkParts?.Count() ?? 0);
                    
                    if (chunkParts == null || !chunkParts.Any())
                    {
                        Log.Warning("チャンク情報がありません: {FileName}", fileName);
                        
                        // フォールバック: 元のGetStreamメソッドを試す
                        try
                        {
                            using var fileStream = fileManifest.GetStream();
                            using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                            await fileStream.CopyToAsync(outputStream);
                            Log.Information("フォールバックでファイル抽出成功: {FileName}", fileName);
                        }
                        catch (Exception fallbackEx)
                        {
                            Log.Error(fallbackEx, "フォールバック抽出に失敗: {FileName}", fileName);
                        }
                        continue;
                    }
                    
                    // カスタムチャンクダウンロード
                    await DownloadFileFromChunksCustom(httpClient, fileManifest, manifest, chunkBaseUrl, outputPath, chunkParts, fileSize);
                    
                    processedFiles++;
                    
                    if (processedFiles % 1 == 0) // 全ファイルの進行状況を表示
                    {
                        FLogger.Append(ELog.Information, () => FLogger.Text(
                            $"進行状況: {processedFiles}/{totalFiles} ファイル ({processedFiles * 100.0 / totalFiles:F1}%)", 
                            Constants.WHITE, true));
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("カスタムファイル抽出に失敗: {FileName} - {Error}", fileManifest.FileName, ex.Message);
                    FLogger.Append(ELog.Warning, () => FLogger.Text($"カスタムファイル処理に失敗: {fileManifest.FileName} - {ex.Message}", Constants.YELLOW, true));
                }
            }
            
            var successMsg = $"カスタム抽出完了: {processedFiles}/{totalFiles} ファイルが {outputDir} に保存されました";
            Log.Information(successMsg);
            FLogger.Append(ELog.Information, () => FLogger.Text(successMsg, Constants.GREEN, true));
        }
        catch (Exception ex)
        {
            var errorMsg = $"カスタムファイル抽出エラー: {ex.Message}";
            Log.Error(ex, errorMsg);
            FLogger.Append(ELog.Error, () => FLogger.Text(errorMsg, Constants.RED, true));
        }
    }
    
    private IEnumerable<object> GetChunkParts(dynamic fileManifest)
    {
        try
        {
            var chunkParts = fileManifest.ChunkParts;
            if (chunkParts is IEnumerable<object> enumerable)
            {
                return enumerable;
            }
            else if (chunkParts is System.Array array)
            {
                return array.Cast<object>();
            }
            else if (chunkParts != null)
            {
                // 単一オブジェクトの場合、配列に変換
                return new[] { chunkParts };
            }
            return Enumerable.Empty<object>();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "チャンクパーツの取得に失敗");
            return Enumerable.Empty<object>();
        }
    }
    
    private long GetFileSize(dynamic fileManifest)
    {
        try
        {
            return fileManifest.FileSize;
        }
        catch
        {
            return 0;
        }
    }
    
    private async Task DownloadFileFromChunksCustom(HttpClient httpClient, dynamic fileManifest, FBuildPatchAppManifest manifest, string chunkBaseUrl, string outputPath, IEnumerable<object> chunkParts, long fileSize)
    {
        try
        {
            using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            var fileName = fileManifest.FileName;
            
            Log.Information("チャンクベースダウンロード開始: {FileName}, {Size} bytes", fileName, fileSize);
            
            var downloadedSize = 0L;
            var chunkIndex = 0;
            var totalChunks = chunkParts?.Count() ?? 0;
            
            Log.Information("ファイル {FileName} のチャンク処理開始: {ChunkCount} チャンク", fileName, totalChunks);
            
            if (chunkParts == null || !chunkParts.Any())
            {
                Log.Warning("チャンク情報がありません: {FileName}", fileName);
                return;
            }
            
            foreach (var chunkPart in chunkParts)
            {
                try
                {
                    chunkIndex++;
                    Log.Information("チャンク {Index} 処理中...", chunkIndex);
                    
                    // チャンクGUIDを取得
                    var chunkGuid = GetChunkGuid(chunkPart);
                    if (chunkGuid == null)
                    {
                        Log.Warning("チャンクGUIDが取得できません: チャンク {Index}", chunkIndex);
                        continue;
                    }
                    // マニフェストからチャンク情報を検索
                    var chunkInfo = FindChunkInfo(manifest, chunkGuid);
                    if (chunkInfo == null)
                    {
                        Log.Warning("チャンク情報が見つかりません: {ChunkGuid}", chunkGuid);
                        continue;
                    }
                    
                    // チャンクURLを構築
                    var chunkUrl = BuildChunkUrl(chunkBaseUrl, chunkInfo, chunkGuid);
                    Log.Information("チャンクダウンロード: {ChunkUrl}", chunkUrl);
                    
                    // チャンクをダウンロード
                    if (string.IsNullOrEmpty(chunkUrl))
                    {
                        Log.Error("チャンクURLが空です: チャンク {Index}", chunkIndex);
                        continue;
                    }
                    
                    var chunkData = await httpClient.GetByteArrayAsync(chunkUrl as string);
                    Log.Information("チャンクダウンロード成功: {Size} bytes", chunkData.Length);
                    
                    // チャンクを解凍 (必要に応じて)
                    var decompressedData = TryDecompressChunk(chunkData);
                    
                    // ファイルに書き込みのパラメータを取得
                    var offset = GetChunkOffset(chunkPart);
                    var size = GetChunkSize(chunkPart);
                    
                    Log.Information("ファイル書き込みパラメータ: Offset={Offset}, Size={Size}, DecompressedSize={DecompressedSize}", 
                        offset, size, decompressedData.Length);
                    
                    // ファイルサイズを確保
                    outputStream.SetLength(Math.Max(outputStream.Length, offset + size));
                    outputStream.Seek(offset, SeekOrigin.Begin);
                    
                    // チャンクサイズが適切か確認
                    var dataToWrite = Math.Min(size, decompressedData.Length);
                    
                    if (dataToWrite > 0)
                    {
                        outputStream.Write(decompressedData, 0, (int)dataToWrite);
                        downloadedSize += dataToWrite;
                        
                        Log.Information("チャンク書き込み成功: {DataWritten} bytes 書き込み, 累計: {Downloaded} bytes", 
                            dataToWrite, downloadedSize);
                    }
                    else
                    {
                        Log.Warning("書き込みデータがありません: Size={Size}, DecompressedSize={DecompressedSize}", 
                            size, decompressedData.Length);
                    }
                    
                    Log.Information("チャンク書き込み成功: {Index}/{Total}, {Progress:F1}%", 
                        chunkIndex, totalChunks, downloadedSize * 100.0 / fileSize);
                }
                catch (HttpRequestException httpEx)
                {
                    Log.Error(httpEx, "チャンクダウンロードエラー: チャンク {Index}", chunkIndex);
                    throw; // 403エラーの場合は停止
                }
                catch (Exception chunkEx)
                {
                    Log.Warning(chunkEx, "チャンク処理エラー: チャンク {Index}", chunkIndex);
                    // 他のチャンクに続行
                }
            }
            
            // ストリームをフラッシュしてデータを確実に書き込み
            outputStream.Flush();
            
            Log.Information("ファイル再構築完了: {FileName} (総サイズ: {TotalSize} bytes, 書き込み: {Downloaded} bytes)", fileName, fileSize, downloadedSize);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "カスタムファイルチャンクダウンロードエラー: {FileName}", fileManifest.FileName);
            throw;
        }
    }
    
    private string GetChunkGuid(object chunkPart)
    {
        try
        {
            if (chunkPart == null) return null;
            
            // リフレクションで利用可能なプロパティを調べる
            var type = chunkPart.GetType();
            
            // 一般的なGUIDプロパティ名を試す
            string[] possibleGuidProperties = { "ChunkGuid", "Guid", "Id", "ChunkId" };
            
            foreach (var propName in possibleGuidProperties)
            {
                var prop = type.GetProperty(propName);
                if (prop != null && prop.CanRead)
                {
                    var value = prop.GetValue(chunkPart);
                    if (value != null)
                    {
                        Log.Debug("チャンクGUIDプロパティが見つかりました: {PropName} = {Value}", propName, value);
                        return value.ToString();
                    }
                }
            }
            
            // 全プロパティをログ出力してデバッグする
            Log.Information("FChunkPartの全プロパティ: {Properties}", 
                string.Join(", ", type.GetProperties().Select(p => $"{p.Name}({p.PropertyType.Name})")));
            
            Log.Warning("チャンクGUIDプロパティが見つかりません: {Type}", type.FullName);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "チャンクGUID取得エラー");
            return null;
        }
    }
    
    private object FindChunkInfo(FBuildPatchAppManifest manifest, string chunkGuid)
    {
        try
        {
            return manifest.ChunkList?.FirstOrDefault(c => c.Guid.ToString() == chunkGuid);
        }
        catch
        {
            return null;
        }
    }
    
    private string BuildChunkUrl(string chunkBaseUrl, object chunkInfo, string chunkGuid)
    {
        try
        {
            if (chunkInfo == null)
            {
                Log.Error("チャンク情報がnullです");
                return null;
            }
            
            // リフレクションで利用可能なプロパティを調べる
            var type = chunkInfo.GetType();
            var properties = type.GetProperties();
            var propertyNames = string.Join(", ", properties.Select(p => $"{p.Name}({p.PropertyType.Name})"));
            Log.Information("FChunkInfoの全プロパティ: {Properties}", propertyNames);
            
            // 一般的なハッシュプロパティ名を試す
            string[] possibleHashProperties = { "ChunkHash", "Hash", "DataHash", "Guid", "Id" };
            string[] possibleShaProperties = { "ChunkShaHash", "ShaHash", "Sha1Hash", "CheckSum", "Checksum" };
            
            object hashValue = null;
            object shaValue = null;
            
            foreach (var propName in possibleHashProperties)
            {
                var prop = type.GetProperty(propName);
                if (prop != null && prop.CanRead)
                {
                    hashValue = prop.GetValue(chunkInfo);
                    if (hashValue != null)
                    {
                        Log.Debug("ハッシュプロパティが見つかりました: {PropName} = {Value}", propName, hashValue);
                        break;
                    }
                }
            }
            
            foreach (var propName in possibleShaProperties)
            {
                var prop = type.GetProperty(propName);
                if (prop != null && prop.CanRead)
                {
                    shaValue = prop.GetValue(chunkInfo);
                    if (shaValue != null)
                    {
                        Log.Debug("SHAプロパティが見つかりました: {PropName} = {Value}", propName, shaValue);
                        break;
                    }
                }
            }
            
            // GUIDをファイル名として使用するフォールバック
            if (hashValue == null && shaValue == null)
            {
                Log.Warning("ハッシュプロパティが見つからないため、GUIDを使用します: {ChunkGuid}", chunkGuid);
                var chunkFileName = $"{chunkGuid}.chunk";
                var subDir = chunkGuid != null && chunkGuid.Length >= 2 
                    ? chunkGuid.Substring(0, 2).ToUpper()
                    : "00";
                var fullUrl = $"{chunkBaseUrl}/{subDir}/{chunkFileName}";
                Log.Information("チャンクURL構築(フォールバック): {Url}", fullUrl);
                return fullUrl;
            }
            
            // ハッシュとSHAが取得できた場合のファイル名構築
            var chunkHash = hashValue ?? chunkGuid;
            var chunkShaHash = shaValue ?? chunkGuid;
            var chunkFileName2 = $"{chunkHash}_{chunkShaHash}.chunk";
            
            // GUIDの最初の2文字を取得してサブディレクトリを作成
            var subDir2 = chunkGuid != null && chunkGuid.Length >= 2 
                ? chunkGuid.Substring(0, 2).ToUpper()
                : "00";
            
            var fullUrl2 = $"{chunkBaseUrl}/{subDir2}/{chunkFileName2}";
            Log.Information("チャンクURL構築: {Url}", fullUrl2);
            
            return fullUrl2;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "チャンクURL構築エラー: ChunkGuid={ChunkGuid}", chunkGuid);
            return null;
        }
    }
    
    private byte[] TryDecompressChunk(byte[] chunkData)
    {
        try
        {
            Log.Information("チャンクデータ処理開始: {Size} bytes", chunkData.Length);
            
            if (chunkData == null || chunkData.Length == 0)
            {
                Log.Warning("空のチャンクデータ");
                return new byte[0];
            }
            
            // チャンクデータをそのまま使用
            // Epic Manifest Parserが必要に応じて解凍を行う
            Log.Information("チャンクデータをそのまま使用: {Size} bytes", chunkData.Length);
            return chunkData;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "チャンクデータ処理エラー");
            return chunkData ?? new byte[0];
        }
    }
    
    private long GetChunkOffset(dynamic chunkPart)
    {
        try
        {
            // 複数のプロパティ名を試す
            if (chunkPart.Offset != null)
                return Convert.ToInt64(chunkPart.Offset);
            if (chunkPart.FileOffset != null)
                return Convert.ToInt64(chunkPart.FileOffset);
            
            var chunkPartDict = chunkPart as IDictionary<string, object>;
            if (chunkPartDict != null)
            {
                if (chunkPartDict.ContainsKey("Offset"))
                    return Convert.ToInt64(chunkPartDict["Offset"]);
                if (chunkPartDict.ContainsKey("FileOffset"))
                    return Convert.ToInt64(chunkPartDict["FileOffset"]);
            }
            
            Log.Debug("ChunkOffsetが見つかりません、0を使用");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ChunkOffset取得エラー");
            return 0;
        }
    }
    
    private long GetChunkSize(dynamic chunkPart)
    {
        try
        {
            // 複数のプロパティ名を試す
            if (chunkPart.Size != null)
                return Convert.ToInt64(chunkPart.Size);
            if (chunkPart.ChunkSize != null)
                return Convert.ToInt64(chunkPart.ChunkSize);
            
            var chunkPartDict = chunkPart as IDictionary<string, object>;
            if (chunkPartDict != null)
            {
                if (chunkPartDict.ContainsKey("Size"))
                    return Convert.ToInt64(chunkPartDict["Size"]);
                if (chunkPartDict.ContainsKey("ChunkSize"))
                    return Convert.ToInt64(chunkPartDict["ChunkSize"]);
            }
            
            Log.Debug("ChunkSizeが見つかりません、0を使用");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ChunkSize取得エラー");
            return 0;
        }
    }
    
    private long GetChunkDataOffset(dynamic chunkPart)
    {
        try
        {
            return chunkPart.ChunkOffset;
        }
        catch
        {
            return 0;
        }
    }

    private void SetGlobalHttpClientDefaults()
    {
        try
        {
            // .NET HttpClientのグローバルデフォルトを設定
            System.Net.ServicePointManager.DefaultConnectionLimit = 100;
            
            var userAgent = "FortniteGame/++Fortnite+Release-39.00-CL-48801071 (http-eventloop) Windows/10.0.26100.1.768.64bit";
            var accessToken = UserSettings.Default.LastAuthResponse?.AccessToken;
            
            Log.Information("グローバルHttpClient設定を試行: User-Agent={UserAgent}, HasToken={HasToken}", userAgent, !string.IsNullOrEmpty(accessToken));
            
            // EpicManifestParserの内部HttpClientをリフレクションで設定する試み
            SetEpicManifestParserHttpClient(userAgent, accessToken);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "グローバルHttpClient設定に失敗");
        }
    }

    private void SetEpicManifestParserHttpClientViaReflection(HttpClient httpClient)
    {
        try
        {
            // Epic Manifest Parserのアセンブリを取得
            var epicAssembly = System.Reflection.Assembly.GetAssembly(typeof(FBuildPatchAppManifest));
            if (epicAssembly == null)
            {
                Log.Warning("EpicManifestParserアセンブリが見つかりません");
                return;
            }
            
            Log.Information("Epic Manifest ParserアセンブリでHttpClientを設定中...");
            
            // 全型を取得してHttpClientフィールドを探す
            var types = epicAssembly.GetTypes();
            var httpClientSet = false;
            
            foreach (var type in types)
            {
                try
                {
                    // 静的フィールドをチェック
                    var fields = type.GetFields(System.Reflection.BindingFlags.Static | 
                                               System.Reflection.BindingFlags.NonPublic | 
                                               System.Reflection.BindingFlags.Public);
                    
                    foreach (var field in fields)
                    {
                        if (field.FieldType == typeof(HttpClient))
                        {
                            Log.Information("HttpClientフィールドを発見: {Type}.{Field}", type.Name, field.Name);
                            field.SetValue(null, httpClient);
                            httpClientSet = true;
                        }
                    }
                    
                    // 静的プロパティをチェック
                    var properties = type.GetProperties(System.Reflection.BindingFlags.Static | 
                                                       System.Reflection.BindingFlags.NonPublic | 
                                                       System.Reflection.BindingFlags.Public);
                    
                    foreach (var property in properties)
                    {
                        if (property.PropertyType == typeof(HttpClient) && property.CanWrite)
                        {
                            Log.Information("HttpClientプロパティを発見: {Type}.{Property}", type.Name, property.Name);
                            property.SetValue(null, httpClient);
                            httpClientSet = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "タイプ {Type} の処理に失敗", type.Name);
                }
            }
            
            if (httpClientSet)
            {
                Log.Information("Epic Manifest ParserのHttpClient設定が完了しました");
            }
            else
            {
                Log.Warning("Epic Manifest ParserのHttpClientフィールドが見つかりませんでした");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Epic Manifest Parser HttpClient設定エラー");
        }
    }

    private void SetEpicManifestParserHttpClient(string userAgent, string accessToken)
    {
        try
        {
            // リフレクションでEpicManifestParserの内部HttpClientを探す
            var epicAssembly = System.Reflection.Assembly.GetAssembly(typeof(FBuildPatchAppManifest));
            if (epicAssembly == null)
            {
                Log.Warning("EpicManifestParserアセンブリが見つかりません");
                return;
            }
            
            // HttpClientを使用している可能性のあるクラスを探す
            var types = epicAssembly.GetTypes();
            foreach (var type in types)
            {
                try
                {
                    var fields = type.GetFields(System.Reflection.BindingFlags.Static | 
                                               System.Reflection.BindingFlags.NonPublic | 
                                               System.Reflection.BindingFlags.Public);
                    
                    foreach (var field in fields)
                    {
                        if (field.FieldType == typeof(HttpClient))
                        {
                            var httpClient = (HttpClient)field.GetValue(null);
                            if (httpClient != null)
                            {
                                Log.Information("静的HttpClientフィールドを発見: {Type}.{Field}", type.Name, field.Name);
                                ConfigureHttpClient(httpClient, userAgent, accessToken);
                            }
                        }
                    }
                    
                    var properties = type.GetProperties(System.Reflection.BindingFlags.Static | 
                                                       System.Reflection.BindingFlags.NonPublic | 
                                                       System.Reflection.BindingFlags.Public);
                    
                    foreach (var property in properties)
                    {
                        if (property.PropertyType == typeof(HttpClient) && property.CanRead)
                        {
                            var httpClient = (HttpClient)property.GetValue(null);
                            if (httpClient != null)
                            {
                                Log.Information("静的HttpClientプロパティを発見: {Type}.{Property}", type.Name, property.Name);
                                ConfigureHttpClient(httpClient, userAgent, accessToken);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // アクセスできないフィールド/プロパティはスキップ
                    Log.Debug(ex, "タイプ {Type} のアクセスに失敗", type.Name);
                }
            }
            
            // グローバルHttpClient.DefaultRequestHeadersを設定する方法も試す
            TrySetGlobalHttpDefaults(userAgent, accessToken);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "EpicManifestParser HttpClient設定に失敗");
        }
    }
    
    private void ConfigureHttpClient(HttpClient httpClient, string userAgent, string accessToken)
    {
        try
        {
            // User-Agentを設定
            httpClient.DefaultRequestHeaders.UserAgent.Clear();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            
            // 認証ヘッダーを設定
            if (!string.IsNullOrEmpty(accessToken))
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            }
            
            // Acceptヘッダーを設定
            httpClient.DefaultRequestHeaders.Accept.Clear();
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("*/*");
            
            Log.Information("HttpClientを設定しました: UserAgent={UserAgent}, HasAuth={HasAuth}", 
                userAgent, !string.IsNullOrEmpty(accessToken));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HttpClientの設定に失敗");
        }
    }
    
    private void TrySetGlobalHttpDefaults(string userAgent, string accessToken)
    {
        try
        {
            // システムレベルのHTTP設定を試す
            System.Net.ServicePointManager.Expect100Continue = false;
            System.Net.ServicePointManager.UseNagleAlgorithm = false;
            
            Log.Information("グローバルHTTP設定を適用しました");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "グローバルHTTP設定に失敗");
        }
    }
    
    private void ReplaceAllEpicManifestParserHttpClients(HttpClient authenticatedClient, FBuildPatchAppManifest manifest)
    {
        try
        {
            Log.Information("Epic Manifest ParserのすべてのHttpClientインスタンスを認証付きクライアントに置き換え中...");
            FLogger.Append(ELog.Information, () => FLogger.Text("Epic Manifest ParserのすべてのHttpClientインスタンスを認証付きクライアントに置き換え中...", Constants.WHITE, true));
            
            // manifestオブジェクトのインスタンスフィールドも置き換え
            ReplaceHttpClientsInObject(manifest, authenticatedClient);
            
            // EpicManifestParserアセンブリの静的フィールド・プロパティを置き換え
            var assembly = Assembly.GetAssembly(typeof(FBuildPatchAppManifest));
            if (assembly != null)
            {
                Log.Information("EpicManifestParserアセンブリが見つかりました: {AssemblyName}", assembly.FullName);
                FLogger.Append(ELog.Information, () => FLogger.Text($"EpicManifestParserアセンブリ: {assembly.FullName}", Constants.GREEN, true));
                
                int processedTypes = 0;
                foreach (var type in assembly.GetTypes())
                {
                    try
                    {
                        ReplaceStaticHttpClientsInType(type, authenticatedClient);
                        processedTypes++;
                    }
                    catch (Exception typeEx)
                    {
                        Log.Debug(typeEx, "型 {TypeName} のHttpClient置き換えをスキップ", type.Name);
                    }
                }
                Log.Information("処理された型の数: {ProcessedTypes}", processedTypes);
                FLogger.Append(ELog.Information, () => FLogger.Text($"処理された型の数: {processedTypes}", Constants.GREEN, true));
            }
            else
            {
                Log.Warning("EpicManifestParserアセンブリが見つかりませんでした");
                FLogger.Append(ELog.Warning, () => FLogger.Text("EpicManifestParserアセンブリが見つかりませんでした", Constants.YELLOW, true));
            }
            
            Log.Information("Epic Manifest ParserのHttpClient置き換えが完了しました");
            FLogger.Append(ELog.Information, () => FLogger.Text("Epic Manifest ParserのHttpClient置き換えが完了しました", Constants.GREEN, true));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Epic Manifest Parser HttpClient置換中にエラーが発生しました");
            FLogger.Append(ELog.Error, () => FLogger.Text($"Epic Manifest Parser HttpClient置換エラー: {ex}", Constants.RED, true));
        }
    }
    
    private void ReplaceHttpClientsInObject(object obj, HttpClient authenticatedClient)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        ReplaceHttpClientsInObjectInternal(obj, authenticatedClient, visited);
    }
    
    private void ReplaceHttpClientsInObjectInternal(object obj, HttpClient authenticatedClient, HashSet<object> visited)
    {
        if (obj == null) return;
        
        // 循環参照を防ぐため、既に訪問したオブジェクトはスキップ
        if (visited.Contains(obj))
        {
            return;
        }
        
        // プリミティブ型や基本的な型はスキップ
        var type = obj.GetType();
        if (type.IsPrimitive || type == typeof(string) || type == typeof(DateTime) || 
            type == typeof(Guid) || type == typeof(TimeSpan) || type.IsEnum ||
            type.Namespace?.StartsWith("System.") == true)
        {
            return;
        }
        
        visited.Add(obj);
        
        try
        {
            // インスタンスフィールドをチェック
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            foreach (var field in fields)
            {
                try
                {
                    if (field.FieldType == typeof(HttpClient))
                    {
                        field.SetValue(obj, authenticatedClient);
                        Log.Information("インスタンスHttpClientフィールドを置き換え: {TypeName}.{FieldName}", type.Name, field.Name);
                    }
                    // HttpClientを含む他のオブジェクトも再帰的に処理（安全に）
                    else if (field.FieldType.IsClass && !field.FieldType.IsPrimitive && 
                            field.FieldType != typeof(string) && !field.FieldType.IsEnum &&
                            !field.FieldType.Namespace?.StartsWith("System.") == true)
                    {
                        var nestedObj = field.GetValue(obj);
                        if (nestedObj != null && !visited.Contains(nestedObj))
                        {
                            ReplaceHttpClientsInObjectInternal(nestedObj, authenticatedClient, visited);
                        }
                    }
                }
                catch (Exception fieldEx)
                {
                    Log.Debug(fieldEx, "フィールド {FieldName} の処理をスキップ", field.Name);
                }
            }
            
            // インスタンスプロパティをチェック
            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            foreach (var property in properties)
            {
                try
                {
                    if (property.PropertyType == typeof(HttpClient) && property.CanWrite)
                    {
                        property.SetValue(obj, authenticatedClient);
                        Log.Information("インスタンスHttpClientプロパティを置き換え: {TypeName}.{PropertyName}", type.Name, property.Name);
                    }
                    // プロパティからのネストしたオブジェクトの再帰処理（安全に）
                    else if (property.PropertyType.IsClass && !property.PropertyType.IsPrimitive && 
                            property.PropertyType != typeof(string) && !property.PropertyType.IsEnum &&
                            property.CanRead && property.GetIndexParameters().Length == 0 &&
                            !property.PropertyType.Namespace?.StartsWith("System.") == true)
                    {
                        var nestedObj = property.GetValue(obj);
                        if (nestedObj != null && !visited.Contains(nestedObj))
                        {
                            ReplaceHttpClientsInObjectInternal(nestedObj, authenticatedClient, visited);
                        }
                    }
                }
                catch (Exception propEx)
                {
                    Log.Debug(propEx, "プロパティ {PropertyName} の処理をスキップ", property.Name);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "オブジェクト {TypeName} のHttpClient置き換えに失敗", obj.GetType().Name);
        }
    }
    
    private void ReplaceStaticHttpClientsInType(Type type, HttpClient authenticatedClient)
    {
        // 静的フィールドをチェック
        var fields = type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        foreach (var field in fields)
        {
            if (field.FieldType == typeof(HttpClient))
            {
                try
                {
                    field.SetValue(null, authenticatedClient);
                    Log.Information("静的HttpClientフィールドを置き換え: {TypeName}.{FieldName}", type.Name, field.Name);
                }
                catch (Exception fieldEx)
                {
                    Log.Debug(fieldEx, "静的フィールド {FieldName} の設定をスキップ", field.Name);
                }
            }
        }
        
        // 静的プロパティをチェック
        var properties = type.GetProperties(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        foreach (var property in properties)
        {
            if (property.PropertyType == typeof(HttpClient) && property.CanWrite)
            {
                try
                {
                    property.SetValue(null, authenticatedClient);
                    Log.Information("静的HttpClientプロパティを置き換え: {TypeName}.{PropertyName}", type.Name, property.Name);
                }
                catch (Exception propEx)
                {
                    Log.Debug(propEx, "静的プロパティ {PropertyName} の設定をスキップ", property.Name);
                }
            }
        }
    }

    private bool CanGetAesKey(object parameter)
    {
        return !string.IsNullOrWhiteSpace(MapCodeInput) &&
            UserSettings.Default.LastAuthResponse?.AccessToken != null &&
            UserSettings.Default.LastAuthResponse.ExpiresAt > DateTime.Now;
    }
    private void CopyAesKey(object parameter)

    {
        if (CanCopyAesKey(parameter))
        {
            System.Windows.Clipboard.SetText(RetrievedAesKey);
            FLogger.Append(ELog.Information, () => FLogger.Text("AES Key copied to clipboard.", Constants.WHITE, true));
        }
    }
    private bool CanCopyAesKey(object parameter)
    {
        return !string.IsNullOrWhiteSpace(RetrievedAesKey) &&
            !RetrievedAesKey.Contains("Retrieving") &&
            !RetrievedAesKey.Contains("Failed") &&
            !RetrievedAesKey.Contains("error");
    }

    private void SaveAesKey(object parameter)
    {
        if (CanSaveAesKey(parameter))
        {
            try
            {
                var mapCode = MapCodeInput.Trim();
                var key = RetrievedAesKey.Trim();
                var directory = Path.Combine(UserSettings.Default.OutputDirectory, "MapAES");
                Directory.CreateDirectory(directory);
                var fileName = $"{mapCode}.txt";
                var fullPath = Path.Combine(directory, fileName);
                File.WriteAllText(fullPath, key);
                FLogger.Append(ELog.Information, () => FLogger.Text($"AESキーを保存しました: ", Constants.WHITE, false));
                FLogger.Append(ELog.Information, () => FLogger.Link(fileName, fullPath, true));
            }
            catch (Exception ex)
            {
                FLogger.Append(ELog.Error, () => FLogger.Text($"AESキーの保存に失敗しました: {ex.Message}", Constants.RED, true));
            }
        }
    }

    private bool CanSaveAesKey(object parameter)
    {
        return !string.IsNullOrWhiteSpace(RetrievedAesKey) &&
            !RetrievedAesKey.Contains("Retrieving") &&
            !RetrievedAesKey.Contains("Failed") &&
            !RetrievedAesKey.Contains("error");
    }

    #region Pac化
    private string _installedBundlesPath;
    public string InstalledBundlesPath
    {
        get => _installedBundlesPath;
        set
        {
            if (SetProperty(ref _installedBundlesPath, value))
            {
                UpdateIslandProjects();
                ((RelayCommand)ExecutePacCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public ObservableCollection<IslandProject> IslandProjects { get; } = new();

    private IslandProject _selectedIslandProject;
    public IslandProject SelectedIslandProject
    {
        get => _selectedIslandProject;
        set
        {
            if (SetProperty(ref _selectedIslandProject, value))
            {
                ((RelayCommand)ExecutePacCommand).RaiseCanExecuteChanged();
            }
        }
    }

    private bool _isPieMode;
    public bool IsPieMode { get => _isPieMode; set => SetProperty(ref _isPieMode, value); }

    private void BrowseInstalledBundles(object parameter)
    {
        var dialog = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog();
        if (dialog.ShowDialog() == true)
        {
            InstalledBundlesPath = dialog.SelectedPath;
        }
    }

    private async void ExecutePac(object parameter)
    {
        var progressVM = new PacProgressWindowViewModel("pac化を実行中...", "準備しています...");
        var progressWindow = new PacProgressWindow(progressVM)
        {
            Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w is SettingsView)
        };

        var cancellationTokenSource = new CancellationTokenSource();
        progressVM.CancelCommand = new RelayCommand(_ => cancellationTokenSource.Cancel());

        progressWindow.Show();

        try
        {
            await Task.Run(() =>
            {
                var islandFolder = SelectedIslandProject.FullPath;
                var gamePath = UserSettings.Default.GameDirectory;

                // Find the root of the FortniteGame directory to prevent path duplication
                int fortniteGameIndex = gamePath.IndexOf("FortniteGame", StringComparison.OrdinalIgnoreCase);
                if (fortniteGameIndex == -1)
                {
                    throw new DirectoryNotFoundException("ゲームフォルダ内に'FortniteGame'ディレクトリが見つかりません。設定を確認してください。");
                }
                string fortniteRootPath = gamePath.Substring(0, fortniteGameIndex);

                var contentPaksPath = Path.Combine(fortniteRootPath, "FortniteGame", "Content", "Paks");
                var uefnIslandsExe = Path.Combine(fortniteRootPath, "FortniteGame", "Binaries", "Win64", "UEFN-Islands.exe");
                string[] pluginExtensions = { ".pak", ".sig", ".utoc", ".ucas" };

                // 1. Rename files
                progressVM.Update(10, "プラグインファイルを改名しています...");
                cancellationTokenSource.Token.ThrowIfCancellationRequested();
                foreach (var ext in pluginExtensions)
                {
                    var sourceFile = Path.Combine(islandFolder, "plugin" + ext);
                    var destFile = Path.Combine(islandFolder, "pakchunk99Island-WindowsClient" + ext);
                    if (File.Exists(sourceFile))
                    {
                        if (File.Exists(destFile)) File.Delete(destFile);
                        File.Move(sourceFile, destFile);
                    }
                }

                // 2. Extract island name from .utoc
                progressVM.Update(30, ".utocから島名を抽出しています...");
                cancellationTokenSource.Token.ThrowIfCancellationRequested();
                var utocPath = Path.Combine(islandFolder, "pakchunk99Island-WindowsClient.utoc");
                if (!File.Exists(utocPath))
                {
                    throw new FileNotFoundException("改名された.utocファイルが見つかりません。InstalledBundlesフォルダが正しいか確認してください。", utocPath);
                }
                var extractedPluginName = ExtractIslandNameFromUtoc(utocPath, progressVM);

                // 3. Copy files to Paks folder
                progressVM.Update(50, "Paksフォルダにファイルをコピーしています...");
                cancellationTokenSource.Token.ThrowIfCancellationRequested();
                foreach (var ext in pluginExtensions)
                {
                    var sourceFile = Path.Combine(islandFolder, "pakchunk99Island-WindowsClient" + ext);
                    var destFile = Path.Combine(contentPaksPath, "pakchunk99Island-WindowsClient" + ext);
                    if (File.Exists(sourceFile))
                    {
                        if (File.Exists(destFile)) File.Delete(destFile);
                        File.Copy(sourceFile, destFile, true);
                    }
                }

                // 4. Start UEFN-Islands.exe
                if (File.Exists(uefnIslandsExe) && !string.IsNullOrEmpty(extractedPluginName))
                {
                    progressVM.Update(80, "UEFN-Islandsを起動しています...");
                    cancellationTokenSource.Token.ThrowIfCancellationRequested();
                    var pieArg = IsPieMode ? ",ValkyriePIE" : "";
                    var arguments = $"-disableplugins=\"ValkyrieFortnite,AtomVK\" -enableplugins=\"{extractedPluginName}{pieArg}\""; // extractedPluginName is used here
                    Process.Start(uefnIslandsExe, arguments);
                }
                else
                {
                    progressVM.Update(80, "UEFN-Islands.exeが見つかりません。起動をスキップします。");
                    Thread.Sleep(2000); // ユーザーがメッセージを読めるように少し待機
                }
                
                progressVM.Update(100, "完了しました！");
                Thread.Sleep(1000); // Show "Completed" for a moment
            }, cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            progressVM.Update(0, "キャンセルされました。");
            await Task.Delay(1500);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラーが発生しました: {ex.Message}", "pak化エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            progressWindow.Close();
        }
    }

    private bool CanExecutePac(object parameter)
    {
        return SelectedIslandProject != null && !string.IsNullOrEmpty(UserSettings.Default.GameDirectory);
    }

    private void UpdateIslandProjects()
    {
        Application.Current.Dispatcher.Invoke(() => IslandProjects.Clear());
        if (string.IsNullOrEmpty(InstalledBundlesPath) || !Directory.Exists(InstalledBundlesPath)) return;

        try
        {
            var directories = Directory.GetDirectories(InstalledBundlesPath);
            var projects = new List<IslandProject>();
            foreach (var dir in directories)
            {
                var dirInfo = new DirectoryInfo(dir);
                projects.Add(new IslandProject(dirInfo.Name, dirInfo.LastWriteTime, dir));
            }
            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var p in projects.OrderByDescending(p => p.LastModified))
                {
                    IslandProjects.Add(p);
                }
                SelectedIslandProject = IslandProjects.FirstOrDefault();
            });
        }
        catch (Exception e)
        {
            FLogger.Append(ELog.Error, () => FLogger.Text($"プロジェクトの読み込みに失敗しました: {e.Message}", Constants.RED, true));
        }
    }

    private string ExtractIslandNameFromUtoc(string utocPath, PacProgressWindowViewModel progressVM)
    {
        // This is a direct port of the logic from MIddleMan's ExtractIslandNameFromPak.cs
        // It's fragile but should work for its intended purpose.
        var fileBytes = File.ReadAllBytes(utocPath);
        var pattern = Encoding.UTF8.GetBytes("/FortniteGame/Plugins/GameFeatures/");
        int index = fileBytes.AsSpan().IndexOf(pattern);
        if (index == -1)
        {
            progressVM.Update(40, "警告: .utocからプラグイン名を抽出できませんでした。");
            Thread.Sleep(2000); // ユーザーがメッセージを読めるように少し待機
            return null;
        }

        int startIndex = index + pattern.Length;
        int endIndex = fileBytes.AsSpan(startIndex).IndexOf((byte)'/');
        if (endIndex == -1)
        {
            throw new Exception(".utocファイルからプラグイン名を抽出できませんでした。");
        }

        return Encoding.UTF8.GetString(fileBytes, startIndex, endIndex);
    }
    #endregion
}

public class IslandProject
{
    public string Name { get; }
    public DateTime LastModified { get; }
    public string FullPath { get; }
    public string DisplayName => $"{Name} - {LastModified:yyyy/MM/dd HH:mm}";

    public IslandProject(string name, DateTime lastModified, string fullPath)
    {
        Name = name;
        LastModified = lastModified;
        FullPath = fullPath;
    }
}

// SettingsViewModelクラスの外部にGetStringOrNumberValueメソッドを配置
public static class JsonExtensions 
{
    /// <summary>
    /// JsonElementから文字列または数値を文字列として安全に取得します
    /// </summary>
    public static string GetStringOrNumberValue(this JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => element.GetRawText()
        };
    }
}
