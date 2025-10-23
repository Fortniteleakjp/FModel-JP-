using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FModel.Settings;
using FModel.Views.Resources.Controls;

namespace FModel.Services;

public class EpicGamesAuthService
{
    private const string ClientAuth = "Basic OThmN2U0MmMyZTNhNGY4NmE3NGViNDNmYmI0MWVkMzk6MGEyNDQ5YTItMDAxYS00NTFlLWFmZWMtM2U4MTI5MDFjNGQ3";

    private static readonly string DeviceAuthPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FModel", // Use a dedicated folder for FModel
        "deviceAuth.json"
    );

    private readonly HttpClient _httpClient;

    public EpicGamesAuthService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        Directory.CreateDirectory(Path.GetDirectoryName(DeviceAuthPath)!);
    }

    private async Task<T> SendJsonAsync<T>(HttpRequestMessage request)
    {
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var responseBody = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(responseBody);
    }

    public async Task<AuthData?> RefreshTokenAsync(AuthData savedAuth)
    {
        var bodyContent = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "device_auth"),
            new KeyValuePair<string, string>("account_id", savedAuth.AccountId),
            new KeyValuePair<string, string>("device_id", savedAuth.DeviceId),
            new KeyValuePair<string, string>("secret", savedAuth.Secret),
            new KeyValuePair<string, string>("token_type", "eg1")
        });

        var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token")
        {
            Content = bodyContent
        };
        
        tokenReq.Headers.Add("Authorization", ClientAuth);

        try
        {
            var tokenResponse = await SendJsonAsync<JsonElement>(tokenReq);

            if (!tokenResponse.TryGetProperty("access_token", out var accessTokenElement) || accessTokenElement.GetString() is not { } newAccessToken)
            {
                FLogger.Append(ELog.Error, () => FLogger.Text("Failed to get 'access_token' from refresh response.", Constants.RED, true));
                return null;
            }

            if (!tokenResponse.TryGetProperty("expires_in", out var expiresInElement) || expiresInElement.ValueKind != JsonValueKind.Number)
            {
                FLogger.Append(ELog.Error, () => FLogger.Text("Failed to get 'expires_in' from refresh response.", Constants.RED, true));
                return null;
            }

            savedAuth.AccessToken = newAccessToken;
            savedAuth.ExpiresAt = DateTime.Now.AddSeconds(expiresInElement.GetInt32());
            await File.WriteAllTextAsync(DeviceAuthPath, JsonSerializer.Serialize(savedAuth, new JsonSerializerOptions { WriteIndented = true }));
            return savedAuth;
        }
        catch (Exception ex)
        {
            FLogger.Append(ELog.Warning, () => FLogger.Text($"Device auth expired or failed to refresh, re-logging. Exception: {ex}", Constants.WHITE, true));
            return null;
        }
    }

    public async Task<AuthData?> LoginAsync()
    {
        // Log initial token request
        FLogger.Append(ELog.Information, () => FLogger.Text("Requesting initial client credentials token.", Constants.WHITE, true));
        var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token")
        {
            Content = new StringContent("grant_type=client_credentials", Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        tokenReq.Headers.Add("Authorization", ClientAuth);
        var tokenResponse = await SendJsonAsync<JsonElement>(tokenReq);

        if (!tokenResponse.TryGetProperty("access_token", out var accessTokenElement) || accessTokenElement.GetString() is not { } accessToken)
        {
            FLogger.Append(ELog.Error, () => FLogger.Text("Failed to get initial 'access_token'.", Constants.RED, true));
            return null;
        }

        FLogger.Append(ELog.Information, () => FLogger.Text("Initial client credentials token obtained.", Constants.WHITE, true));

        // Log device authorization request
        FLogger.Append(ELog.Information, () => FLogger.Text("Requesting device authorization.", Constants.WHITE, true));
        var deviceReq = new HttpRequestMessage(HttpMethod.Post, "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/deviceAuthorization")
        {
            Content = new StringContent("prompt=login", Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        deviceReq.Headers.Add("Authorization", $"Bearer {accessToken}");
        var device = await SendJsonAsync<JsonElement>(deviceReq);

        if (!device.TryGetProperty("verification_uri_complete", out var verificationUriProperty) ||
            verificationUriProperty.GetString() is not { } url)
        {
            FLogger.Append(ELog.Error, () => FLogger.Text("Failed to get verification URL.", Constants.RED, true));
            return null;
        }

        // Log verification URL
        FLogger.Append(ELog.Information, () => FLogger.Text($"Verification URL: {url}", Constants.WHITE, true));
        FLogger.Append(ELog.Information, () => FLogger.Text("Please login in the browser window that just opened.", Constants.WHITE, true));
        
        FLogger.Append(ELog.Information, () => FLogger.Text("Attempting to start browser process...", Constants.WHITE, true));
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            FLogger.Append(ELog.Information, () => FLogger.Text("Browser process started successfully.", Constants.GREEN, true));
        }
        catch (Exception ex)
        {
            FLogger.Append(ELog.Error, () => FLogger.Text($"Failed to start browser process: {ex}", Constants.RED, true));
            // Continue execution, user might manually open the URL
        }

        if (!device.TryGetProperty("expires_in", out var expiresInElement) || expiresInElement.ValueKind != JsonValueKind.Number)
        {
            FLogger.Append(ELog.Error, () => FLogger.Text("Failed to get 'expires_in' from device auth response.", Constants.RED, true));
            return null;
        }
        var deadline = DateTimeOffset.UtcNow.AddSeconds(expiresInElement.GetInt32());

        if (!device.TryGetProperty("interval", out var intervalElement) || intervalElement.ValueKind != JsonValueKind.Number)
        {
            FLogger.Append(ELog.Error, () => FLogger.Text("Failed to get 'interval' from device auth response.", Constants.RED, true));
            return null;
        }
        var interval = intervalElement.GetInt32(); // Polling interval

        if (!device.TryGetProperty("device_code", out var deviceCodeElement) || deviceCodeElement.GetString() is not { } deviceCode)
        {
            FLogger.Append(ELog.Error, () => FLogger.Text("Failed to get 'device_code' from device auth response.", Constants.RED, true));
            return null;
        }

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval * 1000);

            var pollingBody = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "device_code"),
                new KeyValuePair<string, string>("device_code", deviceCode)
            });

            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token")
                {
                    Content = pollingBody
                };
                req.Headers.Add("Authorization", ClientAuth);
                FLogger.Append(ELog.Information, () => FLogger.Text($"Polling for token with device code: {deviceCode}", Constants.WHITE, true));
                var token = await SendJsonAsync<JsonElement>(req);

                if (!token.TryGetProperty("displayName", out var displayNameElement) || displayNameElement.GetString() is not { } displayName)
                {
                    FLogger.Append(ELog.Warning, () => FLogger.Text("Polling successful, but 'displayName' not found yet. Continuing to poll.", Constants.WHITE, true));
                    continue;
                }
                
                FLogger.Append(ELog.Information, () => FLogger.Text($"Authentication successful for user: {displayName}", Constants.GREEN, true));

                if (!token.TryGetProperty("account_id", out var accountIdElement) || accountIdElement.GetString() is not { } accountId ||
                    !token.TryGetProperty("access_token", out var finalAccessTokenElement) || finalAccessTokenElement.GetString() is not { } finalAccessToken ||
                    !token.TryGetProperty("expires_in", out var finalExpiresInElement) || finalExpiresInElement.ValueKind != JsonValueKind.Number)
                {
                    FLogger.Append(ELog.Error, () => FLogger.Text("Authentication succeeded, but failed to get required token properties.", Constants.RED, true));
                    return null;
                }

                var authReq = new HttpRequestMessage(HttpMethod.Post, $"https://account-public-service-prod.ol.epicgames.com/account/api/public/account/{accountId}/deviceAuth");
                authReq.Headers.Add("Authorization", $"Bearer {finalAccessToken}");
                FLogger.Append(ELog.Information, () => FLogger.Text($"Requesting device auth for account ID: {accountId}", Constants.WHITE, true));
                var deviceAuth = await SendJsonAsync<JsonElement>(authReq);

                var authData = new AuthData
                {
                    DisplayName = displayName,
                    AccountId = accountId,
                    DeviceId = deviceAuth.GetProperty("deviceId").GetString()!,
                    Secret = deviceAuth.GetProperty("secret").GetString()!,
                    AccessToken = finalAccessToken,
                    ExpiresAt = DateTime.Now.AddSeconds(finalExpiresInElement.GetInt32())
                };

                await File.WriteAllTextAsync(DeviceAuthPath, JsonSerializer.Serialize(authData, new JsonSerializerOptions { WriteIndented = true }));
                FLogger.Append(ELog.Information, () => FLogger.Text("Device auth data saved.", Constants.GREEN, true));
                return authData;
            }
            catch (Exception ex)
            {
                // Log polling errors but continue trying
                FLogger.Append(ELog.Warning, () => FLogger.Text($"Polling error: {ex}", Constants.WHITE, true));
            }
        }

        throw new Exception("Login timed out.");
    }

    public static async Task<AuthData?> LoadDeviceAuthAsync()
    {
        if (!File.Exists(DeviceAuthPath)) return null;
        
        var json = await File.ReadAllTextAsync(DeviceAuthPath);
        var authData = JsonSerializer.Deserialize<AuthData>(json);
        return authData;
    }
}
