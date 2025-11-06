using System.Threading;
using System.Threading.Tasks;

using EpicManifestParser.Api;

using FModel.Framework;
using FModel.Settings;
using FModel.ViewModels.ApiEndpoints.Models;

using RestSharp;

using Serilog;

namespace FModel.ViewModels.ApiEndpoints;

public class EpicApiEndpoint : AbstractApiProvider
{
    private const string _OAUTH_URL = "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token";
    private const string _BASIC_TOKEN = "Basic M2Y2OWU1NmM3NjQ5NDkyYzhjYzI5ZjFhZjA4YThhMTI6YjUxZWU5Y2IxMjIzNGY1MGE2OWVmYTY3ZWY1MzgxMmU=";
    private const string _APP_URL = "https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/public/assets/v2/platform/Windows/namespace/fn/catalogItem/4fe75bbc5a674f4f9b356b5c90567da5/app/Fortnite/label/Live";

    public EpicApiEndpoint(RestClient client) : base(client) { }

    public async Task<ManifestInfo> GetManifestAsync(CancellationToken token)
    {
        // Fortnite Live専用のアクセストークンを生成
        var authRequest = new FRestRequest(_OAUTH_URL, Method.Post);
        authRequest.AddHeader("Authorization", _BASIC_TOKEN);
        authRequest.AddHeader("Content-Type", "application/x-www-form-urlencoded");
        authRequest.AddParameter("grant_type", "client_credentials");
        var authResponse = await _client.ExecuteAsync<AuthResponse>(authRequest, token).ConfigureAwait(false);
        Log.Information("[{Method}] [{Status}({StatusCode})] '{Resource}' Response: {Content}", authRequest.Method, authResponse.StatusDescription, (int) authResponse.StatusCode, authResponse.ResponseUri?.OriginalString, authResponse.Content);

        if (!authResponse.IsSuccessful || string.IsNullOrEmpty(authResponse.Data?.AccessToken))
        {
            Log.Warning("Fortnite Live専用のアクセストークン発行に失敗しました。");
            return null;
        }

        var accessToken = authResponse.Data.AccessToken;
        Log.Information("Fortnite Live専用のアクセストークンが正常に発行されました。");

        var request = new FRestRequest(_APP_URL);
        request.AddHeader("Authorization", $"Bearer {accessToken}");
        Log.Information("リクエストに使用するアクセストークン: {AccessToken}", accessToken);

        var response = await _client.ExecuteAsync(request, token).ConfigureAwait(false);
        Log.Information("[{Method}] [{Status}({StatusCode})] '{Resource}' Response: {Content}", request.Method, response.StatusDescription, (int) response.StatusCode, response.ResponseUri?.OriginalString, response.Content);
        if (response.IsSuccessful)
        {
            Log.Information("Epic Games APIからマニフェストを正常に取得しました。");
            return ManifestInfo.Deserialize(response.RawBytes);
        }

        Log.Warning("Epic Games APIからのマニフェスト取得に失敗しました。");
        return null;
    }

    public ManifestInfo GetManifest(CancellationToken token)
    {
        return GetManifestAsync(token).GetAwaiter().GetResult();
    }

    public async Task VerifyAuth(CancellationToken token)
    {
        if (await IsExpired().ConfigureAwait(false))
        {
            var auth = await GetAuthAsync(token).ConfigureAwait(false);
            if (auth != null)
            {
                UserSettings.Default.LastAuthResponse = auth;
            }
        }
    }

    private async Task<AuthResponse> GetAuthAsync(CancellationToken token)
    {
        var request = new FRestRequest(_OAUTH_URL, Method.Post);
        request.AddHeader("Authorization", _BASIC_TOKEN);
        request.AddHeader("Content-Type", "application/x-www-form-urlencoded");
        request.AddParameter("grant_type", "client_credentials");
        var response = await _client.ExecuteAsync<AuthResponse>(request, token).ConfigureAwait(false);
        Log.Information("[{Method}] [{Status}({StatusCode})] '{Resource}' Response: {Content}", request.Method, response.StatusDescription, (int) response.StatusCode, response.ResponseUri?.OriginalString, response.Content);
        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Data?.AccessToken))
        {
            Log.Information("新しいEpic Games APIアクセストークンが正常に発行されました。");
            Log.Information("発行されたアクセストークン: {AccessToken}", response.Data.AccessToken);
        }
        else
        {
            Log.Warning("Epic Games APIアクセストークンの発行に失敗しました。");
        }
        return response.Data;
    }

    private async Task<bool> IsExpired()
    {
        if (UserSettings.Default.LastAuthResponse?.AccessToken is null or "") return true;
        var request = new FRestRequest("https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify");
        request.AddHeader("Authorization", $"bearer {UserSettings.Default.LastAuthResponse.AccessToken}");
        var response = await _client.ExecuteGetAsync(request).ConfigureAwait(false);
        return !response.IsSuccessful;
    }
}
