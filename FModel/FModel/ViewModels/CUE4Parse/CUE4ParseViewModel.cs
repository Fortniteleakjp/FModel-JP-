using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Sounds;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.GameTypes.KRD.Assets.Exports;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports.Verse;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.GameplayTags;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Oodle.Objects;
using CUE4Parse.UE4.Shaders;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Wwise;
using CUE4Parse.Utils;
using FGuid = CUE4Parse.UE4.Objects.Core.Misc.FGuid;
using FModel.Creator;
using FModel.Extensions;
using FModel.Framework;
using FModel.Services;
using FModel.Settings;
using FModel.Views;
using FModel.Views.Resources.Controls;
using FModel.Views.Snooper;
using Newtonsoft.Json;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Serilog;
using SkiaSharp;
using System.Linq;
using System.Text;
using UE4Config.Parsing;
using Application = System.Windows.Application;

namespace FModel.ViewModels.CUE4Parse;

public partial class CUE4ParseViewModel : ViewModel
{
    private ThreadWorkerViewModel _threadWorkerView => ApplicationService.ThreadWorkerView;
    private ApiEndpointViewModel _apiEndpointView => ApplicationService.ApiEndpointView;
    private readonly Regex _fnLiveRegex = new(@"^FortniteGame[/\\]Content[/\\]Paks[/\\]",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private bool _modelIsOverwritingMaterial;
    public bool ModelIsOverwritingMaterial
    {
        get => _modelIsOverwritingMaterial;
        set => SetProperty(ref _modelIsOverwritingMaterial, value);
    }

    public bool IsSnooperOpen => _snooper is { Exists: true, IsVisible: true };
    private Snooper _snooper;
    public Snooper SnooperViewer
    {
        get
        {
            if (_snooper != null)
                return _snooper;

            return Application.Current.Dispatcher.Invoke(delegate
            {
                var scale = ImGuiController.GetDpiScale();
                var htz = Snooper.GetMaxRefreshFrequency();
                return _snooper = new Snooper(
                    new GameWindowSettings { UpdateFrequency = htz },
                    new NativeWindowSettings
                    {
                        ClientSize = new OpenTK.Mathematics.Vector2i(
                            Convert.ToInt32(SystemParameters.MaximizedPrimaryScreenWidth * .75 * scale),
                            Convert.ToInt32(SystemParameters.MaximizedPrimaryScreenHeight * .85 * scale)),
                        NumberOfSamples = Constants.SAMPLES_COUNT,
                        WindowBorder = WindowBorder.Resizable,
                        Flags = ContextFlags.ForwardCompatible,
                        Profile = ContextProfile.Core,
                        Vsync = VSyncMode.Adaptive,
                        APIVersion = new Version(4, 6),
                        StartVisible = false,
                        StartFocused = false,
                        Title = "3D Viewer"
                    });
            });
        }
    }

    public AbstractVfsFileProvider Provider { get; }
    public AbstractVfsFileProvider DiffProvider { get; }
    public GameDirectoryViewModel GameDirectory { get; }
    public GameDirectoryViewModel DiffGameDirectory { get; }
    public AssetsFolderViewModel AssetsFolder { get; }
    public SearchViewModel SearchVm { get; }
    public TabControlViewModel TabControl { get; }
    public ConfigIni IoStoreOnDemand { get; }

    public CUE4ParseViewModel()
    {
        Provider = CreateProvider(UserSettings.Default.CurrentDir, _fnLiveRegex);

        if (UserSettings.Default.DiffDir != null)
        {
            DiffProvider = CreateProvider(UserSettings.Default.DiffDir, _fnLiveRegex);
            DiffGameDirectory = new GameDirectoryViewModel();
        }

        RefreshReadSettings();

        GameDirectory = new GameDirectoryViewModel();
        AssetsFolder = new AssetsFolderViewModel();
        SearchVm = new SearchViewModel();
        TabControl = new TabControlViewModel();
        IoStoreOnDemand = new ConfigIni(nameof(IoStoreOnDemand));
    }

    public async Task InitInformation()
    {
        await _threadWorkerView.Begin(cancellationToken =>
        {
            var info = _apiEndpointView.FModelApi.GetNews(cancellationToken, Provider.ProjectName);
            if (info == null)
                return;

            FLogger.Append(ELog.None, () =>
            {
                for (var i = 0; i < info.Messages.Length; i++)
                {
                    FLogger.Text(info.Messages[i], info.Colors[i], bool.Parse(info.NewLines[i]));
                }
            });
        });
    }

    public Task VerifyConsoleVariables()
    {
        if (Provider.Versions["StripAdditiveRefPose"])
        {
            FLogger.Append(ELog.Warning, () =>
                FLogger.Text("Additive animations have their reference pose stripped, which will lead to inaccurate preview and export", Constants.WHITE, true));
        }

        if (Provider.Versions.Game is EGame.GAME_UE4_LATEST or EGame.GAME_UE5_LATEST && !Provider.ProjectName.Equals("FortniteGame", StringComparison.OrdinalIgnoreCase)) // ignore fortnite globally
        {
            FLogger.Append(ELog.Warning, () =>
                FLogger.Text($"Experimental UE version selected, likely unsuitable for '{Provider.GameDisplayName ?? Provider.ProjectName}'", Constants.WHITE, true));
        }

        return Task.CompletedTask;
    }

    public Task VerifyOnDemandArchives()
    {
        // only local fortnite
        if (Provider is not DefaultFileProvider || !Provider.ProjectName.Equals("FortniteGame", StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        // scuffed but working
        var persistentDownloadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame/Saved/PersistentDownloadDir");
        var iasFileInfo = new FileInfo(Path.Combine(persistentDownloadDir, "ias", "ias.cache.0"));
        if (!iasFileInfo.Exists || iasFileInfo.Length == 0)
            return Task.CompletedTask;

        return Task.Run(async () =>
        {
            var inst = new List<InstructionToken>();
            IoStoreOnDemand.FindPropertyInstructions("Endpoint", "TocPath", inst);
            if (inst.Count <= 0)
                return;

            var ioStoreOnDemandPath = Path.Combine(UserSettings.Default.GameDirectory, "..\\..\\..\\Cloud", inst[0].Value.SubstringAfterLast("/").SubstringBefore("\""));
            if (!File.Exists(ioStoreOnDemandPath))
                return;

            await _apiEndpointView.EpicApi.VerifyAuth(default);
            await Provider.RegisterVfs(new IoChunkToc(ioStoreOnDemandPath), new IoStoreOnDemandOptions
            {
                ChunkBaseUri = new Uri("https://download.epicgames.com/ias/fortnite/", UriKind.Absolute),
                ChunkCacheDirectory = Directory.CreateDirectory(Path.Combine(UserSettings.Default.OutputDirectory, ".data")),
                Authorization = new AuthenticationHeaderValue("Bearer", UserSettings.Default.LastAuthResponse.AccessToken),
                Timeout = TimeSpan.FromSeconds(30)
            });
            var onDemandCount = await Provider.MountAsync();
            FLogger.Append(ELog.Information, () =>
                FLogger.Text($"{onDemandCount} on-demand archive{(onDemandCount > 1 ? "s" : "")} streamed via epicgames.com", Constants.WHITE, true));
        });
    }

    public void ExtractSelected(CancellationToken cancellationToken, IEnumerable<GameFile> assetItems)
    {
        foreach (var entry in assetItems)
        {
            Thread.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            Extract(cancellationToken, entry, TabControl.HasNoTabs);
        }
    }

    private void BulkFolder(CancellationToken cancellationToken, TreeItem folder, Action<GameFile> action)
    {
        foreach (var entry in folder.AssetsList.Assets)
        {
            Thread.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                action(entry);
            }
            catch
            {
                // ignore
            }
        }

        foreach (var f in folder.Folders)
            BulkFolder(cancellationToken, f, action);
    }

    public void ExportFolder(CancellationToken cancellationToken, TreeItem folder)
    {
        Parallel.ForEach(folder.AssetsList.Assets, entry =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExportData(entry, false);
        });

        foreach (var f in folder.Folders)
            ExportFolder(cancellationToken, f);
    }

    public void ExtractFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs));

    public void SaveFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Properties | EBulkType.Auto));

    public void TextureFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Textures | EBulkType.Auto));

    public void ModelFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Meshes | EBulkType.Auto));

    public void AnimationFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Animations | EBulkType.Auto));

    public void Extract(CancellationToken cancellationToken, GameFile entry, bool addNewTab = false, EBulkType bulk = EBulkType.None)
    {
        Log.Information("User DOUBLE-CLICKED to extract '{FullPath}'", entry.Path);

        if (addNewTab && TabControl.CanAddTabs)
            TabControl.AddTab(entry);
        else
            TabControl.SelectedTab.SoftReset(entry);
        TabControl.SelectedTab.DiffContent = null;
        TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector(entry.Extension);

        var updateUi = !HasFlag(bulk, EBulkType.Auto);
        var saveProperties = HasFlag(bulk, EBulkType.Properties);
        var saveTextures = HasFlag(bulk, EBulkType.Textures);
        switch (entry.Extension)
        {
            case "uasset":
            case "umap":
                {
                    var result = Provider.GetLoadPackageResult(entry);
                    TabControl.SelectedTab.TitleExtra = result.TabTitleExtra;

                    if (saveProperties || updateUi)
                    {
                        TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(result.GetDisplayData(saveProperties), Formatting.Indented), saveProperties, updateUi);
                        if (saveProperties)
                            break; // do not search for viewable exports if we are dealing with jsons
                    }

                    for (var i = result.InclusiveStart; i < result.ExclusiveEnd; i++)
                    {
                        if (CheckExport(cancellationToken, result.Package, i, bulk))
                            break;
                    }

                    break;
                }
            case "upluginmanifest":
            case "projectstore":
            case "uproject":
            case "manifest":
            case "uplugin":
            case "archive":
            case "dnearchive": // Banishers: Ghosts of New Eden
            case "gitignore":
            case "vmodule":
            case "uparam": // Steel Hunters
            case "verse":
            case "html":
            case "json":
            case "ini":
            case "txt":
            case "log":
            case "lsd": // Days Gone
            case "bat":
            case "dat":
            case "cfg":
            case "ddr":
            case "ide":
            case "ipl":
            case "zon":
            case "xml":
            case "css":
            case "csv":
            case "pem":
            case "tps":
            case "tgc": // State of Decay 2
            case "lua":
            case "js":
            case "po":
            case "md":
            case "h":
                {
                    var data = Provider.SaveAsset(entry);
                    using var stream = new MemoryStream(data) { Position = 0 };
                    using var reader = new StreamReader(stream);

                    TabControl.SelectedTab.SetDocumentText(reader.ReadToEnd(), saveProperties, updateUi);

                    break;
                }
            case "locmeta":
                {
                    var archive = entry.CreateReader();
                    var metadata = new FTextLocalizationMetaDataResource(archive);
                    TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(metadata, Formatting.Indented), saveProperties, updateUi);

                    break;
                }
            case "locres":
                {
                    var archive = entry.CreateReader();
                    var locres = new FTextLocalizationResource(archive);
                    TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(locres, Formatting.Indented), saveProperties, updateUi);

                    break;
                }
            case "bin" when entry.Name.Contains("AssetRegistry", StringComparison.OrdinalIgnoreCase):
                {
                    var archive = entry.CreateReader();
                    var registry = new FAssetRegistryState(archive);
                    TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(registry, Formatting.Indented), saveProperties, updateUi);

                    break;
                }
            case "bin" when entry.Name.Contains("GlobalShaderCache", StringComparison.OrdinalIgnoreCase):
                {
                    var archive = entry.CreateReader();
                    var registry = new FGlobalShaderCache(archive);
                    TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(registry, Formatting.Indented), saveProperties, updateUi);

                    break;
                }
            case "bnk":
            case "pck":
                {
                    var archive = entry.CreateReader();
                    var wwise = new WwiseReader(archive);
                    TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(wwise, Formatting.Indented), saveProperties, updateUi);
                    foreach (var (name, data) in wwise.WwiseEncodedMedias)
                    {
                        SaveAndPlaySound(entry.Path.SubstringBeforeWithLast('/') + name, "WEM", data);
                    }

                    break;
                }
            case "xvag":
            case "at9":
            case "wem":
                {
                    var data = Provider.SaveAsset(entry);
                    SaveAndPlaySound(entry.PathWithoutExtension, entry.Extension, data);

                    break;
                }
            case "udic":
                {
                    var archive = entry.CreateReader();
                    var header = new FOodleDictionaryArchive(archive).Header;
                    TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(header, Formatting.Indented), saveProperties, updateUi);

                    break;
                }
            case "png":
            case "jpg":
            case "bmp":
                {
                    var data = Provider.SaveAsset(entry);
                    using var stream = new MemoryStream(data) { Position = 0 };
                    TabControl.SelectedTab.AddImage(entry.NameWithoutExtension, false, SKBitmap.Decode(stream), saveTextures, updateUi);

                    break;
                }
            case "svg":
                {
                    var data = Provider.SaveAsset(entry);
                    using var stream = new MemoryStream(data) { Position = 0 };
                    var svg = new SkiaSharp.Extended.Svg.SKSvg(new SKSize(512, 512));
                    var bitmap = RenderSvg(stream);

                    TabControl.SelectedTab.AddImage(entry.NameWithoutExtension, false, bitmap, saveTextures, updateUi);

                    break;
                }
            case "ufont":
            case "otf":
            case "ttf":
                FLogger.Append(ELog.Warning, () =>
                    FLogger.Text($"Export '{entry.Name}' raw data and change its extension if you want it to be an installable font file", Constants.WHITE, true));
                break;
            case "ushaderbytecode":
            case "ushadercode":
                {
                    var archive = entry.CreateReader();
                    var ar = new FShaderCodeArchive(archive);
                    TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(ar, Formatting.Indented), saveProperties, updateUi);

                    break;
                }
            case "upipelinecache":
                {
                    var archive = entry.CreateReader();
                    var ar = new FPipelineCacheFile(archive);
                    TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(ar, Formatting.Indented), saveProperties, updateUi);

                    break;
                }
            case "res": // just skip
                break;
            default:
                {
                    FLogger.Append(ELog.Warning, () =>
                        FLogger.Text($"The package '{entry.Name}' is of an unknown type.", Constants.WHITE, true));
                    break;
                }
        }
    }

    public void ExtractAndScroll(CancellationToken cancellationToken, string fullPath, string objectName, string parentExportType)
    {
        Log.Information("User CTRL-CLICKED to extract '{FullPath}'", fullPath);

        var entry = Provider[fullPath];
        TabControl.AddTab(entry, parentExportType);
        TabControl.SelectedTab.ScrollTrigger = objectName;

        var result = Provider.GetLoadPackageResult(entry, objectName);

        TabControl.SelectedTab.TitleExtra = result.TabTitleExtra;
        TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector(""); // json
        TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(result.GetDisplayData(), Formatting.Indented), false, false);

        for (var i = result.InclusiveStart; i < result.ExclusiveEnd; i++)
        {
            if (CheckExport(cancellationToken, result.Package, i))
                break;
        }
    }

    private bool CheckExport(CancellationToken cancellationToken, IPackage pkg, int index, EBulkType bulk = EBulkType.None) // return true once you wanna stop searching for exports
    {
        var isNone = bulk == EBulkType.None;
        var updateUi = !HasFlag(bulk, EBulkType.Auto);
        var saveTextures = HasFlag(bulk, EBulkType.Textures);

        var pointer = new FPackageIndex(pkg, index + 1).ResolvedObject;
        if (pointer?.Object is null)
            return false;

        var dummy = ((AbstractUePackage) pkg).ConstructObject(pointer.Class?.Object?.Value as UStruct, pkg);
        switch (dummy)
        {
            case UVerseDigest when isNone && pointer.Object.Value is UVerseDigest verseDigest:
                {
                    if (!TabControl.CanAddTabs)
                        return false;

                    TabControl.AddTab($"{verseDigest.ProjectName}.verse");
                    TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("verse");
                    TabControl.SelectedTab.SetDocumentText(verseDigest.ReadableCode, false, false);
                    return true;
                }
            case UTexture when (isNone || saveTextures) && pointer.Object.Value is UTexture texture:
                {
                    TabControl.SelectedTab.AddImage(texture, saveTextures, updateUi);
                    return false;
                }
            case USvgAsset when (isNone || saveTextures) && pointer.Object.Value is USvgAsset svgasset:
                {
                    var data = svgasset.GetOrDefault<byte[]>("SvgData");
                    var sourceFile = svgasset.GetOrDefault<string>("SourceFile");
                    using var stream = new MemoryStream(data);
                    stream.Position = 0;
                    var bitmap = RenderSvg(stream);

                    if (saveTextures)
                    {
                        var fileName = sourceFile.SubstringAfterLast('/');
                        var path = Path.Combine(UserSettings.Default.TextureDirectory,
                            UserSettings.Default.KeepDirectoryStructure ? TabControl.SelectedTab.Entry.Directory : "", fileName!).Replace('\\', '/');

                        Directory.CreateDirectory(path.SubstringBeforeLast('/'));

                        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                        fs.Write(data, 0, data.Length);
                        if (File.Exists(path))
                        {
                            Log.Information("{FileName} successfully saved", fileName);
                            if (updateUi)
                            {
                                FLogger.Append(ELog.Information, () =>
                                {
                                    FLogger.Text("Successfully saved ", Constants.WHITE);
                                    FLogger.Link(fileName, path, true);
                                });
                            }
                        }
                        else
                        {
                            Log.Error("{FileName} could not be saved", fileName);
                            if (updateUi)
                                FLogger.Append(ELog.Error, () => FLogger.Text($"Could not save '{fileName}'", Constants.WHITE, true));
                        }
                    }

                    TabControl.SelectedTab.AddImage(sourceFile.SubstringAfterLast('/'), false, bitmap, false, updateUi);
                    return false;
                }
            case UAkAudioEvent when isNone && pointer.Object.Value is UAkAudioEvent { EventCookedData: { } wwiseData }:
                {
                    foreach (var kvp in wwiseData.EventLanguageMap)
                    {
                        if (!kvp.Value.HasValue)
                            continue;

                        foreach (var media in kvp.Value.Value.Media)
                        {
                            if (!Provider.TrySaveAsset(Path.Combine("Game/WwiseAudio/", media.MediaPathName.Text), out var data))
                                continue;

                            var namedPath = string.Concat(
                                Provider.ProjectName, "/Content/WwiseAudio/",
                                media.DebugName.Text.SubstringBeforeLast('.').Replace('\\', '/'),
                                " (", kvp.Key.LanguageName.Text, ")");
                            SaveAndPlaySound(namedPath, media.MediaPathName.Text.SubstringAfterLast('.'), data);
                        }
                    }
                    return false;
                }
            case UAkMediaAssetData when isNone:
            case USoundWave when isNone:
                {
                    var shouldDecompress = UserSettings.Default.CompressedAudioMode == ECompressedAudio.PlayDecompressed;
                    pointer.Object.Value.Decode(shouldDecompress, out var audioFormat, out var data);
                    var hasAf = !string.IsNullOrEmpty(audioFormat);
                    if (data == null || !hasAf)
                    {
                        if (hasAf)
                            FLogger.Append(ELog.Warning, () => FLogger.Text($"Unsupported audio format '{audioFormat}'", Constants.WHITE, true));
                        return false;
                    }

                    SaveAndPlaySound(TabControl.SelectedTab.Entry.PathWithoutExtension.Replace('\\', '/'), audioFormat, data);
                    return false;
                }
            case UWorld when isNone && UserSettings.Default.PreviewWorlds:
            case UBlueprintGeneratedClass when isNone && UserSettings.Default.PreviewWorlds && TabControl.SelectedTab.ParentExportType switch
            {
                "JunoBuildInstructionsItemDefinition" => true,
                "JunoBuildingSetAccountItemDefinition" => true,
                "JunoBuildingPropAccountItemDefinition" => true,
                _ => false
            }:
            case UPaperSprite when isNone && UserSettings.Default.PreviewMaterials:
            case UStaticMesh when isNone && UserSettings.Default.PreviewStaticMeshes:
            case USkeletalMesh when isNone && UserSettings.Default.PreviewSkeletalMeshes:
            case USkeleton when isNone && UserSettings.Default.SaveSkeletonAsMesh:
            case UMaterialInstance when isNone && UserSettings.Default.PreviewMaterials && !ModelIsOverwritingMaterial &&
                                        !(Provider.ProjectName.Equals("FortniteGame", StringComparison.OrdinalIgnoreCase) &&
                                          (pkg.Name.Contains("/MI_OfferImages/", StringComparison.OrdinalIgnoreCase) ||
                                           pkg.Name.Contains("/RenderSwitch_Materials/", StringComparison.OrdinalIgnoreCase) ||
                                           pkg.Name.Contains("/MI_BPTile/", StringComparison.OrdinalIgnoreCase))):
                {
                    if (SnooperViewer.TryLoadExport(cancellationToken, dummy, pointer.Object))
                        SnooperViewer.Run();
                    return true;
                }
            case UMaterialInstance when isNone && ModelIsOverwritingMaterial && pointer.Object.Value is UMaterialInstance m:
                {
                    SnooperViewer.Renderer.Swap(m);
                    SnooperViewer.Run();
                    return true;
                }
            case UAnimSequenceBase when isNone && UserSettings.Default.PreviewAnimations || SnooperViewer.Renderer.Options.ModelIsWaitingAnimation:
                {
                    // animate all animations using their specified skeleton or when we explicitly asked for a loaded model to be animated (ignoring whether we wanted to preview animations)
                    SnooperViewer.Renderer.Animate(pointer.Object.Value);
                    SnooperViewer.Run();
                    return true;
                }
            case UStaticMesh when HasFlag(bulk, EBulkType.Meshes):
            case USkeletalMesh when HasFlag(bulk, EBulkType.Meshes):
            case USkeleton when UserSettings.Default.SaveSkeletonAsMesh && HasFlag(bulk, EBulkType.Meshes):
            // case UMaterialInstance when HasFlag(bulk, EBulkType.Materials): // read the fucking json
            case UAnimSequenceBase when HasFlag(bulk, EBulkType.Animations):
                {
                    SaveExport(pointer.Object.Value, updateUi);
                    return true;
                }
            default:
                {
                    if (!isNone && !saveTextures)
                        return false;

                    using var cPackage = new CreatorPackage(pkg.Name, dummy.ExportType, pointer.Object, UserSettings.Default.CosmeticStyle);
                    if (!cPackage.TryConstructCreator(out var creator))
                        return false;

                    creator.ParseForInfo();
                    TabControl.SelectedTab.AddImage(pointer.Object.Value.Name, false, creator.Draw(), saveTextures, updateUi);
                    return true;

                }
        }
    }

    public void ShowMetadata(GameFile entry)
    {
        var package = Provider.LoadPackage(entry);

        if (TabControl.CanAddTabs)
            TabControl.AddTab(entry);
        else
            TabControl.SelectedTab.SoftReset(entry);

        TabControl.SelectedTab.TitleExtra = "Metadata";
        TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("");

        TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(package, Formatting.Indented), false, false);
    }

    public void Decompile(GameFile entry)
    {
        if (TabControl.CanAddTabs) TabControl.AddTab(entry);
        else TabControl.SelectedTab.SoftReset(entry);

        TabControl.SelectedTab.TitleExtra = "復元";
        TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("cpp");

        var pkg = Provider.LoadPackage(entry);

        var outputBuilder = new StringBuilder();
        for (var i = 0; i < pkg.ExportMapLength; i++)
        {
            var pointer = new FPackageIndex(pkg, i + 1).ResolvedObject;
            if (pointer?.Object is null)
                continue;

            var dummy = ((AbstractUePackage) pkg).ConstructObject(pointer.Class?.Object?.Value as UStruct, pkg);
            if (dummy is not UClass || pointer.Object.Value is not UClass blueprint)
                continue;

            var typePrefix = KismetExtensions.GetPrefix(blueprint.GetType().Name);
            outputBuilder.AppendLine($"class {typePrefix}{blueprint.Name} : public {typePrefix}{blueprint?.SuperStruct?.Name ?? string.Empty}\n{{\npublic:");

            if (!blueprint.ClassDefaultObject.TryLoad(out var bpObject))
                continue;

            var strings = new List<string>();
            foreach (var property in bpObject.Properties)
            {
                var propertyName = property.Name.ToString();
                var propertyValue = property.Tag?.GenericValue;
                strings.Add(propertyName);
                string placeholder = $"{propertyName}fmodelholder"; // spelling mistake is intended

                void ShouldAppend(string value)
                {
                    if (outputBuilder.ToString().Contains(placeholder))
                    {
                        outputBuilder.Replace(placeholder, value);
                    }
                    else
                    {
                        outputBuilder.AppendLine($"\t{KismetExtensions.GetPropertyType(propertyValue)} {propertyName.Replace(" ", "")} = {value};");
                    }
                }

                string GetLineOfText(object value)
                {
                    string text = null;
                    switch (value)
                    {
                        case FScriptStruct structTag:
                            switch (structTag.StructType)
                            {
                                case FVector vector:
                                    text = $"FVector({vector.X}, {vector.Y}, {vector.Z})";
                                    break;
                                case FGuid guid:
                                    text = $"FGuid({guid.A}, {guid.B}, {guid.C}, {guid.D})";
                                    break;
                                case TIntVector3<int> vector3:
                                    text = $"FVector({vector3.X}, {vector3.Y}, {vector3.Z})";
                                    break;
                                case TIntVector3<float> floatVector3:
                                    text = $"FVector({floatVector3.X}, {floatVector3.Y}, {floatVector3.Z})";
                                    break;
                                case TIntVector2<float> floatVector2:
                                    text = $"FVector2D({floatVector2.X}, {floatVector2.Y})";
                                    break;
                                case FVector2D vector2d:
                                    text = $"FVector2D({vector2d.X}, {vector2d.Y})";
                                    break;
                                case FRotator rotator:
                                    text = $"FRotator({rotator.Pitch}, {rotator.Yaw}, {rotator.Roll})";
                                    break;
                                case FLinearColor linearColor:
                                    text = $"FLinearColor({linearColor.R}, {linearColor.G}, {linearColor.B}, {linearColor.A})";
                                    break;
                                case FGameplayTagContainer gTag:
                                    text = gTag.GameplayTags.Length switch
                                    {
                                        > 1 => "[\n" + string.Join(",\n", gTag.GameplayTags.Select(tag => $"\t\t\"{tag.TagName}\"")) + "\n\t]",
                                        > 0 => $"\"{gTag.GameplayTags[0].TagName}\"",
                                        _ => "[]"
                                    };
                                    break;
                                case FStructFallback fallback:
                                    if (fallback.Properties.Count > 0)
                                    {
                                        text = "[\n" + string.Join(",\n", fallback.Properties.Select(p => $"\t\"{GetLineOfText(p)}\"")) + "\n\t]";
                                    }
                                    else
                                    {
                                        text = "[]";
                                    }
                                    break;
                            }
                            break;
                        case UScriptSet:
                        case UScriptMap:
                        case UScriptArray:
                            IEnumerable<string> inner = value switch
                            {
                                UScriptSet set => set.Properties.Select(p => $"\t\"{p.GenericValue}\""),
                                UScriptMap map => map.Properties.Select(kvp => $"\t{{\n\t\t\"{kvp.Key}\": \"{kvp.Value}\"\n\t}}"),
                                UScriptArray array => array.Properties.Select(p => $"\t\"{GetLineOfText(p)}\""),
                                _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
                            };

                            text = "[\n" + string.Join(",\n", inner) + "\n\t]";
                            break;
                        case FMulticastScriptDelegate multicast:
                            text = multicast.InvocationList.Length == 0 ? "[]" : $"[{string.Join(", ", multicast.InvocationList.Select(x => $"\"{x.FunctionName}\""))}]";
                            break;
                        case bool:
                            text = value.ToString()?.ToLowerInvariant();
                            break;
                    }

                    return text ?? value.ToString();
                }

                ShouldAppend(GetLineOfText(propertyValue));
            }

            foreach (var field in blueprint.ChildProperties)
            {
                if (field is not FProperty property || strings.Contains(property.Name.Text)) continue;

                var propertyName = property.Name.ToString().Replace(" ", "");
                var type = KismetExtensions.GetPropertyType(property);
                var prefix = KismetExtensions.GetPrefix(property.GetType().Name);

                string pointerIdentifier;
                if (property.PropertyFlags.HasFlag(EPropertyFlags.InstancedReference) ||
                    property.PropertyFlags.HasFlag(EPropertyFlags.ReferenceParm) ||
                    KismetExtensions.GetPropertyProperty(property))
                {
                    pointerIdentifier = "*";
                }
                else
                {
                    pointerIdentifier = string.Empty;
                }

                outputBuilder.AppendLine($"\t{prefix}{type}{pointerIdentifier} {propertyName} = {propertyName}fmodelholder;");
            }

            {
                var funcMapOrder = blueprint?.FuncMap?.Keys.Select(fname => fname.ToString()).ToList();
                var functions = pkg.ExportsLazy
                .Where(e => e.Value is UFunction)
                .Select(e => (UFunction) e.Value)
                    .OrderBy(f =>
                    {
                        if (funcMapOrder != null)
                        {
                            var functionName = f.Name.ToString();
                            int index = funcMapOrder.IndexOf(functionName);
                            return index >= 0 ? index : int.MaxValue;
                        }

                        return int.MaxValue;
                    })
                    .ThenBy(f => f.Name.ToString())
                    .ToList();

                var jumpCodeOffsetsMap = new Dictionary<string, List<int>>();

                foreach (var function in functions.AsEnumerable().Reverse())
                {
                    if (function?.ScriptBytecode == null)
                        continue;

                    foreach (var property in function.ScriptBytecode)
                    {
                        string? label = null;
                        int? offset = null;

                        switch (property.Token)
                        {
                            case EExprToken.EX_JumpIfNot:
                                label = ((EX_JumpIfNot) property).ObjectPath?.ToString()?.Split('.').Last().Split('[')[0];
                                offset = (int) ((EX_JumpIfNot) property).CodeOffset;
                                break;

                            case EExprToken.EX_Jump:
                                label = ((EX_Jump) property).ObjectPath?.ToString()?.Split('.').Last().Split('[')[0];
                                offset = (int) ((EX_Jump) property).CodeOffset;
                                break;

                            case EExprToken.EX_LocalFinalFunction:
                                {
                                    EX_FinalFunction op = (EX_FinalFunction) property;
                                    label = op.StackNode?.Name?.ToString()?.Split('.').Last().Split('[')[0];

                                    if (op.Parameters.Length == 1 && op.Parameters[0] is EX_IntConst intConst)
                                        offset = intConst.Value;
                                    break;
                                }
                        }

                        if (!string.IsNullOrEmpty(label) && offset.HasValue)
                        {
                            if (!jumpCodeOffsetsMap.TryGetValue(label, out var list))
                                jumpCodeOffsetsMap[label] = list = new List<int>();

                            list.Add(offset.Value);
                        }
                    }
                }

                foreach (var function in functions)
                {
                    string argsList = "";
                    string returnFunc = "void";
                    if (function?.ChildProperties != null)
                    {
                        foreach (FProperty property in function.ChildProperties)
                        {
                            var name = property.Name.ToString();
                            var plainName = property.Name.PlainText;
                            var prefix = KismetExtensions.GetPrefix(property.GetType().Name);
                            var type = KismetExtensions.GetPropertyType(property);
                            var isConst = property.PropertyFlags.HasFlag(EPropertyFlags.ConstParm);
                            var isOut = property.PropertyFlags.HasFlag(EPropertyFlags.OutParm);
                            var isInstanced = property.PropertyFlags.HasFlag(EPropertyFlags.InstancedReference);
                            var isEdit = property.PropertyFlags.HasFlag(EPropertyFlags.Edit);

                            if (plainName == "ReturnValue")
                            {
                                returnFunc = $"{(isConst ? "const " : "")}{prefix}{type}{(isInstanced || prefix == "U" ? "*" : "")}";
                                continue;
                            }

                            bool uselessIgnore = name.EndsWith("_ReturnValue") || name.StartsWith("CallFunc_") || name.StartsWith("K2Node_") || name.StartsWith("Temp_"); // read variable name

                            if (uselessIgnore && !isEdit)
                                continue;

                            var strippedVerseName = Regex.Replace(name, @"^__verse_0x[0-9A-Fa-f]+_", "");
                            argsList += $"{(isConst ? "const " : "")}{prefix}{type}{(isInstanced || prefix == "U" ? "*" : "")}{(isOut ? "&" : "")} {strippedVerseName}, ";
                        }
                    }
                    argsList = argsList.TrimEnd(',', ' ');

                    outputBuilder.AppendLine($"\n\t{returnFunc} {function.Name.Replace(" ", "")}({argsList})\n\t{{");
                    if (function?.ScriptBytecode != null)
                    {
                        var jumpCodeOffsets = jumpCodeOffsetsMap.TryGetValue(function.Name, out var list) ? list : new List<int>();
                        foreach (KismetExpression property in function.ScriptBytecode)
                        {
                            KismetExtensions.ProcessExpression(property.Token, property, outputBuilder, jumpCodeOffsets);
                        }
                    }
                    else
                    {
                        outputBuilder.Append("\n\t // No Bytecode (Make sure \"Serialize Script Bytecode\" is enabled \n\n");
                        outputBuilder.Append("\t}\n");
                    }
                }

                outputBuilder.Append("\n\n}");
            }
        }

        var cpp = Regex.Replace(outputBuilder.ToString(), @"\w+fmodelholder", "nullptr");
        TabControl.SelectedTab.SetDocumentText(cpp, false, false);
    }

    private void SaveAndPlaySound(string fullPath, string ext, byte[] data)
    {
        if (fullPath.StartsWith('/'))
            fullPath = fullPath[1..];
        var savedAudioPath = Path.Combine(UserSettings.Default.AudioDirectory,
            UserSettings.Default.KeepDirectoryStructure ? fullPath : fullPath.SubstringAfterLast('/')).Replace('\\', '/') + $".{ext.ToLowerInvariant()}";

        if (!UserSettings.Default.IsAutoOpenSounds)
        {
            Directory.CreateDirectory(savedAudioPath.SubstringBeforeLast('/'));
            using var stream = new FileStream(savedAudioPath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);
            writer.Write(data);
            writer.Flush();
            return;
        }

        // TODO
        // since we are currently in a thread, the audio player's lifetime (memory-wise) will keep the current thread up and running until fmodel itself closes
        // the solution would be to kill the current thread at this line and then open the audio player without "Application.Current.Dispatcher.Invoke"
        // but the ThreadWorkerViewModel is an idiot and doesn't understand we want to kill the current thread inside the current thread and continue the code
        Application.Current.Dispatcher.Invoke(delegate
        {
            var audioPlayer = Helper.GetWindow<AudioPlayer>("Audio Player", () => new AudioPlayer().Show());
            audioPlayer.Load(data, savedAudioPath);
        });
    }

    private void SaveExport(UObject export, bool updateUi = true)
    {
        var toSave = new Exporter(export, UserSettings.Default.ExportOptions);
        var toSaveDirectory = new DirectoryInfo(UserSettings.Default.ModelDirectory);
        if (toSave.TryWriteToDir(toSaveDirectory, out var label, out var savedFilePath))
        {
            Log.Information("Successfully saved {FilePath}", savedFilePath);
            if (updateUi)
            {
                FLogger.Append(ELog.Information, () =>
                {
                    FLogger.Text("Successfully saved ", Constants.WHITE);
                    FLogger.Link(label, savedFilePath, true);
                });
            }
        }
        else
        {
            Log.Error("{FileName} could not be saved", export.Name);
            FLogger.Append(ELog.Error, () => FLogger.Text($"Could not save '{export.Name}'", Constants.WHITE, true));
        }
    }

    private readonly object _rawData = new();
    public void ExportData(GameFile entry, bool updateUi = true)
    {
        if (Provider.TrySavePackage(entry, out var assets))
        {
            string path = UserSettings.Default.RawDataDirectory;
            Parallel.ForEach(assets, kvp =>
            {
                lock (_rawData)
                {
                    path = Path.Combine(UserSettings.Default.RawDataDirectory, UserSettings.Default.KeepDirectoryStructure ? kvp.Key : kvp.Key.SubstringAfterLast('/')).Replace('\\', '/');
                    Directory.CreateDirectory(path.SubstringBeforeLast('/'));
                    File.WriteAllBytes(path, kvp.Value);
                }
            });

            Log.Information("{FileName} successfully exported", entry.Name);
            if (updateUi)
            {
                FLogger.Append(ELog.Information, () =>
                {
                    FLogger.Text("Successfully exported ", Constants.WHITE);
                    FLogger.Link(entry.Name, path, true);
                });
            }
        }
        else
        {
            Log.Error("{FileName} could not be exported", entry.Name);
            if (updateUi)
                FLogger.Append(ELog.Error, () => FLogger.Text($"Could not export '{entry.Name}'", Constants.WHITE, true));
        }
    }

    private static bool HasFlag(EBulkType a, EBulkType b)
    {
        return (a & b) == b;
    }

    private static SKBitmap RenderSvg(Stream stream)
    {
        const int size = 512;
        var svg = new SkiaSharp.Extended.Svg.SKSvg();
        svg.Load(stream);
        var bmp = new SKBitmap(size, size);
        using var canvas = new SKCanvas(bmp);
        using var paint = new SKPaint();
        paint.IsAntialias = true;
        paint.FilterQuality = SKFilterQuality.Medium;
        canvas.Clear(SKColors.Transparent);
        canvas.DrawPicture(svg.Picture, paint);
        return bmp;
    }
}
