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
    private string _fModelLanguageSnapshot;
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

    private string _selectedFModelLanguage;
    public string SelectedFModelLanguage
    {
        get => _selectedFModelLanguage;
        set => SetProperty(ref _selectedFModelLanguage, value);
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

    private EBruteForceAesMode _selectedBruteForceAesMode;
    public EBruteForceAesMode SelectedBruteForceAesMode
    {
        get => _selectedBruteForceAesMode;
        set => SetProperty(ref _selectedBruteForceAesMode, value);
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

    private ulong _criwareDecryptionKey;
    public ulong CriwareDecryptionKey
    {
        get => _criwareDecryptionKey;
        set => SetProperty(ref _criwareDecryptionKey, value);
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
    public ReadOnlyObservableCollection<EBruteForceAesMode> BruteForceAesModes { get; private set; }
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
    public IEnumerable<string> AvailableLanguages { get; } = new[] { "English", "Japanese" };

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
        private bool _isPacRunning;
        public bool IsPacRunning
        {
            get => _isPacRunning;
            set
            {
                if (SetProperty(ref _isPacRunning, value))
                {
                    ((RelayCommand)ExecutePacCommand).RaiseCanExecuteChanged();
                }
            }
        }



    public void Initialize()
    {
        _outputSnapshot = UserSettings.Default.OutputDirectory;
        _rawDataSnapshot = UserSettings.Default.RawDataDirectory; // _rawDataSnapshot の初期化
        _propertiesSnapshot = UserSettings.Default.PropertiesDirectory; // _propertiesSnapshot の初期化
        _textureSnapshot = UserSettings.Default.TextureDirectory; // _textureSnapshot の初期化
        _audioSnapshot = UserSettings.Default.AudioDirectory; // _audioSnapshot の初期化
        _modelSnapshot = UserSettings.Default.ModelDirectory;
        _gameSnapshot = UserSettings.Default.GameDirectory;
        _fModelLanguageSnapshot = UserSettings.Default.FModelLanguage;
        _uePlatformSnapshot = UserSettings.Default.CurrentDir.TexturePlatform;
        _ueGameSnapshot = UserSettings.Default.CurrentDir.UeVersion;
        _customVersionsSnapshot = UserSettings.Default.CurrentDir.Versioning.CustomVersions;
        _optionsSnapshot = UserSettings.Default.CurrentDir.Versioning.Options;
        _mapStructTypesSnapshot = UserSettings.Default.CurrentDir.Versioning.MapStructTypes;
        _criwareDecryptionKey = UserSettings.Default.CurrentDir.CriwareDecryptionKey;

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
        SelectedFModelLanguage = _fModelLanguageSnapshot;
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
        CriwareDecryptionKey = _criwareDecryptionKey;
        SelectedAesReload = UserSettings.Default.AesReload;
        SelectedBruteForceAesMode = UserSettings.Default.BruteForceAesMode;
        SelectedRestoreTabsOnStartup = UserSettings.Default.RestoreTabsOnStartup;
        SelectedDiscordRpc = UserSettings.Default.DiscordRpc;

        var ueGames = new ObservableCollection<EGame>(EnumerateUeGames());
        UeGames = new ReadOnlyObservableCollection<EGame>(ueGames);
        DiffUeGames = new ReadOnlyObservableCollection<EGame>(ueGames); // Can't reuse UeGames because FilterableComboBox would share the same ItemsSource
        AssetLanguages = new ReadOnlyObservableCollection<ELanguage>(new ObservableCollection<ELanguage>(EnumerateAssetLanguages()));
        AesReloads = new ReadOnlyObservableCollection<EAesReload>(new ObservableCollection<EAesReload>(EnumerateAesReloads()));
        BruteForceAesModes = new ReadOnlyObservableCollection<EBruteForceAesMode>(new ObservableCollection<EBruteForceAesMode>(EnumerateBruteForceAesModes()));
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
            _isPacRunning = false; // Initialize the new property
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
            _gameSnapshot != UserSettings.Default.GameDirectory || // textbox
            _diffGameSnapshot != UserSettings.Default.DiffGameDirectory || // textbox
            _fModelLanguageSnapshot != SelectedFModelLanguage)
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
        UserSettings.Default.CurrentDir.CriwareDecryptionKey = CriwareDecryptionKey;

        UserSettings.Default.FModelLanguage = SelectedFModelLanguage;
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
        UserSettings.Default.BruteForceAesMode = SelectedBruteForceAesMode;
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
    private IEnumerable<EBruteForceAesMode> EnumerateBruteForceAesModes() => Enum.GetValues<EBruteForceAesMode>();
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
        var userAgent = "FortniteGame/++Fortnite+Release-39.00-CL-48801071 (http-eventloop) Windows/10.0.26100.1.768.64bit"; // Default fallback
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
            var mappingsData = await httpClient.GetFromJsonAsync<JsonElement?>("https://api.fortniteapi.com/v1/mappings");
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
            userAgent = $"FortniteGame/++Fortnite+{versionStr} (http-eventloop) Windows/10.0.26100.1.768.64bit";

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
                await DownloadAndCreatePak(contentData.Value, MapCodeInput, userAgent);
            }
            else
            {
                RetrievedAesKey = "マップは暗号化されていません";
                Log.Information("マップは暗号化されていません: {MapCode}", mapCode);
                FLogger.Append(ELog.Information, () => FLogger.Text(RetrievedAesKey, Constants.WHITE, true));

                // 暗号化されていない場合でもPAK化を試みる
                await DownloadAndCreatePak(contentData.Value, MapCodeInput, userAgent);
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

    private async Task DownloadAndCreatePak(JsonElement contentData, string mapCode, string userAgent)
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
            
            var chunkBaseUrl = BuildInitialChunkBaseUrl(baseUrl);
            
            // チャンクダウンロード用HttpClient（Authorizationヘッダーなし）
            using var httpClient = new HttpClient();
            // Authorizationヘッダーは追加しない（Epic公式仕様）
            
            // Fortnite User-Agentを設定
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
            FLogger.Append(ELog.Information, () => FLogger.Text("マニフェストURLとチャンク設定を確定しました。", Constants.WHITE, true));

            byte[] manifestBytes = null;

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
                    manifestBytes = await httpClient.GetByteArrayAsync(fullManifestUrl);
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
                        
                        manifestBytes ??= await File.ReadAllBytesAsync(tempManifestPath);
                        manifest = FBuildPatchAppManifest.Deserialize(manifestBytes, fallbackOptions);
                        Log.Information("ファイルからのマニフェスト読み込み成功");
                    }
                    chunkBaseUrl = ResolveChunkBaseUrl(manifest, manifestBytes, chunkBaseUrl);
                    var elapsedTime = Stopwatch.GetElapsedTime(startTs);
                    Log.Information("マニフェスト解析完了: {FileCount} ファイル ({ElapsedMs:F1}ms)", 
                        manifest.Files.Count(), elapsedTime.TotalMilliseconds);
                    FLogger.Append(ELog.Information, () => FLogger.Text($"マニフェスト解析完了: {manifest.Files.Count()} ファイル ({elapsedTime.TotalMilliseconds:F1}ms)", Constants.GREEN, true));
                    LogManifestStructure(manifest);
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
                    manifestBytes = await fallbackHttpClient.GetByteArrayAsync(fullManifestUrl);
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
                await ExtractFilesFromManifest(manifest, outputDir, mapCode, chunkBaseUrl, userAgent);

                if (!ValidateExtractedPluginFiles(outputDir, out var validationError))
                {
                    var skipMessage = $"抽出ファイルの検証に失敗したためPAC処理を中止しました: {validationError}";
                    Log.Warning(skipMessage);
                    FLogger.Append(ELog.Warning, () => FLogger.Text(skipMessage, Constants.YELLOW, true));
                    return;
                }

                await ExecuteMapAesPacPostProcess(outputDir);
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

    private async Task ExecuteMapAesPacPostProcess(string extractedFolder)
    {
        try
        {
            FLogger.Append(ELog.Information, () => FLogger.Text("MapAES抽出後のpak化処理を開始します...", Constants.WHITE, true));
            await Task.Run(() => ExecutePacCore(extractedFolder, CancellationToken.None, null, true));
            await RefreshArchivesAfterPacAsync();
            FLogger.Append(ELog.Information, () => FLogger.Text("MapAES抽出後のpak化処理が完了しました。", Constants.GREEN, true));
        }
        catch (Exception ex)
        {
            FLogger.Append(ELog.Error, () => FLogger.Text($"MapAES抽出後のpak化処理に失敗しました: {ex.Message}", Constants.RED, true));
            Log.Error(ex, "MapAES抽出後のpak化処理に失敗しました");
        }
    }

    private async Task ExtractFilesFromManifest(FBuildPatchAppManifest manifest, string outputDir, string mapCode, string chunkBaseUrl, string userAgent)
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
            SetGlobalHttpClientDefaults(userAgent);
            
            // カスタムチャンクダウンロードを試行
            var useCustomDownload = true; // テスト用フラグ
            
            if (useCustomDownload)
            {
                Log.Information("カスタムチャンクダウンロードを使用します");
                await ExtractFilesUsingCustomDownload(manifest, outputDir, chunkBaseUrl, userAgent);
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

    private async Task ExtractFilesUsingCustomDownload(FBuildPatchAppManifest manifest, string outputDir, string chunkBaseUrl, string userAgent)
    {
        try
        {
            Log.Information("カスタムチャンクダウンロードでファイルを抽出中...");
            FLogger.Append(ELog.Information, () => FLogger.Text("カスタムチャンクダウンロードを開始します。", Constants.WHITE, true));
            
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
            httpClient.DefaultRequestHeaders.UserAgent.Clear();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            httpClient.DefaultRequestHeaders.Accept.Clear();
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("*/*");
            httpClient.DefaultRequestHeaders.AcceptEncoding.Clear();
            httpClient.DefaultRequestHeaders.AcceptEncoding.ParseAdd("deflate, gzip");
            
            Log.Information("カスタムHttpClient設定完了: UserAgent={UserAgent}", userAgent);
            FLogger.Append(ELog.Information, () => FLogger.Text($"UserAgent: {userAgent}", Constants.WHITE, true));
            
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
                    
                    // チャンク情報を取得
                    var chunkParts = GetChunkParts(fileManifest);
                    var fileSize = GetFileSize(fileManifest);
                    
                    Log.Information("ファイル情報: {FileName}, Size={Size}, Chunks={ChunkCount}", fileName, fileSize, chunkParts?.Count() ?? 0);
                    
                    if (chunkParts == null || !chunkParts.Any())
                    {
                        Log.Warning("チャンク情報がありません: {FileName}", fileName);
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
            var fileName = fileManifest.FileName;

            if (await TryDownloadUsingManifestStream(httpClient, manifest, fileManifest, outputPath, fileSize))
            {
                Log.Information("manifestストリームで再構築完了: {FileName} (総サイズ: {TotalSize} bytes)", fileName, fileSize);
                return;
            }

            Log.Warning("manifestストリーム抽出に失敗したため、カスタム再構築ロジックを使用します: {FileName}", fileName);

            var chunkCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var chunkPartList = chunkParts?.ToList() ?? new List<object>();

            Log.Information("チャンクベースダウンロード開始: {FileName}, {Size} bytes", fileName, fileSize);

            var downloadedSize = 0L;
            var writePosition = 0L;
            var totalChunks = chunkPartList.Count;
            var sawExplicitDestinationOffset = false;

            Log.Information("ファイル {FileName} のチャンク処理開始: {ChunkCount} チャンク", fileName, totalChunks);

            if (totalChunks == 0)
            {
                Log.Warning("チャンク情報がありません: {FileName}", fileName);
                return;
            }

            using (var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                for (var chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    try
                    {
                    var displayChunkIndex = chunkIndex + 1;
                    var chunkPart = chunkPartList[chunkIndex];
                    Log.Information("チャンク {Index} 処理中...", displayChunkIndex);

                    var chunkGuidRaw = GetChunkGuid(chunkPart);
                    if (chunkGuidRaw == null)
                    {
                        Log.Warning("チャンクGUIDが取得できません: チャンク {Index}", displayChunkIndex);
                        continue;
                    }

                    var chunkGuidText = chunkGuidRaw.ToString() ?? string.Empty;
                    var chunkGuidKey = NormalizeGuidLike(chunkGuidText);
                    var rawOffset = GetRawOffset(chunkPart);
                    var partOffsetInChunk = GetChunkSourceOffset(chunkPart);
                    var partSize = GetChunkSize(chunkPart);

                    if (partSize <= 0)
                    {
                        Log.Warning("チャンクサイズが無効です: {FileName}, Chunk {Index}, Size={Size}", fileName, chunkIndex, partSize);
                        continue;
                    }

                    var chunkInfo = FindChunkInfo(manifest, chunkGuidRaw);
                    if (chunkInfo == null)
                    {
                        Log.Warning("チャンク情報が見つかりません: {ChunkGuid}", chunkGuidText);
                        continue;
                    }

                    if (!chunkCache.TryGetValue(chunkGuidKey, out var decompressedData))
                    {
                        var chunkUrl = BuildChunkUrl(chunkBaseUrl, chunkInfo, chunkGuidText);
                        Log.Information("File: {FileName} - Chunk {ChunkIndex}/{TotalChunks} をダウンロードします", fileName, chunkIndex, totalChunks);

                        if (string.IsNullOrEmpty(chunkUrl))
                        {
                            Log.Error("チャンクURLが空です: チャンク {Index}", chunkIndex);
                            continue;
                        }

                        var chunkData = await httpClient.GetByteArrayAsync(chunkUrl);
                        Log.Information("チャンクダウンロード成功: {Size} bytes", chunkData.Length);

                        decompressedData = null;
                        var uncompressedSize = GetChunkUncompressedSize(chunkInfo);
                        if (uncompressedSize <= 0)
                        {
                            Log.Warning("チャンク非圧縮サイズが取得できませんでした (0)。圧縮データのまま使用します: {ChunkGuid}", chunkGuidText);
                        }
                        if (uncompressedSize > 0)
                        {
                            if (chunkData.Length == uncompressedSize)
                            {
                                Log.Information("チャンクは非圧縮のようです。そのまま使用します。");
                                decompressedData = chunkData;
                            }
                            else
                            {
                                decompressedData = await DecompressChunkData(chunkData, (int)uncompressedSize, chunkGuidText);
                            }
                        }
                        else
                        {
                            decompressedData = chunkData;
                        }

                        chunkCache[chunkGuidKey] = decompressedData;
                    }

                    var hasExplicitDestinationOffset = TryGetChunkDestinationOffset(chunkPart, out var destinationOffset);
                    if (hasExplicitDestinationOffset)
                    {
                        sawExplicitDestinationOffset = true;
                    }
                    if (!hasExplicitDestinationOffset)
                    {
                        destinationOffset = writePosition;
                    }

                    if (destinationOffset < 0)
                    {
                        destinationOffset = writePosition;
                    }

                    if (fileSize > 0)
                    {
                        if (destinationOffset >= fileSize)
                        {
                            Log.Warning("宛先オフセットがファイルサイズ範囲外のため、順次配置にフォールバック: File={FileName}, Chunk={ChunkIndex}, Destination={Destination}, FileSize={FileSize}",
                                fileName, displayChunkIndex, destinationOffset, fileSize);
                            destinationOffset = writePosition;
                        }
                        else if (destinationOffset < writePosition)
                        {
                            Log.Warning("宛先オフセットが逆行したため、順次配置にフォールバック: File={FileName}, Chunk={ChunkIndex}, Destination={Destination}, WritePosition={WritePosition}",
                                fileName, displayChunkIndex, destinationOffset, writePosition);
                            destinationOffset = writePosition;
                        }
                    }

                    if (partOffsetInChunk <= 0 && rawOffset > 0)
                    {
                        if (rawOffset < decompressedData.LongLength && rawOffset + partSize <= decompressedData.LongLength)
                        {
                            partOffsetInChunk = rawOffset;
                        }
                    }

                    if (partOffsetInChunk >= decompressedData.LongLength)
                    {
                        Log.Warning("チャンク内オフセットが範囲外: File={FileName}, Chunk={ChunkIndex}, Offset={Offset}, ChunkDataSize={ChunkSize}",
                            fileName, chunkIndex, partOffsetInChunk, decompressedData.LongLength);
                        continue;
                    }

                    var available = decompressedData.LongLength - partOffsetInChunk;
                    var bytesRequested = partSize;

                    long nextDestinationOffset = -1;
                    if (hasExplicitDestinationOffset)
                    {
                        for (var nextIndex = chunkIndex + 1; nextIndex < totalChunks; nextIndex++)
                        {
                            if (TryGetChunkDestinationOffset(chunkPartList[nextIndex], out var candidate) && candidate > destinationOffset)
                            {
                                nextDestinationOffset = candidate;
                                break;
                            }
                        }
                    }

                    var layoutExpectedSize = 0L;
                    if (nextDestinationOffset > destinationOffset)
                    {
                        layoutExpectedSize = nextDestinationOffset - destinationOffset;
                    }
                    else if (hasExplicitDestinationOffset && fileSize > 0 && destinationOffset < fileSize)
                    {
                        layoutExpectedSize = fileSize - destinationOffset;
                    }

                    // Only use layout-based size calculation as a fallback if the size from the manifest is invalid.
                    // Overriding a valid size can lead to data corruption if chunk parts are not perfectly ordered.
                    if (bytesRequested <= 0 && layoutExpectedSize > 0)
                    {
                        bytesRequested = layoutExpectedSize;
                    }
                    
                    if (bytesRequested <= 0)
                    {
                        bytesRequested = available;
                    }

                    if (fileSize > 0)
                    {
                        var remainingInFile = fileSize - destinationOffset;
                        if (remainingInFile <= 0)
                        {
                            continue;
                        }

                        bytesRequested = Math.Min(bytesRequested, remainingInFile);
                    }

                    var bytesToWrite = Math.Min(bytesRequested, available);
                    if (bytesToWrite <= 0)
                    {
                        Log.Warning("書き込み可能データがありません: File={FileName}, Chunk={ChunkIndex}, Requested={Requested}, Available={Available}",
                            fileName, displayChunkIndex, bytesRequested, available);
                        continue;
                    }

                    outputStream.Seek(destinationOffset, SeekOrigin.Begin);
                    outputStream.Write(decompressedData, (int)partOffsetInChunk, (int)bytesToWrite);

                    downloadedSize += bytesToWrite;
                    writePosition = Math.Max(writePosition, destinationOffset + bytesToWrite);

                        Log.Information("チャンク書き込み成功: {Index}/{Total}, {Progress:F1}%",
                            displayChunkIndex, totalChunks, fileSize > 0 ? downloadedSize * 100.0 / fileSize : 0.0);
                    }
                    catch (HttpRequestException httpEx)
                    {
                        Log.Error(httpEx, "チャンクダウンロードエラー: チャンク {Index}", chunkIndex);
                        throw;
                    }
                    catch (Exception chunkEx)
                    {
                        Log.Warning(chunkEx, "チャンク処理エラー: チャンク {Index}", chunkIndex);
                    }
                }

                outputStream.Flush();
            }

            var actualFileSize = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;

            if (fileSize > 0 && !sawExplicitDestinationOffset && actualFileSize < fileSize)
            {
                await using (var padStream = new FileStream(outputPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    padStream.SetLength(fileSize);
                    await padStream.FlushAsync();
                }

                Log.Warning("DestOffsetなしmanifestのため末尾をゼロ埋め拡張しました: {FileName} Expected={Expected} Before={Before} Downloaded={Downloaded}",
                    fileName, fileSize, actualFileSize, downloadedSize);
                actualFileSize = new FileInfo(outputPath).Length;
            }

            if (fileSize > 0 && (actualFileSize != fileSize || downloadedSize != fileSize))
            {
                if (!sawExplicitDestinationOffset && actualFileSize == fileSize && downloadedSize < fileSize)
                {
                    Log.Warning("DestOffsetなしmanifestでゼロ埋め拡張後にサイズ整合と判定します: {FileName} Expected={Expected} Actual={Actual} Downloaded={Downloaded}",
                        fileName, fileSize, actualFileSize, downloadedSize);
                }
                else
                {
                throw new InvalidOperationException($"ファイル再構築が不完全です: {fileName} expected={fileSize} actual={actualFileSize} downloaded={downloadedSize}");
                }
            }

            if (fileSize > 0 && downloadedSize <= 0)
            {
                throw new InvalidOperationException($"ファイル再構築に失敗しました。1バイトも書き込まれていません: {fileName}");
            }

            Log.Information("ファイル再構築完了: {FileName} (総サイズ: {TotalSize} bytes, 書き込み: {Downloaded} bytes)", fileName, fileSize, downloadedSize);
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
            }
            catch (Exception cleanupEx)
            {
                Log.Warning(cleanupEx, "チャンク再構築失敗後のクリーンアップに失敗しました: {OutputPath}", outputPath);
            }

            Log.Error(ex, "カスタムファイルチャンクダウンロードエラー: {FileName}", fileManifest.FileName);
            throw;
        }
    }

    private async Task<byte[]> DecompressChunkData(byte[] chunkData, int uncompressedSize, string chunkGuidText)
    {
        try
        {
            var output = new byte[uncompressedSize];
            OodleHelper.Decompress(chunkData, 0, chunkData.Length, output, 0, uncompressedSize);
            return output;
        }
        catch (Exception oodleEx)
        {
            Log.Warning(oodleEx, "Oodle解凍失敗。ZlibHelperで再試行します: {ChunkGuid}", chunkGuidText);
        }

        try
        {
            var output = new byte[uncompressedSize];
            ZlibHelper.Decompress(chunkData, 0, chunkData.Length, output, 0, uncompressedSize);
            return output;
        }
        catch (Exception zlibEx)
        {
            Log.Warning(zlibEx, "ZlibHelper解凍失敗。ZLibStreamで再試行します: {ChunkGuid}", chunkGuidText);
        }

        try
        {
            using var compressedStream = new MemoryStream(chunkData);
            using var zlibStream = new ZLibStream(compressedStream, CompressionMode.Decompress);
            using var decompressedStream = new MemoryStream();
            await zlibStream.CopyToAsync(decompressedStream);
            var output = decompressedStream.ToArray();
            if (output.Length != uncompressedSize)
            {
                Log.Warning("ZLibStream解凍サイズが想定と異なります: Expected={Expected}, Actual={Actual}, Chunk={ChunkGuid}",
                    uncompressedSize, output.Length, chunkGuidText);
            }
            return output;
        }
        catch (Exception zlibStreamEx)
        {
            Log.Warning(zlibStreamEx, "ZLibStream解凍失敗。DeflateStreamで再試行します: {ChunkGuid}", chunkGuidText);
        }

        using (var compressedStream = new MemoryStream(chunkData))
        {
            if (chunkData.Length > 2 && (chunkData[0] & 0x0F) == 8)
            {
                compressedStream.Position = 2;
            }

            using var deflateStream = new DeflateStream(compressedStream, CompressionMode.Decompress);
            using var decompressedStream = new MemoryStream();
            await deflateStream.CopyToAsync(decompressedStream);
            var output = decompressedStream.ToArray();
            if (output.Length != uncompressedSize)
            {
                Log.Warning("DeflateStream解凍サイズが想定と異なります: Expected={Expected}, Actual={Actual}, Chunk={ChunkGuid}",
                    uncompressedSize, output.Length, chunkGuidText);
            }
            return output;
        }
    }

    private async Task<bool> TryDownloadUsingManifestStream(HttpClient authenticatedClient, FBuildPatchAppManifest manifest, dynamic fileManifest, string outputPath, long expectedSize)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                ReplaceAllEpicManifestParserHttpClients(authenticatedClient, manifest);
                ReplaceHttpClientsInObject(fileManifest, authenticatedClient);

                using var fileStream = fileManifest.GetStream();
                await using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                await fileStream.CopyToAsync(outputStream);
                await outputStream.FlushAsync();

                var actualSize = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
                if (expectedSize > 0 && actualSize != expectedSize)
                {
                    throw new InvalidOperationException($"manifestストリーム抽出サイズ不一致 expected={expectedSize} actual={actualSize}");
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "manifestストリーム抽出に失敗 (Attempt {Attempt}/{MaxAttempts}): {FileName}", attempt, maxAttempts, fileManifest.FileName);
                try
                {
                    if (File.Exists(outputPath))
                    {
                        File.Delete(outputPath);
                    }
                }
                catch
                {
                }

                if (attempt < maxAttempts)
                {
                    await Task.Delay(250 * attempt);
                    continue;
                }
            }
        }

        return false;
    }

    private static bool IsCriticalContainerFile(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Equals(".pak", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".utoc", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".ucas", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RebuildFileUsingManifestStream(dynamic fileManifest, string outputPath, long expectedSize)
    {
        await Task.CompletedTask;
    }
    
    private object GetChunkGuid(object chunkPart)
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
                        return value;
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
    
    private long GetChunkUncompressedSize(object chunkInfo)
    {
        try
        {
            if (TryGetNumericMember(chunkInfo, "UncompressedSize", out var size)) return size;
            if (TryGetNumericMember(chunkInfo, "FileSize", out size)) return size;
            return 0;
        }
        catch
        {
            return 0;
        }
    }
    
    private object FindChunkInfo(FBuildPatchAppManifest manifest, object chunkGuid)
    {
        try
        {
            if (chunkGuid == null)
            {
                return null;
            }

            var targetNormalized = NormalizeGuidLike(chunkGuid.ToString() ?? string.Empty);

            return manifest.ChunkList?.FirstOrDefault(c =>
                Equals(c.Guid, chunkGuid) ||
                string.Equals(
                    NormalizeGuidLike(c.Guid.ToString()),
                    targetNormalized,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }
    
    private string BuildChunkUrl(string chunkBaseUrl, object chunkInfo, string chunkGuid)
    {
        var baseWithSlash = string.IsNullOrEmpty(chunkBaseUrl) || chunkBaseUrl.EndsWith("/") ? chunkBaseUrl : chunkBaseUrl + "/";

        if (chunkInfo != null)
        {
            try
            {
                var type = chunkInfo.GetType();
                var hashProp = type.GetProperty("Hash");
                var groupProp = type.GetProperty("GroupNumber");
                var guidProp = type.GetProperty("Guid");

                if (hashProp != null && groupProp != null && guidProp != null)
                {
                    var hashVal = hashProp.GetValue(chunkInfo);
                    var groupVal = groupProp.GetValue(chunkInfo);
                    var guidVal = guidProp.GetValue(chunkInfo);

                    if (hashVal is ulong hash && groupVal is byte group)
                    {
                        var guidStr = guidVal?.ToString()?.Replace("-", "")?.ToUpper() ?? chunkGuid.Replace("-", "").ToUpper();
                        var hashStr = hash.ToString("X16");
                        var groupStr = group.ToString("D2");

                        var v4Url = $"{baseWithSlash}ChunksV4/{groupStr}/{hashStr}_{guidStr}.chunk";
                        Log.Information("チャンクURL構築 (V4): {Url}", v4Url);
                        return v4Url;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "チャンクURL構築中にエラーが発生しました (V4)");
            }
        }

        // Fallback: BaseUrl + Guid
        var normalizedGuid = NormalizeGuidLike(chunkGuid);
        var fullUrl = $"{baseWithSlash}{normalizedGuid}.chunk";
        Log.Information("チャンクURL構築 (Fallback): {Url}", fullUrl);
        return fullUrl;
    }

    private static string BuildInitialChunkBaseUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return string.Empty;
        }

        var normalized = baseUrl.Trim().TrimEnd('/') + "/alt/";
        return normalized;
    }

    private string ResolveChunkBaseUrl(FBuildPatchAppManifest manifest, byte[] manifestBytes, string fallback)
    {
        var candidate = TryResolveChunkBaseUrlFromManifestBytes(manifestBytes) ??
                        TryResolveChunkBaseUrlViaReflection(manifest);
        var normalizedCandidate = NormalizeChunkBaseUrl(candidate, fallback);
        if (!string.IsNullOrEmpty(normalizedCandidate))
        {
            Log.Information("マニフェスト由来のチャンクベースURLを使用します: {ChunkBaseUrl}", normalizedCandidate);
            return normalizedCandidate;
        }

        var normalizedFallback = NormalizeChunkBaseUrl(fallback);
        Log.Information("チャンクベースURLにフォールバックします: {ChunkBaseUrl}", normalizedFallback);
        return normalizedFallback;
    }

    private string TryResolveChunkBaseUrlFromManifestBytes(byte[] manifestBytes)
    {
        if (manifestBytes == null || manifestBytes.Length == 0)
        {
            return null;
        }

        var encodings = new[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.ASCII };
        foreach (var encoding in encodings)
        {
            try
            {
                var text = encoding.GetString(manifestBytes);
                var url = TryExtractChunkBaseFromText(text);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }
            }
            catch
            {
                // ignore decoding issues
            }
        }

        return null;
    }

    private string TryExtractChunkBaseFromText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var match = Regex.Match(text, @"https://[^\s""']+?/ChunksV\d+/", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var value = match.Value;
            var index = value.IndexOf("/ChunksV", StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                return value.Substring(0, index);
            }

            return value;
        }

        return null;
    }

    private string TryResolveChunkBaseUrlViaReflection(FBuildPatchAppManifest manifest)
    {
        if (manifest == null)
        {
            return null;
        }

        var direct = TryGetStringMember(manifest, "ChunkBaseUrl", "ChunkBaseUri", "ChunkBasePath", "ChunkBase");
        if (IsProbablyChunkBase(direct))
        {
            return direct;
        }

        var dataGroupCandidate = TryResolveChunkBaseFromDataGroups(manifest);
        if (!string.IsNullOrWhiteSpace(dataGroupCandidate))
        {
            return dataGroupCandidate;
        }

        var dictionaryCandidate = TryResolveChunkBaseFromDictionaries(manifest);
        if (!string.IsNullOrWhiteSpace(dictionaryCandidate))
        {
            return dictionaryCandidate;
        }

        return null;
    }

    private string TryResolveChunkBaseFromDataGroups(FBuildPatchAppManifest manifest)
    {
        var type = manifest.GetType();
        foreach (var member in new[] { "DataGroupList", "DataGroups", "ChunkGroups" })
        {
            var groupsObj = GetMemberValue(type, manifest, member);
            if (groupsObj is System.Collections.IEnumerable enumerable)
            {
                foreach (var group in enumerable)
                {
                    if (group == null)
                    {
                        continue;
                    }

                    var url = TryGetStringMember(group, "Url", "Uri", "BaseUrl", "ChunkBaseUrl", "ChunkBaseUri");
                    if (IsProbablyChunkBase(url))
                    {
                        return url;
                    }
                }
            }
        }

        return null;
    }

    private string TryResolveChunkBaseFromDictionaries(FBuildPatchAppManifest manifest)
    {
        var type = manifest.GetType();
        foreach (var member in new[] { "Meta", "ManifestMeta", "CustomFields", "Header" })
        {
            var dictObj = GetMemberValue(type, manifest, member);
            if (dictObj is System.Collections.IDictionary dict)
            {
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    if (entry.Value is string str && IsProbablyChunkBase(str))
                    {
                        return str;
                    }
                }
            }
        }

        return null;
    }

    private object GetMemberValue(Type type, object instance, string memberName)
    {
        if (type == null || instance == null || string.IsNullOrWhiteSpace(memberName))
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
        try
        {
            var property = type.GetProperty(memberName, flags);
            if (property != null)
            {
                return property.GetValue(instance);
            }

            var field = type.GetField(memberName, flags);
            if (field != null)
            {
                return field.GetValue(instance);
            }
        }
        catch
        {
            // ignore reflection issues
        }

        return null;
    }

    private string TryGetStringMember(object source, params string[] memberNames)
    {
        if (source == null || memberNames == null)
        {
            return null;
        }

        var type = source.GetType();
        foreach (var name in memberNames)
        {
            var value = GetMemberValue(type, source, name);
            if (value == null)
            {
                continue;
            }

            var strValue = value switch
            {
                string s => s,
                Uri uri => uri.ToString(),
                _ => value.ToString()
            };

            if (!string.IsNullOrWhiteSpace(strValue))
            {
                return strValue;
            }
        }

        return null;
    }

    private bool IsProbablyChunkBase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("ChunksV", StringComparison.OrdinalIgnoreCase) ||
               value.Contains(".chunk", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("cooked-content", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeChunkBaseUrl(string url, string fallbackForAlt = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var normalized = url.Trim().Replace("\\", "/");
        var schemeSeparator = normalized.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator > 0)
        {
            var scheme = normalized.Substring(0, schemeSeparator + 3);
            var rest = normalized.Substring(schemeSeparator + 3);
            while (rest.Contains("//"))
            {
                rest = rest.Replace("//", "/");
            }

            normalized = scheme + rest;
        }

        var chunkIndex = normalized.IndexOf("/ChunksV", StringComparison.OrdinalIgnoreCase);
        if (chunkIndex > 0)
        {
            normalized = normalized.Substring(0, chunkIndex);
        }

        if (normalized.EndsWith(".chunk", StringComparison.OrdinalIgnoreCase))
        {
            var lastSlash = normalized.LastIndexOf('/');
            if (lastSlash > 0)
            {
                normalized = normalized.Substring(0, lastSlash);
            }
        }

        normalized = normalized.TrimEnd('/') + "/";

        if (!normalized.Contains("/alt/", StringComparison.OrdinalIgnoreCase))
        {
            var fallbackHasAlt = !string.IsNullOrWhiteSpace(fallbackForAlt) &&
                                  fallbackForAlt.Contains("/alt/", StringComparison.OrdinalIgnoreCase);
            if (fallbackHasAlt)
            {
                normalized = normalized.TrimEnd('/') + "/alt/";
            }
        }

        return normalized;
    }

    private static string NormalizeGuidLike(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray();
        return new string(chars);
    }

    private void LogManifestStructure(FBuildPatchAppManifest manifest)
    {
        try
        {
            if (manifest?.Files == null)
            {
                Log.Warning("manifest構造ログ出力をスキップしました。manifestがnullです。");
                return;
            }

            var manifestFiles = manifest.Files.ToList();
            var pathEntries = manifestFiles
                .Select(x => x.FileName?.Replace('\\', '/') ?? string.Empty)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Take(5000)
                .ToList();

            Log.Information("manifest構造 (Files={FileCount}, PathEntries={EntryCount})", manifestFiles.Count, pathEntries.Count);

            foreach (var relativePath in pathEntries)
            {
                var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length <= 1)
                {
                    Log.Information("[MANIFEST_FILE] {Path}", relativePath);
                    continue;
                }

                var current = string.Empty;
                for (var i = 0; i < segments.Length - 1; i++)
                {
                    current = string.IsNullOrEmpty(current) ? segments[i] : $"{current}/{segments[i]}";
                    Log.Information("[MANIFEST_DIR ] {Path}", current);
                }

                Log.Information("[MANIFEST_FILE] {Path}", relativePath);
            }

            foreach (var fileManifest in manifestFiles)
            {
                var fileName = fileManifest.FileName?.Replace('\\', '/') ?? "(unknown)";
                var fileSize = GetManifestFileSize(fileManifest);
                var chunkParts = GetChunkParts(fileManifest)?.ToList() ?? new List<object>();
                Log.Information("[MANIFEST_ENTRY] File={FileName} Size={FileSize} Chunks={ChunkCount}", fileName, fileSize, chunkParts.Count);

                for (var i = 0; i < chunkParts.Count; i++)
                {
                    var chunk = chunkParts[i];
                    var guid = GetChunkGuid(chunk)?.ToString() ?? "(null)";
                    var destinationOffset = ResolveChunkDestinationOffset(chunk, -1);
                    var sourceOffset = GetChunkSourceOffset(chunk);
                    var size = GetChunkSize(chunk);
                    Log.Information("[MANIFEST_CHUNK] File={FileName} Index={Index} Guid={Guid} DestOffset={DestOffset} SrcOffset={SrcOffset} Size={Size}",
                        fileName, i + 1, guid, destinationOffset, sourceOffset, size);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "manifest構造のログ出力中にエラーが発生しました");
        }
    }

    private long GetManifestFileSize(dynamic fileManifest)
    {
        try
        {
            long size;
            if (TryGetNumericMember(fileManifest, "FileSize", out size)) return size;
            if (TryGetNumericMember(fileManifest, "Size", out size)) return size;
            if (TryGetNumericMember(fileManifest, "Length", out size)) return size;
            return 0;
        }
        catch
        {
            return 0;
        }
    }
    
    private long GetChunkSourceOffset(dynamic chunkPart)
    {
        try
        {
            long offset;
            if (TryGetNumericMember(chunkPart, "ChunkOffset", out offset)) return offset;
            if (TryGetNumericMember(chunkPart, "OffsetInChunk", out offset)) return offset;
            if (TryGetNumericMember(chunkPart, "SourceOffset", out offset)) return offset;
            if (TryGetNumericMember(chunkPart, "DataOffset", out offset)) return offset;
            if (TryGetNumericMember(chunkPart, "StartOffset", out offset)) return offset;
            
            Log.Debug("ChunkSourceOffsetが見つかりません、0を使用");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ChunkSourceOffset取得エラー");
            return 0;
        }
    }

    private long ResolveChunkDestinationOffset(object chunkPart, long sequentialFallback)
    {
        try
        {
            long offset;
            if (TryGetNumericMember(chunkPart, "FileOffset", out offset)) return offset;
        if (TryGetNumericMember(chunkPart, "OffsetInFile", out offset)) return offset;
            if (TryGetNumericMember(chunkPart, "TargetOffset", out offset)) return offset;
            if (TryGetNumericMember(chunkPart, "FileDataOffset", out offset)) return offset;
            return sequentialFallback;
        }
        catch
        {
            return sequentialFallback;
        }
    }

    private bool TryGetChunkDestinationOffset(object chunkPart, out long offset)
    {
        offset = 0;
        try
        {
            if (TryGetNumericMember(chunkPart, "FileOffset", out offset)) return true;
            if (TryGetNumericMember(chunkPart, "OffsetInFile", out offset)) return true;
            if (TryGetNumericMember(chunkPart, "TargetOffset", out offset)) return true;
            if (TryGetNumericMember(chunkPart, "FileDataOffset", out offset)) return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    private long GetRawOffset(object chunkPart)
    {
        try
        {
            long offset;
            if (TryGetNumericMember(chunkPart, "Offset", out offset)) return offset;
            return 0;
        }
        catch
        {
            return 0;
        }
    }
    
    private long GetChunkSize(dynamic chunkPart)
    {
        try
        {
            long size;
            if (TryGetNumericMember(chunkPart, "PartSize", out size) && size > 0) return size;
            if (TryGetNumericMember(chunkPart, "ChunkPartSize", out size) && size > 0) return size;
            if (TryGetNumericMember(chunkPart, "FilePartSize", out size) && size > 0) return size;
            if (TryGetNumericMember(chunkPart, "DataSize", out size) && size > 0) return size;
            if (TryGetNumericMember(chunkPart, "Size", out size) && size > 0) return size;
            if (TryGetNumericMember(chunkPart, "Length", out size) && size > 0) return size;
            if (TryGetNumericMember(chunkPart, "ChunkSize", out size) && size > 0) return size;
            
            Log.Debug("ChunkSizeが見つかりません、0を使用");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ChunkSize取得エラー");
            return 0;
        }
    }

    private bool TryGetNumericMember(object source, string memberName, out long value)
    {
        value = 0;
        if (source == null)
        {
            return false;
        }

        if (source is IDictionary<string, object> dict && dict.TryGetValue(memberName, out var dictValue) && dictValue != null)
        {
            try
            {
                value = Convert.ToInt64(dictValue);
                return true;
            }
            catch
            {
            }
        }

        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var type = source.GetType();

        var prop = type.GetProperty(memberName, flags);
        if (prop != null && prop.CanRead)
        {
            var raw = prop.GetValue(source);
            if (raw != null)
            {
                try
                {
                    value = Convert.ToInt64(raw);
                    return true;
                }
                catch
                {
                }
            }
        }

        var field = type.GetField(memberName, flags);
        if (field != null)
        {
            var raw = field.GetValue(source);
            if (raw != null)
            {
                try
                {
                    value = Convert.ToInt64(raw);
                    return true;
                }
                catch
                {
                }
            }
        }

        return false;
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

    private void SetGlobalHttpClientDefaults(string userAgent)
    {
        try
        {
            // .NET HttpClientのグローバルデフォルトを設定
            System.Net.ServicePointManager.DefaultConnectionLimit = 100;
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
        
        if (visited.Contains(obj))
        {
            return;
        }
        
        var type = obj.GetType();

        if (type == typeof(string)) return;

        visited.Add(obj);

        if (obj is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                ReplaceHttpClientsInObjectInternal(item, authenticatedClient, visited);
            }
            
            if (type.Namespace?.StartsWith("System.") == true)
            {
                return;
            }
        }
        
        if (type.IsPrimitive || type == typeof(DateTime) || 
            type == typeof(Guid) || type == typeof(TimeSpan) || type.IsEnum ||
            type.Namespace?.StartsWith("System.") == true)
        {
            return;
        }
        
        try
        {
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
                    else if (field.FieldType.IsClass && !field.FieldType.IsPrimitive && 
                            field.FieldType != typeof(string) && !field.FieldType.IsEnum)
                    {
                        var nestedObj = field.GetValue(obj);
                        if (nestedObj != null)
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
                    else if (property.PropertyType.IsClass && !property.PropertyType.IsPrimitive && 
                            property.PropertyType != typeof(string) && !property.PropertyType.IsEnum &&
                            property.CanRead && property.GetIndexParameters().Length == 0)
                    {
                        var nestedObj = property.GetValue(obj);
                        if (nestedObj != null)
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
                ExecutePacCore(islandFolder, cancellationTokenSource.Token, progressVM, true);
                progressVM.Update(100, "完了しました！");
                Thread.Sleep(1000); // Show "Completed" for a moment
            }, cancellationTokenSource.Token);

            await RefreshArchivesAfterPacAsync();
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

    private void ExecutePacCore(string islandFolder, CancellationToken cancellationToken, PacProgressWindowViewModel progressVM, bool launchUefn)
    {
        var gamePath = UserSettings.Default.GameDirectory;

        int fortniteGameIndex = gamePath.IndexOf("FortniteGame", StringComparison.OrdinalIgnoreCase);
        if (fortniteGameIndex == -1)
        {
            throw new DirectoryNotFoundException("ゲームフォルダ内に'FortniteGame'ディレクトリが見つかりません。設定を確認してください。");
        }

        string fortniteRootPath = gamePath.Substring(0, fortniteGameIndex);
        var contentPaksPath = Path.Combine(fortniteRootPath, "FortniteGame", "Content", "Paks");
        var uefnIslandsExe = Path.Combine(fortniteRootPath, "FortniteGame", "Binaries", "Win64", "UEFN-Islands.exe");
        string[] pluginExtensions = { ".pak", ".sig", ".utoc", ".ucas" };

        foreach (var ext in pluginExtensions)
        {
            var legacyFile = Path.Combine(contentPaksPath, "pakchunk99Island-WindowsClient" + ext);
            if (File.Exists(legacyFile))
            {
                File.Delete(legacyFile);
            }
        }

        progressVM?.Update(10, "プラグインファイルを確認しています...");
        cancellationToken.ThrowIfCancellationRequested();
        var hasAnyPluginFile = pluginExtensions.Any(ext => File.Exists(Path.Combine(islandFolder, "plugin" + ext)));
        if (!hasAnyPluginFile)
        {
            throw new FileNotFoundException("plugin.* ファイルが見つかりません。InstalledBundlesフォルダが正しいか確認してください。", islandFolder);
        }

        progressVM?.Update(30, ".utocから島名を抽出しています...");
        cancellationToken.ThrowIfCancellationRequested();
        var utocPath = Path.Combine(islandFolder, "plugin.utoc");
        if (!File.Exists(utocPath))
        {
            throw new FileNotFoundException("plugin.utoc ファイルが見つかりません。InstalledBundlesフォルダが正しいか確認してください。", utocPath);
        }

        var extractedPluginName = ExtractIslandNameFromUtoc(utocPath, progressVM);

        progressVM?.Update(50, "Paksフォルダにファイルをコピーしています...");
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var ext in pluginExtensions)
        {
            var sourceFile = Path.Combine(islandFolder, "plugin" + ext);
            var destFile = Path.Combine(contentPaksPath, "plugin" + ext);
            if (File.Exists(sourceFile))
            {
                if (File.Exists(destFile)) File.Delete(destFile);
                File.Copy(sourceFile, destFile, true);
            }
        }

        if (!launchUefn)
        {
            return;
        }

        if (File.Exists(uefnIslandsExe) && !string.IsNullOrEmpty(extractedPluginName))
        {
            progressVM?.Update(80, "UEFN-Islandsを起動しています...");
            cancellationToken.ThrowIfCancellationRequested();
            var pieArg = IsPieMode ? ",ValkyriePIE" : "";
            var arguments = $"-disableplugins=\"ValkyrieFortnite,AtomVK\" -enableplugins=\"{extractedPluginName}{pieArg}\"";
            Process.Start(uefnIslandsExe, arguments);
        }
        else
        {
            progressVM?.Update(80, "UEFN-Islands.exeが見つかりません。起動をスキップします。");
            if (progressVM != null)
            {
                Thread.Sleep(2000);
            }
        }
    }

    private bool ValidateExtractedPluginFiles(string folderPath, out string error)
    {
        error = string.Empty;

        var pakPath = Path.Combine(folderPath, "plugin.pak");
        var utocPath = Path.Combine(folderPath, "plugin.utoc");
        var ucasPath = Path.Combine(folderPath, "plugin.ucas");

        if (!File.Exists(pakPath))
        {
            error = "plugin.pak が存在しません";
            return false;
        }

        if (!File.Exists(utocPath))
        {
            error = "plugin.utoc が存在しません";
            return false;
        }

        if (!File.Exists(ucasPath))
        {
            error = "plugin.ucas が存在しません";
            return false;
        }

        if (new FileInfo(pakPath).Length <= 0 || new FileInfo(utocPath).Length <= 0 || new FileInfo(ucasPath).Length <= 0)
        {
            error = "pluginコンテナファイルのサイズが0です";
            return false;
        }

        if (!HasValidUtocMagic(utocPath))
        {
            error = "plugin.utoc のヘッダーが不正です";
            return false;
        }

        return true;
    }

    private static bool HasValidUtocMagic(string utocPath)
    {
        try
        {
            var expectedMagic = new byte[] { 0x2D, 0x3D, 0x3D, 0x2D, 0x2D, 0x3D, 0x3D, 0x2D, 0x2D, 0x3D, 0x3D, 0x2D, 0x2D, 0x3D, 0x3D, 0x2D };
            var buffer = new byte[16];
            using var fs = new FileStream(utocPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Read(buffer, 0, buffer.Length) != buffer.Length)
            {
                return false;
            }

            return buffer.SequenceEqual(expectedMagic);
        }
        catch
        {
            return false;
        }
    }

    private async Task RefreshArchivesAfterPacAsync()
    {
        try
        {
            FLogger.Append(ELog.Information, () => FLogger.Text("アーカイブ一覧を再読み込みしています...", Constants.WHITE, true));

            var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
            if (provider != null)
            {
                await Task.Run(() => provider.Initialize());
            }

            await ApplicationService.ApplicationView.UpdateProvider(true);
            FLogger.Append(ELog.Information, () => FLogger.Text("アーカイブ一覧を更新しました。", Constants.GREEN, true));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "アーカイブ一覧の再読み込みに失敗しました");
            FLogger.Append(ELog.Warning, () => FLogger.Text($"アーカイブ一覧の再読み込みに失敗しました: {ex.Message}", Constants.YELLOW, true));
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
            progressVM?.Update(40, "警告: .utocからプラグイン名を抽出できませんでした。");
            if (progressVM != null)
            {
                Thread.Sleep(2000); // ユーザーがメッセージを読めるように少し待機
            }
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
