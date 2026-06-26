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

public partial class SettingsViewModel
{
    private async Task DownloadAndCreatePak(JsonElement contentData, string mapCode, string userAgent)
    {
        try
        {
            FLogger.Append(ELog.Information, () => FLogger.Text("マニフェストをダウンロードしてPAKファイルを作成しています...", Constants.WHITE, true));

            // デバッグ用: contentDataの構造をログ出力
            Log.Information("ContentData構造: {ContentData}", contentData.ToString());

            if (contentData.ValueKind != JsonValueKind.Object)
            {
                FLogger.Append(ELog.Error, () => FLogger.Text("contentDataがオブジェクトではありません。", Constants.RED, true));
                return;
            }

            // コンテンツが準備中 (Pending) かどうかをチェック
            if (contentData.TryGetProperty("status", out var statusProp) && statusProp.GetString() == "pending")
            {
                var msg = "コンテンツの準備中(Pending)です。サーバー側での処理に時間がかかっています。数分待ってから再度[AESキーを取得]ボタンを押してください。";
                FLogger.Append(ELog.Warning, () => FLogger.Text(msg, Constants.YELLOW, true));
                Log.Information(msg);
                if (contentData.TryGetProperty("retry-after", out var retryAfter))
                    Log.Information("再試行推奨時間: {Seconds}秒", retryAfter.GetRawText());
                return;
            }

            // rootModuleIdは自動判定せず、ユーザー入力を使用
            var rootModuleId = RootModuleIdInput?.Trim();

            if (string.IsNullOrEmpty(rootModuleId))
            {
                FLogger.Append(ELog.Error, () => FLogger.Text("rootModuleIdを入力してください。", Constants.RED, true));
                Log.Error("rootModuleIdが未入力です。");
                return;
            }
            
            Log.Information("使用するrootModuleId: {ModuleId}", rootModuleId);

            if ((!contentData.TryGetProperty("content", out var contentArrayElement) && 
                 !contentData.TryGetProperty("modules", out contentArrayElement)) || 
                contentArrayElement.ValueKind != JsonValueKind.Array)
            {
                FLogger.Append(ELog.Error, () => FLogger.Text("コンテンツデータに有効なモジュール配列('content' または 'modules')が見つかりません。", Constants.RED, true));
                return;
            }
            var moduleInfo = contentArrayElement.EnumerateArray()
                .FirstOrDefault(x => x.TryGetProperty("moduleId", out var modId) &&
                                   modId.GetStringOrNumberValue() == rootModuleId);

            if (moduleInfo.Equals(default(JsonElement)))
            {
                FLogger.Append(ELog.Error, () => FLogger.Text("コンテンツデータにルートモジュールが見つかりません。", Constants.RED, true));
                return;
            }

            // Safely get binaries property
            JsonElement binaries;
            if (!moduleInfo.TryGetProperty("binaries", out binaries) || binaries.ValueKind != JsonValueKind.Object)
            {
                FLogger.Append(ELog.Error, () => FLogger.Text("モジュール情報に'binaries'オブジェクトが見つかりません。", Constants.RED, true));
                return;
            }

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
                // NOTE: アクセストークンはログに出さない（機密情報のため有無のみ記録）。
                Log.Information("認証ヘッダーを設定しました (Bearerトークンあり)");
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
