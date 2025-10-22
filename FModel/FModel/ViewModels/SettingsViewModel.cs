using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports.Material;
using FModel.ViewModels.ApiEndpoints.Models; // AuthResponse を使用するために追加
using CUE4Parse.UE4.Assets.Exports.Texture;
using System.Windows.Input; // For ICommand
using System.Windows.Media; // For Brush
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.UEFormat.Enums;
using CUE4Parse.UE4.Assets.Exports.Nanite;
using FModel.Framework;
using FModel.Services;
using FModel.Settings;
using FModel.Views.Resources.Controls;

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
        // Initialize commands
        AuthenticateEpicGamesCommand = new RelayCommand(AuthenticateEpicGames, CanAuthenticateEpicGames);
        GetAesKeyCommand = new RelayCommand(GetAesKey, CanGetAesKey);
        CopyAesKeyCommand = new RelayCommand(CopyAesKey, CanCopyAesKey);
        SaveAesKeyCommand = new RelayCommand(SaveAesKey, CanSaveAesKey);

        // Initialize settings and UI elements
        Initialize();
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
    private void InitializeAuthStatus()
    {
        // This method would typically check if a valid authentication token already exists
        // and update EpicAuthStatusText and EpicAuthStatusForeground accordingly.
        // For now, it defaults to "Not Authenticated".
        if (UserSettings.Default.LastAuthResponse != null && UserSettings.Default.LastAuthResponse.ExpiresAt > DateTime.Now)
        {
            EpicAuthStatusText = "Authenticated";
            EpicAuthStatusForeground = Brushes.Green;
        }
        else
        {
            EpicAuthStatusText = "Not Authenticated";
            EpicAuthStatusForeground = Brushes.Red;
        }
    }

    private void AuthenticateEpicGames(object? parameter)
    {
        // Placeholder: In a real application, this would initiate an OAuth flow,
        // likely opening a browser window for the user to log in to Epic Games.
        // Upon successful authentication, an access token would be received.
        EpicAuthStatusText = "Authenticating...";
        EpicAuthStatusForeground = Brushes.Orange;

        // Simulate authentication success (replace with actual OAuth logic)
        // For demonstration, let's assume authentication is successful after a delay
        // and a dummy token is obtained.
        // This would typically involve calling an external service or opening a browser.
        // Example:
        // var authResult = await EpicGamesAuthService.Authenticate();
        // if (authResult.IsSuccess)
        // {
        //     UserSettings.Default.LastAuthResponse = authResult.AuthResponse;
        //     EpicAuthStatusText = "Authenticated";
        //     EpicAuthStatusForeground = Brushes.Green;
        //     Append(Information, () => Text("Epic Games authenticated successfully."));
        // }
        // else
        // {
        //     EpicAuthStatusText = "Authentication Failed";
        //     EpicAuthStatusForeground = Brushes.Red;
        //     Append(Error, () => Text($"Epic Games authentication failed: {authResult.ErrorMessage}"));
        // }
        // For now, just update status:
        EpicAuthStatusText = "Authenticated (Placeholder)";
        EpicAuthStatusForeground = Brushes.Green;
        UserSettings.Default.LastAuthResponse = new AuthResponse { AccessToken = "DUMMY_TOKEN", ExpiresAt = DateTime.Now.AddHours(1) };
        ((RelayCommand)AuthenticateEpicGamesCommand).RaiseCanExecuteChanged();
        ((RelayCommand)GetAesKeyCommand).RaiseCanExecuteChanged();
    }

    private bool CanAuthenticateEpicGames(object? parameter)
    {
        return UserSettings.Default.LastAuthResponse == null || UserSettings.Default.LastAuthResponse.ExpiresAt <= DateTime.Now;
    }

    private void GetAesKey(object? parameter)
    {
        // Placeholder: This would call an API (e.g., from AES-Grabber's backend or a public API)
        // using the MapCodeInput and the authentication token.
        RetrievedAesKey = "Retrieving AES Key...";
        // Example:
        // var aesKey = await AesRetrievalService.GetAesKeyFromMapCode(MapCodeInput, UserSettings.Default.LastAuthResponse.AccessToken);
        // if (!string.IsNullOrEmpty(aesKey))
        // {
        //     RetrievedAesKey = aesKey;
        //     Append(Information, () => Text($"AES Key retrieved for map code '{MapCodeInput}'."));
        // }
        // else
        // {
        //     RetrievedAesKey = "Failed to retrieve AES Key. Check map code or authentication.";
        //     Append(Error, () => Text($"Failed to retrieve AES Key for map code '{MapCodeInput}'."));
        // }
        // For now, simulate retrieval:
        RetrievedAesKey = $"0x1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF (for map: {MapCodeInput})";
        ((RelayCommand)CopyAesKeyCommand).RaiseCanExecuteChanged();
        ((RelayCommand)SaveAesKeyCommand).RaiseCanExecuteChanged();
    }

    private bool CanGetAesKey(object? parameter)
    {
        return !string.IsNullOrWhiteSpace(MapCodeInput) &&
               UserSettings.Default.LastAuthResponse != null &&
               UserSettings.Default.LastAuthResponse.ExpiresAt > DateTime.Now;
    }

    private void CopyAesKey(object? parameter)

    {
        if (CanCopyAesKey(parameter))
        {
            System.Windows.Clipboard.SetText(RetrievedAesKey);
            FLogger.Append(ELog.Information, () => FLogger.Text("AES Key copied to clipboard.", Constants.WHITE, true));
        }
    }
    
    private bool CanCopyAesKey(object? parameter)
    {
        return !string.IsNullOrWhiteSpace(RetrievedAesKey) &&
               !RetrievedAesKey.StartsWith("Retrieving") &&
               !RetrievedAesKey.StartsWith("Failed");
    }

    private void SaveAesKey(object? parameter)
    {
        if (CanSaveAesKey(parameter))
        {
            // Placeholder: Logic to save the AES key, e.g., to a file or UserSettings.Default.CurrentDir.AesKeys
            // For now, just log it.
            FLogger.Append(ELog.Information, () => FLogger.Text($"AES Key saved: {RetrievedAesKey}", Constants.WHITE, true));
        }
    }

    private bool CanSaveAesKey(object? parameter)
    {
        return !string.IsNullOrWhiteSpace(RetrievedAesKey) &&
               !RetrievedAesKey.StartsWith("Retrieving") &&
               !RetrievedAesKey.StartsWith("Failed");
    }
}
