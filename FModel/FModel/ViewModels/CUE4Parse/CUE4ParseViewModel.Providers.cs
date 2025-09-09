using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AdonisUI.Controls;
using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using FModel.Extensions;
using EpicManifestParser;
using EpicManifestParser.UE;
using EpicManifestParser.ZlibngDotNetDecompressor;
using FModel.Settings;
using FModel.ViewModels;
using FModel.Views;
using FModel.Views.Resources.Controls;
using Serilog;

namespace FModel.ViewModels.CUE4Parse;

public partial class CUE4ParseViewModel
{
    // 初期化処理
    public async Task Initialize()
    {
        await _threadWorkerView.Begin(cancellationToken =>
        {
            // Providerとディレクトリを取得し、それぞれ初期化
            foreach (var (provider, dir) in ProvidersWithDirectories())
            {
                InitializeProvider(provider, dir, cancellationToken);
            }

            // 初期化したProviderごとにログ出力
            ForEachProvider(provider =>
            {
                Log.Information($"{provider.Versions.Game} ({provider.Versions.Platform}) | アーカイブ数: x{provider.UnloadedVfs.Count} | AESキー数: x{provider.RequiredKeys.Count} | Looseファイル数: x{provider.Files.Count}");
            });
        });
    }

    // Providerの初期化処理
    private void InitializeProvider(AbstractVfsFileProvider provider, DirectorySettings dir, CancellationToken cancellationToken)
    {
        switch (provider)
        {
            case StreamedFileProvider p:
                switch (p.LiveGame)
                {
                    case "FortniteLive":
                        {
                            // Fortnite用マニフェストを取得
                            var manifestInfo = _apiEndpointView.EpicApi.GetManifest(cancellationToken);
                            if (manifestInfo is null)
                            {
                                throw new FileLoadException("最新のFortniteマニフェストを取得できませんでした。ローカルインストールに切り替える必要があるかもしれません。");
                            }

                            // キャッシュディレクトリを準備
                            var cacheDir = Directory.CreateDirectory(Path.Combine(UserSettings.Default.OutputDirectory, ".data")).FullName;
                            var manifestOptions = new ManifestParseOptions
                            {
                                ChunkCacheDirectory = cacheDir,
                                ManifestCacheDirectory = cacheDir,
                                ChunkBaseUrl = "http://download.epicgames.com/Builds/Fortnite/CloudDir/",
                                Decompressor = ManifestZlibngDotNetDecompressor.Decompress,
                                DecompressorState = ZlibHelper.Instance,
                                CacheChunksAsIs = false
                            };

                            // ロード状況を表示するウィンドウ
                            var loadingVm = new LoadingInfoWindowViewModel();
                            LoadingInfoWindow loadingWindow = null;
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                loadingWindow = new LoadingInfoWindow(loadingVm);
                                loadingWindow.Show();
                            });

                            var stopwatch = new Stopwatch();
                            stopwatch.Start();

                            // 経過時間を更新するタイマー
                            var timer = new System.Timers.Timer(1000);
                            timer.Elapsed += (sender, e) =>
                            {
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    loadingVm.ElapsedTime = $"{stopwatch.Elapsed:mm\\:ss}";
                                });
                            };
                            timer.Start();

                            FBuildPatchAppManifest manifest;
                            try
                            {
                                loadingVm.StatusText = "マニフェストをダウンロード中...";
                                (manifest, _) = manifestInfo.DownloadAndParseAsync(manifestOptions,
                                    cancellationToken: cancellationToken,
                                    elementManifestPredicate: static x => x.Uri.Host == "download.epicgames.com"
                                ).GetAwaiter().GetResult();

                                loadingVm.StatusText = "マニフェストを解析中...";
                                var totalSize = manifest.Files.Sum(x => (long)x.FileSize);
                                loadingVm.DownloadSize = StringExtensions.GetReadableSize(totalSize);
                                loadingVm.EstimatedTime = "不明";
                                loadingVm.ProgressText = "";
                                loadingVm.IsIndeterminate = true;

                                loadingVm.StatusText = "最終処理中...";
                                if (manifest.TryFindFile("Cloud/IoStoreOnDemand.ini", out var ioStoreOnDemandFile))
                                {
                                    IoStoreOnDemand.Read(new StreamReader(ioStoreOnDemandFile.GetStream()));
                                }

                                // Fortnite用のファイルを登録
                                Parallel.ForEach(manifest.Files.Where(x => _fnLiveRegex.IsMatch(x.FileName)), fileManifest =>
                                {
                                    p.RegisterVfs(fileManifest.FileName, [fileManifest.GetStream()],
                                        it => new FRandomAccessStreamArchive(it, manifest.FindFile(it)!.GetStream(), p.Versions));
                                });

                                FLogger.Append(ELog.Information, () =>
                                    FLogger.Text($"Fortnite [LIVE] が {stopwatch.Elapsed:g} で正常にロードされました", Constants.WHITE, true));
                            }
                            catch (HttpRequestException ex)
                            {
                                Log.Error("マニフェストのダウンロードに失敗しました ({ManifestUri})", ex.Data["ManifestUri"]?.ToString() ?? "");
                                throw;
                            }
                            finally
                            {
                                stopwatch.Stop();
                                timer.Stop();
                                Application.Current.Dispatcher.Invoke(() => loadingWindow?.Close());
                            }
                            break;
                        }
                    case "ValorantLive":
                        {
                            // Valorant用マニフェストを取得
                            var manifest = _apiEndpointView.ValorantApi.GetManifest(cancellationToken);
                            if (manifest == null)
                            {
                                throw new Exception("最新のValorantマニフェストを取得できませんでした。ローカルインストールに切り替える必要があるかもしれません。");
                            }

                            // Pakファイルを登録
                            Parallel.ForEach(manifest.Paks, pak =>
                            {
                                p.RegisterVfs(pak.GetFullName(), [pak.GetStream(manifest)]);
                            });

                            FLogger.Append(ELog.Information, () =>
                                FLogger.Text($"Valorant '{manifest.Header.GameVersion}' が正常にロードされました", Constants.WHITE, true));
                            break;
                        }
                }
                break;
            case DefaultFileProvider:
            {
                // ローカルインストール版のIoStoreOnDemand設定を読み込む
                var ioStoreOnDemandPath = Path.Combine(dir.GameDirectory, "..\\..\\..\\Cloud\\IoStoreOnDemand.ini");
                if (File.Exists(ioStoreOnDemandPath))
                {
                    using var s = new StreamReader(ioStoreOnDemandPath);
                    IoStoreOnDemand.Read(s);
                }
                break;
            }
        }
        provider.Initialize();
    }

    // Providerを作成する
    private static AbstractVfsFileProvider CreateProvider(DirectorySettings dir, Regex fnLiveRegex)
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
                    return project switch
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
                        _ => new DefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer, pathComparer)
                    };
                }
        }
    }

    // 読み込み設定を更新
    public void RefreshReadSettings()
    {
        Provider.ReadScriptData = UserSettings.Default.ReadScriptData;
        Provider.ReadShaderMaps = UserSettings.Default.ReadShaderMaps;

        if (UserSettings.Default.DiffDir == null || DiffProvider == null) return;

        DiffProvider.ReadScriptData = UserSettings.Default.ReadScriptData;
        DiffProvider.ReadShaderMaps = UserSettings.Default.ReadShaderMaps;
    }

    // 全てのProviderを返す
    public IEnumerable<AbstractVfsFileProvider> AllProviders()
    {
        yield return Provider;
        if (DiffProvider != null)
            yield return DiffProvider;
    }

    // 各Providerに対して処理を実行
    public void ForEachProvider(Action<AbstractVfsFileProvider> action)
    {
        foreach (var provider in AllProviders())
            action(provider);
    }

    // Endpointを持つProviderを返す
    private IEnumerable<(AbstractVfsFileProvider Provider, EndpointSettings Endpoint)> ProvidersWithEndpoints(EEndpointType type)
    {
        if (UserSettings.IsEndpointValid(UserSettings.Default.CurrentDir, type, out var mainEndpoint))
            yield return (Provider, mainEndpoint);

        if (DiffProvider != null && UserSettings.Default.DiffDir != null
                                 && UserSettings.IsEndpointValid(UserSettings.Default.DiffDir, type, out var diffEndpoint))
        {
            yield return (DiffProvider, diffEndpoint);
        }
    }

    // ディレクトリ設定を持つProviderを返す
    public IEnumerable<(AbstractVfsFileProvider Provider, DirectorySettings Dir)> ProvidersWithDirectories()
    {
        yield return (Provider, UserSettings.Default.CurrentDir);
        if (DiffProvider != null && UserSettings.Default.DiffDir != null)
            yield return (DiffProvider, UserSettings.Default.DiffDir);
    }

    // Providerをクリアする（キャッシュや検索結果を削除）
    public void ClearProvider()
    {
        AssetsFolder.Folders.Clear();
        SearchVm.SearchResults.Clear();
        Helper.CloseWindow<AdonisWindow>("検索ウィンドウ");

        ForEachProvider(provider => provider.UnloadNonStreamedVfs());

        GC.Collect();
    }
}
