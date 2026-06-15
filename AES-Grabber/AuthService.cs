using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Diagnostics;

public class AuthService
{
    private const string ClientAuth = "Basic OThmN2U0MmMyZTNhNGY4NmE3NGViNDNmYmI0MWVkMzk6MGEyNDQ5YTItMDAxYS00NTFlLWFmZWMtM2U4MTI5MDFjNGQ3";

    // 本体(FModel)と同じパス・同じDPAPIエントロピーを使い、ログイン情報を共有できるようにする。
    private static readonly string DeviceAuthPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FModel",
        "deviceAuth.json"
    );

    // 旧バージョンが LocalAppData 直下に平文保存していたファイル（移行・削除対象）。
    private static readonly string LegacyDeviceAuthPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "deviceAuth.json"
    );

    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("FModel.DeviceAuth.v1");

    private static async Task SaveAuthDataAsync(AuthData authData)
    {
        // DeviceId/Secret は長期再認証に使える機密情報のため Windows DPAPI(CurrentUser) で暗号化して保存する。
        Directory.CreateDirectory(Path.GetDirectoryName(DeviceAuthPath)!);
        var json = JsonSerializer.Serialize(authData, new JsonSerializerOptions { WriteIndented = true });
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), DpapiEntropy, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(DeviceAuthPath, protectedBytes);
    }

    public static async Task<AuthData?> RefreshTokenAsync(AuthData savedAuth)
    {
        var bodyString = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "device_auth"),
            new KeyValuePair<string, string>("account_id", savedAuth.AccountId),
            new KeyValuePair<string, string>("device_id", savedAuth.DeviceId),
            new KeyValuePair<string, string>("secret", savedAuth.Secret),
            new KeyValuePair<string, string>("token_type", "eg1")
        }).ReadAsStringAsync().Result;

        var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token")
        {
            Content = new StringContent(bodyString, Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        
        tokenReq.Headers.Add("Authorization", ClientAuth);

        try
        {
            var tokenResponse = await EpicGamesApiService.SendJsonAsync<JsonElement>(tokenReq);

            savedAuth.AccessToken = tokenResponse.GetProperty("access_token").GetString() ?? savedAuth.AccessToken;

            return savedAuth;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Warning: デバイス認証の期限切れ、またはリフレッシュ失敗。再ログインします。({ex.Message})");
            Console.ResetColor();
            return null;
        }
    }

    public static async Task<AuthData?> LoginAsync()
    {
        var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token")
        {
            Content = new StringContent("grant_type=client_credentials", Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        tokenReq.Headers.Add("Authorization", ClientAuth);
        var tokenResponse = await EpicGamesApiService.SendJsonAsync<JsonElement>(tokenReq);
        var accessToken = tokenResponse.GetProperty("access_token").ToString();

        var deviceReq = new HttpRequestMessage(HttpMethod.Post, "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/deviceAuthorization")
        {
            Content = new StringContent("prompt=login", Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        deviceReq.Headers.Add("Authorization", $"Bearer {accessToken}");
        var device = await EpicGamesApiService.SendJsonAsync<JsonElement>(deviceReq);

        var url = device.GetProperty("verification_uri_complete").ToString();
        Console.WriteLine($"リダイレクト先でログインしてください");
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); 

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
                token = await EpicGamesApiService.SendJsonAsync<JsonElement>(req);

                if (token.TryGetProperty("displayName", out var displayName))
                {
                    Console.Write("認証成功 ユーザーネーム: ");
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Write(displayName.ToString());
                    Console.ResetColor();
                    Console.WriteLine();
                }
                else
                    continue;

                var accountId = token.GetProperty("account_id").ToString();
                var authReq = new HttpRequestMessage(HttpMethod.Post, $"https://account-public-service-prod.ol.epicgames.com/account/api/public/account/{accountId}/deviceAuth");
                authReq.Headers.Add("Authorization", $"Bearer {token.GetProperty("access_token").ToString()}");
                var deviceAuth = await EpicGamesApiService.SendJsonAsync<JsonElement>(authReq);

                var authData = new AuthData
                {
                    DisplayName = displayName.ToString(),
                    AccountId = accountId,
                    DeviceId = deviceAuth.GetProperty("deviceId").ToString(),
                    Secret = deviceAuth.GetProperty("secret").ToString(),
                    AccessToken = token.GetProperty("access_token").ToString()
                };

                await SaveAuthDataAsync(authData);
                return authData;
            }
            catch
            {
                // ignore
            }
        }

        throw new Exception("Login timed out.");
    }

    public static async Task<AuthData?> LoadDeviceAuthAsync()
    {
        // 旧バージョンが LocalAppData 直下に平文保存したファイルを暗号化形式へ移行する。
        if (!File.Exists(DeviceAuthPath) && File.Exists(LegacyDeviceAuthPath))
        {
            try
            {
                var legacyJson = await File.ReadAllTextAsync(LegacyDeviceAuthPath);
                var legacyAuth = JsonSerializer.Deserialize<AuthData>(legacyJson);
                if (legacyAuth != null)
                {
                    await SaveAuthDataAsync(legacyAuth);
                    File.Delete(LegacyDeviceAuthPath); // 平文ファイルを削除
                }
            }
            catch
            {
                // 移行に失敗しても再ログインで回復するため無視。
            }
        }

        if (!File.Exists(DeviceAuthPath))
            return null;

        try
        {
            var raw = await File.ReadAllBytesAsync(DeviceAuthPath);

            string json;
            try
            {
                // 通常は DPAPI で暗号化されている。
                var unprotected = ProtectedData.Unprotect(raw, DpapiEntropy, DataProtectionScope.CurrentUser);
                json = Encoding.UTF8.GetString(unprotected);
            }
            catch (CryptographicException)
            {
                // 平文JSONだった場合のフォールバック（暗号化形式へ移行）。
                json = Encoding.UTF8.GetString(raw);
                var migrated = JsonSerializer.Deserialize<AuthData>(json);
                if (migrated != null)
                    await SaveAuthDataAsync(migrated);
            }

            var authData = JsonSerializer.Deserialize<AuthData>(json);

            if (authData == null)
            {
                Console.WriteLine("Invalid JSON, will re-login…");
                return null;
            }

            return await RefreshTokenAsync(authData);
        }
        catch
        {
            Console.WriteLine("Invalid JSON, will re-login…");
            return null;
        }
    }
}
