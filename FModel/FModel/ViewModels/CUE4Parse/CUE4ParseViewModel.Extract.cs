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

public partial class CUE4ParseViewModel
{
    public void OpenReferenceChain(IEnumerable<GameFile> assets)
    {
        new ReferenceChainWindow(assets.ToList()).Show();
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

        foreach (var f in folder.Folders) BulkFolder(cancellationToken, f, action);
    }

    public void ExportFolder(CancellationToken cancellationToken, TreeItem folder)
    {
        Parallel.ForEach(folder.AssetsList.Assets, entry =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExportData(entry, false);
        });

        foreach (var f in folder.Folders) ExportFolder(cancellationToken, f);
    }

    public void ExtractFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs));

    public void SaveFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Properties | EBulkType.Auto));
    public void SaveDecompiled(CancellationToken cancellationToken, TreeItem folder)
    => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Code | EBulkType.Auto));

    public void TextureFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Textures | EBulkType.Auto));

    public void ModelFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Meshes | EBulkType.Auto));

    public void AnimationFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Animations | EBulkType.Auto));

    public void AudioFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Audio | EBulkType.Auto));

    public void Extract(CancellationToken cancellationToken, GameFile entry, bool addNewTab = false, EBulkType bulk = EBulkType.None)
    {
        Log.Information("User DOUBLE-CLICKED to extract '{FullPath}'", entry.Path);

        if (addNewTab && TabControl.CanAddTabs) TabControl.AddTab(entry);
        else TabControl.SelectedTab.SoftReset(entry);
        TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector(entry.Extension);

        var updateUi = !HasFlag(bulk, EBulkType.Auto);
        var saveProperties = HasFlag(bulk, EBulkType.Properties);
        var saveTextures = HasFlag(bulk, EBulkType.Textures);
        var saveAudio = HasFlag(bulk, EBulkType.Audio);
        var saveDecompiled = HasFlag(bulk, EBulkType.Code);
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
                }
                if (saveDecompiled)
                {
                    var cpp = Decompile(entry, addTab: false);
                    TabControl.SelectedTab.SetDocumentText(cpp, save: false, updateUi: false); // Set text without updating UI or saving as json
                    TabControl.SelectedTab.SaveDecompiled(updateUi); // Save as cpp
                }

                if (saveProperties || saveDecompiled) break; // do not search for viewable exports if we are dealing with jsons or code

                    for (var i = result.InclusiveStart; i < result.ExclusiveEnd; i++)
                {
                    if (CheckExport(cancellationToken, result.Package, i, bulk))
                        break;
                }

                break;
            }
            case "ini" when entry.Name.Contains("BinaryConfig"):
            {
                var ar = entry.CreateReader();
                var configCache = new FConfigCacheIni(ar);

                TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("json");
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(configCache, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "upluginmanifest":
            case "code-workspace":
            case "projectstore":
            case "uefnproject":
            case "uproject":
            case "manifest":
            case "uplugin":
            case "archive":
            case "dnearchive": // Banishers: Ghosts of New Eden
            case "gitignore":
            case "LICENSE":
            case "playstats": // Dispatch
            case "template":
            case "stUMeta": // LIS: Double Exposure
            case "vmodule":
            case "glslfx":
            case "cptake":
            case "uparam": // Steel Hunters
            case "spi1d":
            case "verse":
            case "html":
            case "json5":
            case "json":
            case "uref":
            case "cube":
            case "usda":
            case "ocio":
            case "data" when Provider.ProjectName is "OakGame":
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
            case "cpp":
            case "apx":
            case "udn":
            case "doc":
            case "lua":
            case "vdf":
            case "yml":
            case "js":
            case "po":
            case "md":
            case "h":
            // Uncharted Waters Origin
            case "crn":
            case "uwt":
            case "wvh":
            case "bf":
            case "bl":
            case "bm":
            case "br":
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
            case "bank":
            {
                var archive = entry.CreateReader();
                if (!FmodProvider.TryLoadBank(archive, entry.NameWithoutExtension, out var fmodReader))
                {
                    Log.Error($"Failed to load FMOD bank {entry.Path}");
                    break;
                }

                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(fmodReader, Formatting.Indented, converters: [new FmodSoundBankConverter(), new StringEnumConverter()]), saveProperties, updateUi);

                var extractedSounds = FmodProvider.ExtractBankSounds(fmodReader);
                var directory = Path.GetDirectoryName(entry.Path) ?? "/FMOD/Desktop/";
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(Path.Combine(directory, sound.Name), sound.Extension, sound.Data, saveAudio);
                }

                break;
            }
            case "bnk":
            case "pck":
            {
                var archive = entry.CreateReader();
                var wwise = new WwiseReader(new FWwiseArchive(archive), new WwiseGameFileSource(entry));
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(wwise, Formatting.Indented), saveProperties, updateUi);

                var medias = WwiseProvider.ExtractBankSounds(wwise);
                foreach (var media in medias)
                {
                    SaveAndPlaySound(media.OutputPath, media.Extension, media.Data?.GetData() ?? [], saveAudio);
                }

                break;
            }
            case "awb":
            {
                var archive = entry.CreateReader();
                var awbReader = new AwbReader(archive);

                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(awbReader, Formatting.Indented), saveProperties, updateUi);

                var directory = Path.GetDirectoryName(archive.Name) ?? "/Criware/";
                var extractedSounds = CriWareProvider.ExtractCriWareSounds(awbReader, archive.Name);
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(Path.Combine(directory, sound.Name), sound.Extension, sound.Data, saveAudio);
                }

                break;
            }
            case "acb":
            {
                var archive = entry.CreateReader();
                var acbReader = new AcbReader(archive);

                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(acbReader, Formatting.Indented), saveProperties, updateUi);

                var directory = Path.GetDirectoryName(archive.Name) ?? "/Criware/";
                var extractedSounds = CriWareProvider.ExtractCriWareSounds(acbReader, archive.Name);
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(Path.Combine(directory, sound.Name), sound.Extension, sound.Data, saveAudio);
                }

                break;
            }
            case "xvag":
            case "flac":
            case "at9":
            case "wem":
            case "wav":
            case "WAV":
            case "ogg":
                // todo: CSCore.MediaFoundation.MediaFoundationException The byte stream type of the given URL is unsupported. case "aif":
            {
                var data = Provider.SaveAsset(entry);
                SaveAndPlaySound(entry.PathWithoutExtension, entry.Extension, data, saveAudio);

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
                var svg = new SKSvg();
                if (svg.Load(stream) != null)
                {
                    const int size = 512;
                    var bitmap = new SKBitmap(size, size);
                    using (var canvas = new SKCanvas(bitmap))
                    {
                        canvas.Clear(SKColors.Transparent);
                        if (svg.Picture != null)
                        {
                            var scaleX = size / svg.Picture.CullRect.Width;
                            var scaleY = size / svg.Picture.CullRect.Height;
                            var scale = Math.Min(scaleX, scaleY);
                            
                            var matrix = SKMatrix.CreateScale(scale, scale);
                            canvas.DrawPicture(svg.Picture, ref matrix);
                        }
                    }

                    TabControl.SelectedTab.AddImage(entry.NameWithoutExtension, false, bitmap, saveTextures, updateUi);
                }

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
            case "stinfo":
            {
                var archive = entry.CreateReader();
                var ar = new FShaderTypeHashes(archive);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(ar, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "res": // just skip
            case "luac": // compiled lua
            case "bytes": // wuthering waves
                break;
            default:
            {
                Log.Warning($"The package '{entry.Name}' is of an unknown type.");
                if (!UnknownExtensions.Contains(entry.Extension))
                {
                    UnknownExtensions.Add(entry.Extension);
                FLogger.Append(ELog.Warning, () =>
                        FLogger.Text($"There are some packages with an unknown type {entry.Extension}. Check Log file for a full list.", Constants.WHITE, true));
                }
                break;
            }
        }

        // 通常のファイルオープン時のみ、クラウドストレージのホットフィックス有無を自動確認する
        if (bulk == EBulkType.None && updateUi &&
            TabControl.SelectedTab?.Document is not null)
        {
            _ = Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await FModel.Features.CloudStorage.CloudStorageHotfix.ExecuteAsync();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Auto cloud hotfix check failed for {Path}", entry.Path);
                }
            });
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

    private bool CheckExport(CancellationToken cancellationToken, IPackage pkg, int index, EBulkType bulk = EBulkType.None) // return true once you want to stop searching for exports
    {
        var isNone = bulk == EBulkType.None;
        var updateUi = !HasFlag(bulk, EBulkType.Auto);
        var saveTextures = HasFlag(bulk, EBulkType.Textures);
        var saveAudio = HasFlag(bulk, EBulkType.Audio);

        var pointer = new FPackageIndex(pkg, index + 1).ResolvedObject;
        if (pointer?.Object is null) return false;

        var dummy = ((AbstractUePackage) pkg).ConstructObject(pointer.Class, pkg);
        switch (dummy)
        {
            case UVerseDigest when isNone && pointer.Object.Value is UVerseDigest verseDigest:
            {
                if (!TabControl.CanAddTabs) return false;

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
                const int size = 512;
                var data = svgasset.GetOrDefault<byte[]>("SvgData");
                var sourceFile = svgasset.GetOrDefault<string>("SourceFile");
                using var stream = new MemoryStream(data) { Position = 0 };
                var svg = new SKSvg();
                SKBitmap bitmap = null;
                if (svg.Load(stream) != null)
                {
                    bitmap = new SKBitmap(size, size);
                    using (var canvas = new SKCanvas(bitmap))
                    {
                        canvas.Clear(SKColors.Transparent);
                        if (svg.Picture != null)
                        {
                            var scaleX = size / svg.Picture.CullRect.Width;
                            var scaleY = size / svg.Picture.CullRect.Height;
                            var scale = Math.Min(scaleX, scaleY);
                            
                            var matrix = SKMatrix.CreateScale(scale, scale);
                            canvas.DrawPicture(svg.Picture, ref matrix);
                        }
                    }
                }

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
            case UAkAudioEvent when (isNone || saveAudio) && pointer.Object.Value is UAkAudioEvent audioEvent:
            {
                var extractedSounds = WwiseProvider.ExtractAudioEventSounds(audioEvent);
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(sound.OutputPath, sound.Extension, sound.Data?.GetData() ?? [], saveAudio);
                }
                return false;
            }
            case UFMODEvent when (isNone || saveAudio) && pointer.Object.Value is UFMODEvent fmodEvent:
            {
                var extractedSounds = FmodProvider.ExtractEventSounds(fmodEvent);
                var directory = Path.GetDirectoryName(fmodEvent.Owner?.Name) ?? "/FMOD/Desktop/";
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(Path.Combine(directory, sound.Name), sound.Extension, sound.Data, saveAudio);
                }
                return false;
            }
            case USoundAtomCueSheet or UAtomCueSheet or USoundAtomCue or UAtomWaveBank when (isNone || saveAudio) && pointer.Object.Value is UObject atomObject:
            {
                var extractedSounds = atomObject switch
                {
                    USoundAtomCueSheet cueSheet => CriWareProvider.ExtractCriWareSounds(cueSheet),
                    UAtomCueSheet cueSheet => CriWareProvider.ExtractCriWareSounds(cueSheet),
                    USoundAtomCue cue => CriWareProvider.ExtractCriWareSounds(cue),
                    UAtomWaveBank awb => CriWareProvider.ExtractCriWareSounds(awb),
                    _ => []
                };

                var directory = Path.GetDirectoryName(atomObject.Owner?.Name) ?? "/Criware/";
                directory = Path.GetDirectoryName(atomObject.Owner.Provider.FixPath(directory));
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(Path.Combine(directory, sound.Name).Replace("\\", "/"), sound.Extension, sound.Data, saveAudio);
                }
                return false;
            }
            case UFMODBank when (isNone || saveAudio) && pointer.Object.Value is UFMODBank fmodBank:
            {
                var extractedSounds = FmodProvider.ExtractBankSounds(fmodBank);
                var directory = Path.GetDirectoryName(fmodBank.Owner?.Name) ?? "/FMOD/Desktop/";
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(Path.Combine(directory, sound.Name), sound.Extension, sound.Data, saveAudio);
                }
                return false;
            }
            case UAkMediaAssetData when isNone || saveAudio:
            case USoundWave when isNone || saveAudio:
            {
                var shouldDecompress = UserSettings.Default.CompressedAudioMode == ECompressedAudio.PlayDecompressed;
                pointer.Object.Value.Decode(shouldDecompress, out var audioFormat, out var data);
                var hasAf = !string.IsNullOrEmpty(audioFormat);
                if (data == null || !hasAf)
                {
                    if (hasAf) FLogger.Append(ELog.Warning, () => FLogger.Text($"Unsupported audio format '{audioFormat}'", Constants.WHITE, true));
                    return false;
                }

                SaveAndPlaySound(TabControl.SelectedTab.Entry.PathWithoutExtension.Replace('\\', '/'), audioFormat, data, saveAudio);
                return false;
            }
            case UAnimBlueprintGeneratedClass when isNone && pointer.Object.Value is UAnimBlueprintGeneratedClass animBpClass:
            {
                return TryOpenGraphViewer(animBpClass);
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
            case UBlueprint when isNone && pointer.Object.Value is UBlueprint blueprint:
            {
                return TryOpenGraphViewer(blueprint);
            }
            case UBlueprintGeneratedClass when isNone && pointer.Object.Value is UBlueprintGeneratedClass blueprintClass:
            {
                return TryOpenGraphViewer(blueprintClass);
            }
            case UMaterialInstance when isNone && ModelIsOverwritingMaterial && pointer.Object.Value is UMaterialInstance m:
            {
                SnooperViewer.Renderer.Swap(m);
                SnooperViewer.Run();
                return true;
            }
            case UAnimSequenceBase when isNone && UserSettings.Default.PreviewAnimations || ModelIsWaitingAnimation:
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
            case UWorld when HasFlag(bulk, EBulkType.Worlds):
            {
                SaveExport(pointer.Object.Value, updateUi);
                return true;
            }
            default:
            {
                if (!isNone && !saveTextures) return false;

                using var cPackage = new CreatorPackage(pkg.Name, dummy.ExportType, pointer.Object, UserSettings.Default.CosmeticStyle);
                if (!cPackage.TryConstructCreator(out var creator))
                    return false;

                creator.ParseForInfo();
                FModel.Creator.Layout.IconLayoutPreview.Set(
                    pkg.Name, dummy.ExportType, pointer.Object,
                    FModel.Creator.Layout.LayoutRenderContext.FromCreator(creator, dummy.ExportType));
                var bitmaps = creator.Draw();
                foreach (var bitmap in bitmaps)
                {
                    if (bitmap != null)
                        TabControl.SelectedTab.AddImage(pointer.Object.Value.Name, false, bitmap, saveTextures, updateUi);
                }
                return true;
            }
        }
    }
}
