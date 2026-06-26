using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AdonisUI.Controls;
using Microsoft.Win32;
using CUE4Parse;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.GameTypes.AshEchoes.FileProvider;
using CUE4Parse.GameTypes.KRD.Assets.Exports;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap; // 上流同期: FileUsmapTypeMappingsProvider が Usmap/ サブ名前空間へ移動
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.CriWare;
using CUE4Parse.UE4.Assets.Exports.Fmod;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports.Verse;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse.UE4.BinaryConfig;
using CUE4Parse.UE4.CriWare;
using CUE4Parse.UE4.CriWare.Readers;
using CUE4Parse.UE4.FMod;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.Engine.Animation;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Objects.UObject.Editor;
using CUE4Parse.UE4.Oodle.Objects;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Shaders;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Wwise;
using CUE4Parse.Utils;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Sounds;
using EpicManifestParser;
using EpicManifestParser.UE;
using EpicManifestParser.ZlibngDotNetDecompressor;
using FModel.Creator;
using FModel.Extensions;
using FModel.Framework;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels;
using FModel.Views;
using FModel.Views.Resources.Controls;
using FModel.Views.Snooper;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using K4os.Compression.LZ4.Streams;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Serilog;
using SkiaSharp;
using Svg.Skia;
using UE4Config.Parsing;
using Application = System.Windows.Application;
using FGuid = CUE4Parse.UE4.Objects.Core.Misc.FGuid;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxButton = AdonisUI.Controls.MessageBoxButton;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;
using K4os.Compression.LZ4;

namespace FModel.ViewModels.CUE4Parse;

public partial class CUE4ParseViewModel : ViewModel
{

    private ThreadWorkerViewModel _threadWorkerView => ApplicationService.ThreadWorkerView;
    private ApiEndpointViewModel _apiEndpointView => ApplicationService.ApiEndpointView;
    private readonly Regex _fnLiveRegex = new(@"^FortniteGame[/\\]Content[/\\]Paks[/\\]",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);




    public AbstractVfsFileProvider Provider { get; set; }
    public AbstractVfsFileProvider DiffProvider { get; set; }
    public GameDirectoryViewModel GameDirectory { get; set; }
    public AssetsFolderViewModel AssetsFolder { get; set; }
    public SearchViewModel SearchVm { get; set; }
    public TabControlViewModel TabControl { get; set; }
    public ConfigIni IoStoreOnDemand { get; set; }
    private Lazy<WwiseProvider> _wwiseProviderLazy;
    public WwiseProvider WwiseProvider => _wwiseProviderLazy.Value;
    private Lazy<FModProvider> _fmodProviderLazy;
    public FModProvider FmodProvider => _fmodProviderLazy?.Value;
    private Lazy<CriWareProvider> _criWareProviderLazy;
    public CriWareProvider CriWareProvider => _criWareProviderLazy?.Value;
    public ConcurrentBag<string> UnknownExtensions = [];
    public System.Windows.Input.ICommand ComparePakCommand { get; }
    public System.Windows.Input.ICommand CreateLoliBackupCommand { get; }

    public CUE4ParseViewModel()
    {
        var currentDir = UserSettings.Default.CurrentDir;
        var gameDirectory = currentDir.GameDirectory;
        var versionContainer = new VersionContainer(
            game: currentDir.UeVersion, platform: currentDir.TexturePlatform,
            customVersions: new FCustomVersionContainer(currentDir.Versioning.CustomVersions),
            optionOverrides: currentDir.Versioning.Options,
            mapStructTypesOverrides: currentDir.Versioning.MapStructTypes);
        var pathComparer = StringComparer.OrdinalIgnoreCase;

        switch (gameDirectory)
        {
            case Constants._FN_LIVE_TRIGGER:
            {
                Provider = new StreamedFileProvider("FortniteLive", versionContainer, pathComparer);
                break;
            }
            case Constants._VAL_LIVE_TRIGGER:
            {
                Provider = new StreamedFileProvider("ValorantLive", versionContainer, pathComparer);
                break;
            }
            default:
            {
                var project = gameDirectory.SubstringBeforeLast(gameDirectory.Contains("eFootball") ? "\\pak" : "\\Content").SubstringAfterLast("\\");
                Provider = project switch
                {
                    "StateOfDecay2" => new DefaultFileProvider(new DirectoryInfo(gameDirectory),
                    [
                        new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\StateOfDecay2\\Saved\\Paks"),
                        new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\StateOfDecay2\\Saved\\DisabledPaks")
                    ], SearchOption.AllDirectories, versionContainer, pathComparer),
                    "eFootball" => new DefaultFileProvider(new DirectoryInfo(gameDirectory),
                    [
                        new(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) + "\\KONAMI\\eFootball\\ST\\Download")
                    ], SearchOption.AllDirectories, versionContainer, pathComparer),
                    _ when versionContainer.Game is EGame.GAME_AshEchoes => new AEDefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer, pathComparer),
                    _ when versionContainer.Game is EGame.GAME_BlackStigma => new DefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer, StringComparer.Ordinal),
                    _ => new DefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer, pathComparer)
                };
                if (!Directory.Exists(gameDirectory) && Provider is DefaultFileProvider)
                {
                    Log.Warning($"Game directory '{gameDirectory}' not found. Skipping DefaultFileProvider initialization.");
                    Provider = null;
                }
                break;
            }
        }

        if (Provider != null)
        {
            Provider.ReadScriptData = UserSettings.Default.ReadScriptData;
            Provider.ReadShaderMaps = UserSettings.Default.ReadShaderMaps;
            Provider.ReadNaniteData = true;
        }

        // DiffProviderの初期化
        if (UserSettings.Default.DiffDir != null)
        {
            DiffProvider = CreateDiffProvider(UserSettings.Default.DiffDir);
            if (DiffProvider != null)
            {
                DiffProvider.ReadScriptData = UserSettings.Default.ReadScriptData;
                DiffProvider.ReadShaderMaps = UserSettings.Default.ReadShaderMaps;
                DiffProvider.ReadNaniteData = true;
            }
        }

        GameDirectory = new GameDirectoryViewModel();
        AssetsFolder = new AssetsFolderViewModel();
        SearchVm = new SearchViewModel();
        TabControl = new TabControlViewModel();
        IoStoreOnDemand = new ConfigIni(nameof(IoStoreOnDemand));

        // DiffProvider生成用のローカル関数
        AbstractVfsFileProvider CreateDiffProvider(DirectorySettings dir)
        {
            var gameDirectory = dir.GameDirectory;
            var versionContainer = new VersionContainer(
                game: dir.UeVersion,
                platform: dir.TexturePlatform,
                customVersions: new FCustomVersionContainer(dir.Versioning.CustomVersions),
                optionOverrides: dir.Versioning.Options,
                mapStructTypesOverrides: dir.Versioning.MapStructTypes
            );
            var pathComparer = StringComparer.OrdinalIgnoreCase;

            switch (gameDirectory)
            {
                case Constants._FN_LIVE_TRIGGER:
                    return new StreamedFileProvider("FortniteLive", versionContainer, pathComparer);
                case Constants._VAL_LIVE_TRIGGER:
                    return new StreamedFileProvider("ValorantLive", versionContainer, pathComparer);
                default:
                    {
                        var project = gameDirectory.SubstringBeforeLast(gameDirectory.Contains("eFootball") ? "\\pak" : "\\Content").SubstringAfterLast("\\");
                        AbstractVfsFileProvider resultProvider = project switch
                        {
                            "StateOfDecay2" => new DefaultFileProvider(new DirectoryInfo(gameDirectory),
                                [
                                    new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\StateOfDecay2\\Saved\\Paks"),
                                    new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\StateOfDecay2\\Saved\\DisabledPaks")
                                ], SearchOption.AllDirectories, versionContainer, pathComparer),
                            "eFootball" => new DefaultFileProvider(new DirectoryInfo(gameDirectory),
                                [
                                    new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) + "\\KONAMI\\eFootball\\ST\\Download")
                                ], SearchOption.AllDirectories, versionContainer, pathComparer),
                            _ when versionContainer.Game is EGame.GAME_AshEchoes => new AEDefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer, pathComparer),
                            _ when versionContainer.Game is EGame.GAME_BlackStigma => new DefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer, StringComparer.Ordinal),
                            _ => new DefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer, pathComparer)
                        };
                        if (!Directory.Exists(gameDirectory) && resultProvider is DefaultFileProvider)
                        {
                            Log.Warning($"Game directory '{gameDirectory}' not found. Skipping DefaultFileProvider initialization.");
                            return null;
                        }
                        return resultProvider;
                    }
            }
        }

        ComparePakCommand = new RelayCommand(_ => ComparePak());
        CreateLoliBackupCommand = new RelayCommand(async _ => await CreateLoliBackup());
    }



















    private static bool HasFlag(EBulkType a, EBulkType b)
    {
        return (a & b) == b;
    }


}
