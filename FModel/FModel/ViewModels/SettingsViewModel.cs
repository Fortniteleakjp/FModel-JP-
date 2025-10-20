using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.UEFormat.Enums;
using CUE4Parse.UE4.Assets.Exports.Nanite;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Nanite;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.UEFormat.Enums;
using FModel.Framework;
using FModel.Services;
using FModel.Settings;

namespace FModel.ViewModels;

public class SettingsViewModel : ViewModel
{
    private static readonly HttpClient http = new HttpClient();
    private const string ClientAuth = "Basic OThmN2U0MmMyZTNhNGY4NmE3NGViNDNmYmI0MWVkMzk6MGEyNDQ5YTItMDAxYS00NTFlLWFmZWMtM2U4MTI5MDFjNGQ3";

    private static readonly string DeviceAuthPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FModel",
        "deviceAuth.json"
    );

    private readonly DiscordHandler _discordHandler = DiscordService.DiscordHandler;

    private string _authenticationStatus;
    public string AuthenticationStatus
    {
        get => _authenticationStatus;
        set => SetProperty(ref _authenticationStatus, value);
    }

    private string _authenticationUrl;
    public string AuthenticationUrl
    {
        get => _authenticationUrl;
        set => SetProperty(ref _authenticationUrl, value);
    }

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

    private string _outputSnapshot;
    private string _rawDataSnapshot;
    private string _propertiesSnapshot;
    private string _textureSnapshot;
    private string _audioSnapshot;
    private string _modelSnapshot;
    private string _gameSnapshot;
    private string _diffGameSnapshot;
    private ETexturePlatform _uePlatformSnapshot;
    private EGame _ueGameSnapshot;
    private IList<FCustomVersion> _customVersionsSnapshot;
    private IDictionary<string, bool> _optionsSnapshot;
    private IDictionary<string, KeyValuePair<string, string>> _mapStructTypesSnapshot;
    private ELanguage _assetLanguageSnapshot;

    private EGame _diffUeGameSnapshot;
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

    public void Initialize()
    {
        UpdateAuthenticationStatus();

        _outputSnapshot = UserSettings.Default.OutputDirectory;
        _rawDataSnapshot = UserSettings.Default.RawDataDirectory;
        _propertiesSnapshot = UserSettings.Default.PropertiesDirectory;
        _textureSnapshot = UserSettings.Default.TextureDirectory;
        _audioSnapshot = UserSettings.Default.AudioDirectory;
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

    private void UpdateAuthenticationStatus()
    {
        var authData = LoadDeviceAuth();
        AuthenticationStatus = authData != null ? $"認証済み: {authData.DisplayName}" : "未認証";
    }

    public async Task Login()
    {
        try
        {
            AuthenticationUrl = string.Empty;
            var authData = await LoginAsync();
            if (authData != null)
            {
                await File.WriteAllTextAsync(DeviceAuthPath, JsonSerializer.Serialize(authData, new JsonSerializerOptions { WriteIndented = true }));
                UpdateAuthenticationStatus();
            }
        }
        catch (Exception e)
        {
            // Handle login failure
            AuthenticationStatus = $"認証失敗: {e.Message}";
        }
    }

    public void Logout()
    {
        if (File.Exists(DeviceAuthPath))
        {
            File.Delete(DeviceAuthPath);
        }
        UpdateAuthenticationStatus();
    }

    private AuthData? LoadDeviceAuth()
    {
        if (!File.Exists(DeviceAuthPath))
            return null;

        try
        {
            var json = File.ReadAllText(DeviceAuthPath);
            return JsonSerializer.Deserialize<AuthData>(json);
        }
        catch
        {
            return null;
        }
    }

    async Task<AuthData?> LoginAsync()
    {
        var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token")
        {
            Content = new StringContent("grant_type=client_credentials", Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        tokenReq.Headers.Add("Authorization", ClientAuth);
        var tokenResponse = await SendJsonAsync<JsonElement>(tokenReq);
        var accessToken = tokenResponse.GetProperty("access_token").ToString();

        var deviceReq = new HttpRequestMessage(HttpMethod.Post, "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/deviceAuthorization")
        {
            Content = new StringContent("prompt=login", Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        deviceReq.Headers.Add("Authorization", $"Bearer {accessToken}");
        var device = await SendJsonAsync<JsonElement>(deviceReq);

        AuthenticationUrl = device.GetProperty("verification_uri_complete").ToString();

        JsonElement token;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(int.Parse(device.GetProperty("expires_in").ToString()));
        var interval = int.Parse(device.GetProperty("interval").ToString());

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval * 1000);

            try
            {
                var body = $"grant_type=device_code&device_code={device.GetProperty("device_code").ToString()}";
                var req = new HttpRequestMessage(HttpMethod.Post, "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded")
                };
                req.Headers.Add("Authorization", ClientAuth);
                token = await SendJsonAsync<JsonElement>(req);

                if (!token.TryGetProperty("displayName", out var displayName))
                    continue;

                var accountId = token.GetProperty("account_id").ToString();
                var authReq = new HttpRequestMessage(HttpMethod.Post, $"https://account-public-service-prod.ol.epicgames.com/account/api/public/account/{accountId}/deviceAuth");
                authReq.Headers.Add("Authorization", $"Bearer {token.GetProperty("access_token").ToString()}");
                var deviceAuth = await SendJsonAsync<JsonElement>(authReq);

                return new AuthData
                {
                    DisplayName = displayName.ToString(),
                    AccountId = accountId,
                    DeviceId = deviceAuth.GetProperty("deviceId").ToString(),
                    Secret = deviceAuth.GetProperty("secret").ToString(),
                    AccessToken = token.GetProperty("access_token").ToString()
                };
            }
            catch
            {
                // ignore
            }
        }

        throw new Exception("Login timed out.");
    }

    async Task<T> SendJsonAsync<T>(HttpRequestMessage req)
    {
        var res = await http.SendAsync(req);
        var str = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
        {
            throw new Exception($"HTTP {(int)res.StatusCode} {res.ReasonPhrase}\n{str}");
        }
        return JsonSerializer.Deserialize<T>(str)
            ?? throw new JsonException($"Failed to deserialize JSON to {typeof(T).Name}");
    }
}

public class AuthData
{
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("accountId")] public string AccountId { get; set; } = string.Empty;
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("secret")] public string Secret { get; set; } = string.Empty;
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
}
