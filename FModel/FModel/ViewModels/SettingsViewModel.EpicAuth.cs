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

            var accessToken = UserSettings.Default.LastAuthResponse?.AccessToken;
            if (accessToken is null)
        {
            RetrievedAesKey = "Authentication is required.";
            Log.Warning("認証が必要です: AccessTokenがありません");
            FLogger.Append(ELog.Warning, () => FLogger.Text(RetrievedAesKey, Constants.RED, true));
            return;
        }

        try
        {
            using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            // Get latest build version
            Log.Information("最新ビルドバージョンを取得中...");
            var mappingsData = await httpClient.GetFromJsonAsync<JsonElement?>("https://api.fortniteapi.com/v1/mappings");

            string versionStr = null;
            if (mappingsData.HasValue)
            {
                var root = mappingsData.Value;
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                {
                    // Handle array response: [ { "meta": { "version": "..." } } ]
                    if (root[0].TryGetProperty("meta", out var meta) && meta.TryGetProperty("version", out var versionElement))
                        versionStr = versionElement.GetString();
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    // Handle direct version property or wrapped "data" array
                    if (root.TryGetProperty("version", out var versionElement))
                        versionStr = versionElement.GetString();
                    else if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 &&
                             data[0].TryGetProperty("meta", out var meta) && meta.TryGetProperty("version", out var dataVersion))
                        versionStr = dataVersion.GetString();
                }
            }

            if (string.IsNullOrEmpty(versionStr)) throw new Exception("Failed to get version from mappings data.");
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
            
                var contentResponse = await httpClient.GetAsync(contentUrl);
                contentResponse.EnsureSuccessStatusCode();
                var contentData = await contentResponse.Content.ReadFromJsonAsync<JsonElement?>();

                if (!contentData.HasValue || contentData.Value.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("API response for map content was not a valid JSON object.");
                }

            Log.Information("マップコンテンツ情報取得完了");

            UpdateAvailableRootModuleIds(contentData.Value);

            if (AvailableRootModuleIds.Count == 0)
            {
                RetrievedAesKey = "rootModuleId候補が見つかりませんでした。";
                FLogger.Append(ELog.Error, () => FLogger.Text(RetrievedAesKey, Constants.RED, true));
                return;
            }

            var selectedRootModuleId = RootModuleIdInput?.Trim();
            if (string.IsNullOrWhiteSpace(selectedRootModuleId) ||
                !AvailableRootModuleIds.Any(x => string.Equals(x, selectedRootModuleId, StringComparison.Ordinal)))
            {
                RootModuleIdInput = string.Empty;
                RetrievedAesKey = "rootModuleId候補を表示しました。選択して再度実行してください。";
                FLogger.Append(ELog.Warning, () => FLogger.Text(RetrievedAesKey, Constants.YELLOW, true));
                return;
            }

            if (contentData.HasValue && contentData.Value.TryGetProperty("errorCode", out var errorCode) &&
                errorCode.GetString() == "errors.com.epicgames.content-service.unexpected_link_type")
            {
                RetrievedAesKey = "1.0 maps have no encryption.";
                Log.Information("1.0マップは暗号化されていません: {MapCode}", mapCode);
                FLogger.Append(ELog.Warning, () => FLogger.Text(RetrievedAesKey, Constants.YELLOW, true));
                return;
            }

                // Safely get moduleId and version
                string moduleId = null;
                string version = null;
                bool isEncrypted = false;

                if (contentData.Value.TryGetProperty("isEncrypted", out var isEncryptedElement) &&
                    isEncryptedElement.ValueKind == JsonValueKind.True)
                {
                    isEncrypted = true;
                }

                if (contentData.Value.TryGetProperty("resolved", out var resolvedElement) &&
                    resolvedElement.ValueKind == JsonValueKind.Object &&
                    resolvedElement.TryGetProperty("root", out var rootElement) &&
                    rootElement.ValueKind == JsonValueKind.Object)
                {
                    if (rootElement.TryGetProperty("moduleId", out var moduleIdElement))
                    {
                        moduleId = moduleIdElement.GetStringOrNumberValue();
                    }
                    if (rootElement.TryGetProperty("version", out var versionElement))
                    {
                        version = versionElement.GetStringOrNumberValue();
                    }
                }

                if (isEncrypted)
            {
                Log.Information("マップが暗号化されています。AESキーを取得中...");
                if (string.IsNullOrEmpty(moduleId) || string.IsNullOrEmpty(version))
                    throw new Exception("Failed to extract moduleId or version from content data for encrypted map.");
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
                string key = null;
                if (keyData != null && keyData.Length > 0 &&
                    keyData[0].ValueKind == JsonValueKind.Object &&
                    keyData[0].TryGetProperty("key", out var keyElement) &&
                    keyElement.ValueKind == JsonValueKind.Object &&
                    keyElement.TryGetProperty("Key", out var actualKeyElement))
                {
                    key = actualKeyElement.GetString();
                }
                if (string.IsNullOrEmpty(key)) throw new Exception("Failed to get key from key data.");
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

    private void UpdateAvailableRootModuleIds(JsonElement contentData)
    {
        var previousSelection = RootModuleIdInput?.Trim();

        if ((!contentData.TryGetProperty("content", out var contentArrayElement) &&
             !contentData.TryGetProperty("modules", out contentArrayElement)) ||
            contentArrayElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var ids = contentArrayElement.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("moduleId", out _))
            .Select(x => x.GetProperty("moduleId").GetStringOrNumberValue())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        AvailableRootModuleIds.Clear();

        foreach (var id in ids)
            AvailableRootModuleIds.Add(id);

        if (!string.IsNullOrWhiteSpace(previousSelection) &&
            ids.Any(x => string.Equals(x, previousSelection, StringComparison.Ordinal)))
        {
            RootModuleIdInput = previousSelection;
        }
        else if (!string.IsNullOrWhiteSpace(RootModuleIdInput))
        {
            RootModuleIdInput = string.Empty;
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
}
