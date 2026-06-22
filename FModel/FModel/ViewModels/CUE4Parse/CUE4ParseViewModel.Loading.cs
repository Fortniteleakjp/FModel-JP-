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
    public async Task Initialize()
    {
        await _apiEndpointView.EpicApi.VerifyAuth(CancellationToken.None);
        await _threadWorkerView.Begin(cancellationToken =>
        {
            Provider.OnDemandOptions = new IoStoreOnDemandOptions
            {
                ChunkHostUri = new Uri("https://download.epicgames.com/", UriKind.Absolute),
                ChunkCacheDirectory = Directory.CreateDirectory(Path.Combine(UserSettings.Default.OutputDirectory, ".data")),
                Authorization = new AuthenticationHeaderValue("Bearer", UserSettings.Default.LastAuthResponse?.AccessToken),
                Timeout = TimeSpan.FromSeconds(30)
            };
            switch (Provider)
            {
                case StreamedFileProvider p:
                    switch (p.LiveGame)
                    {
                        case "FortniteLive":
                        {
                            var manifestInfo = _apiEndpointView.EpicApi.GetManifest(cancellationToken);
                            if (manifestInfo is null)
                            {
                                throw new FileLoadException("Could not load latest Fortnite manifest, you may have to switch to your local installation.");
                            }

                            var cacheDir = Directory.CreateDirectory(Path.Combine(UserSettings.Default.OutputDirectory, ".data")).FullName;
                            var manifestOptions = new ManifestParseOptions
                            {
                                ChunkCacheDirectory = cacheDir,
                                ManifestCacheDirectory = cacheDir,
                                ChunkBaseUrl = "https://egdownload.fastly-edge.com/Builds/Fortnite/CloudDir/",
                                Decompressor = ManifestZlibngDotNetDecompressor.Decompress,
                                DecompressorState = ZlibHelper.Instance,
                                CacheChunksAsIs = false
                            };

                            var startTs = Stopwatch.GetTimestamp();
                            FBuildPatchAppManifest manifest;

                            try
                            {
                                (manifest, _) = manifestInfo.DownloadAndParseAsync(manifestOptions,
                                    cancellationToken: cancellationToken,
                                    elementManifestPredicate: static x => x.Uri.Host is "egdownload.fastly-edge.com" or "epicgames-download1.akamaized.net" or "download.epicgames.com"
                                ).GetAwaiter().GetResult();
                            }
                            catch (HttpRequestException ex)
                            {
                                Log.Error("Failed to download manifest ({ManifestUri})", ex.Data["ManifestUri"]?.ToString() ?? "");
                                throw;
                            }

                            if (manifest.TryFindFile("Cloud/IoStoreOnDemand.ini", out var ioStoreOnDemandFile))
                            {
                                IoStoreOnDemand.Read(new StreamReader(ioStoreOnDemandFile.GetStream()));
                            }

                            Parallel.ForEach(manifest.Files.Where(x => _fnLiveRegex.IsMatch(x.FileName)), fileManifest =>
                            {
                                p.RegisterVfs(fileManifest.FileName, [fileManifest.GetStream()],
                                    it => new FRandomAccessStreamArchive(it, manifest.FindFile(it)!.GetStream(), p.Versions));
                            });

                            // UEFN（Fortnite Studio）も Fortnite [LIVE] に含める（設定で切替可・4sval/FModel PR #663）
                            if (UserSettings.Default.LoadUefnWithLive)
                            try
                            {
                                var dillyManifests = _apiEndpointView.DillyApi.GetManifests(cancellationToken);
                                var uefn = dillyManifests?.FirstOrDefault(x => x.AppName == "Fortnite_Studio");
                                if (uefn != null && !string.IsNullOrEmpty(uefn.DownloadUrl))
                                {
                                    using var uefnClient = new HttpClient();
                                    var uefnBytes = uefnClient.GetByteArrayAsync(uefn.DownloadUrl, cancellationToken).GetAwaiter().GetResult();
                                    var uefnManifest = FBuildPatchAppManifest.Deserialize(uefnBytes, manifestOptions);
                                    Parallel.ForEach(uefnManifest.Files.Where(x => _fnLiveRegex.IsMatch(x.FileName)), fileManifest =>
                                    {
                                        p.RegisterVfs(fileManifest.FileName, [fileManifest.GetStream()],
                                            it => new FRandomAccessStreamArchive(it, uefnManifest.FindFile(it)!.GetStream(), p.Versions));
                                    });
                                    FLogger.Append(ELog.Information, () =>
                                        FLogger.Text("UEFN (Fortnite Studio) も Fortnite [LIVE] に読み込みました", Constants.WHITE, true));
                                }
                            }
                            catch (Exception uefnEx)
                            {
                                Log.Warning(uefnEx, "UEFN (Fortnite Studio) の読み込みに失敗しました（Fortnite [LIVE] は通常通り利用できます）");
                            }

                            var elapsedTime = Stopwatch.GetElapsedTime(startTs);
                            FLogger.Append(ELog.Information, () =>
                                FLogger.Text($"Fortnite [LIVE] has been loaded successfully in {elapsedTime.TotalMilliseconds:F1}ms", Constants.WHITE, true));
                            break;
                        }
                        case "ValorantLive":
                        {
                            var manifest = _apiEndpointView.ValorantApi.GetManifest(cancellationToken);
                            if (manifest == null)
                            {
                                throw new Exception("Could not load latest Valorant manifest, you may have to switch to your local installation.");
                            }

                            Parallel.ForEach(manifest.Paks, pak =>
                            {
                                p.RegisterVfs(pak.GetFullName(), [pak.GetStream(manifest)]);
                            });

                            FLogger.Append(ELog.Information, () =>
                                FLogger.Text($"Valorant '{manifest.Header.GameVersion}' has been loaded successfully", Constants.WHITE, true));
                            break;
                        }
                    }

                    break;
                case DefaultFileProvider:
                {
                    var ioStoreOnDemandPath = Path.Combine(UserSettings.Default.GameDirectory, "..\\..\\..\\Cloud\\IoStoreOnDemand.ini");
                    if (File.Exists(ioStoreOnDemandPath))
                    {
                        using var s = new StreamReader(ioStoreOnDemandPath);
                        IoStoreOnDemand.Read(s);
                    }
                    break;
                }
            }

            var tasks = new List<Task>();
            if (Provider != null) tasks.Add(Task.Run(() => Provider.Initialize()));
            if (DiffProvider != null) tasks.Add(Task.Run(() => DiffProvider.Initialize()));
            Task.WaitAll(tasks.ToArray());
            _wwiseProviderLazy = new Lazy<WwiseProvider>(() => new WwiseProvider(Provider, UserSettings.Default.GameDirectory));
            _fmodProviderLazy = new Lazy<FModProvider>(() => new FModProvider(Provider, UserSettings.Default.GameDirectory));
            _criWareProviderLazy = new Lazy<CriWareProvider>(() => new CriWareProvider(Provider, UserSettings.Default.GameDirectory));
            if (Provider != null)
            {
                Log.Information($"{Provider.Versions.Game} ({Provider.Versions.Platform}) | Archives: x{Provider.UnloadedVfs.Count} | AES: x{Provider.RequiredKeys.Count} | Loose Files: x{Provider.Files.Count}");
            }
        });
    }

    public void ClearProvider()
    {
        if (Provider == null) return;

        AssetsFolder.Folders.Clear();
        SearchVm.SearchResults.Clear();
        Helper.CloseWindow<AdonisWindow>("Search View");
        Provider.UnloadNonStreamedVfs();
        GC.Collect();
    }

    public async Task InitInformation()
    {
        await _threadWorkerView.Begin(cancellationToken =>
        {
            var info = _apiEndpointView.FModelApi.GetNews(cancellationToken, Provider.ProjectName);
            if (info == null) return;

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
            if (inst.Count <= 0) return;

            var ioStoreOnDemandPath = Path.Combine(UserSettings.Default.GameDirectory, "..\\..\\..\\Cloud", inst[0].Value.SubstringAfterLast("/").SubstringBefore("\""));
            if (!File.Exists(ioStoreOnDemandPath)) return;
            await Provider.RegisterVfsAsync(new IoChunkToc(ioStoreOnDemandPath, Provider.Versions));
            var onDemandCount = await Provider.MountAsync();
            FLogger.Append(ELog.Information, () =>
                FLogger.Text($"{onDemandCount} on-demand archive{(onDemandCount > 1 ? "s" : "")} streamed via epicgames.com", Constants.WHITE, true));
        });
    }

}
