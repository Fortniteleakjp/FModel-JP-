using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

class Program
{
    [STAThread]
    static async Task Main()
    {
        try
        {
            var savedAuth = await AuthService.LoadDeviceAuthAsync();
            if (savedAuth == null)
                savedAuth = await AuthService.LoginAsync();

            if (savedAuth == null)
            {
                throw new Exception("Failed to login.");
            }

            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine("Credit:Krowe moh");
            Console.WriteLine("C# Port by Landmark");
            Console.ResetColor();
            Console.Write("マップコードを入力してください 例:1234-5678-9012: ");
            var mapCode = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(mapCode))
                throw new Exception("Map code cannot be empty");

            //最新ビルド情報取得
            var mappingsData = await EpicGamesApiService.GetJsonAsync<JsonElement>("https://fortnitecentral.genxgames.gg/api/v1/mappings");

            string versionStr = mappingsData.GetProperty("version").GetString()
                ?? throw new Exception("Failed to get version from mappings data.");

            Console.WriteLine($"Version: {versionStr}");

            //バージョン取得
            var match = Regex.Match(versionStr, @"Release-(\d+)\.(\d+)-CL-(\d+)");
            if (!match.Success)
                throw new Exception($"Failed to parse version string: {versionStr}");

            var major = match.Groups[1].Value;
            var minor = match.Groups[2].Value;
            var cl = match.Groups[3].Value;

        RetryContent:
            // マップ情報取得
            var contentUrl = $"https://content-service.bfda.live.use1a.on.epicgames.com/api/content/v2/link/{mapCode}/cooked-content-package?role=client&platform=windows&major={major}&minor={minor}&patch={cl}";
            var request = new HttpRequestMessage(HttpMethod.Get, contentUrl);
            request.Headers.Add("Authorization", $"bearer {savedAuth.AccessToken}");

            try
            {
                var contentData = await EpicGamesApiService.SendJsonAsync<JsonElement>(request);

                if (contentData.TryGetProperty("errorCode", out var errorCode) &&
                    errorCode.GetString() == "errors.com.epicgames.content-service.unexpected_link_type")
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Warning: 1.0 maps have no encryption and can't be downloaded (unexpected_link_type)");
                    Console.ResetColor();
                    return;
                }

                if (contentData.GetProperty("isEncrypted").GetBoolean())
                {
                    var moduleId = contentData.GetProperty("resolved").GetProperty("root").GetProperty("moduleId").ToString();
                    var version = contentData.GetProperty("resolved").GetProperty("root").GetProperty("version").ToString();

                    var payload = $"[{{\"moduleId\":\"{moduleId}\",\"version\":\"{version}\"}}]";
                    var keyReq = new HttpRequestMessage(HttpMethod.Post, "https://content-service.bfda.live.use1a.on.epicgames.com/api/content/v4/module/key/batch")
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json")
                    };

                    keyReq.Headers.Add("Authorization", $"bearer {savedAuth.AccessToken}");

                    var keyData = await EpicGamesApiService.SendJsonAsync<JsonElement[]>(keyReq);
                    var key = keyData[0].GetProperty("key").GetProperty("Key").ToString();
                    var guid = keyData[0].GetProperty("key").GetProperty("Guid").ToString();

                    // AES Key
                    var aesKey = "0x" + BitConverter.ToString(Convert.FromBase64String(key)).Replace("-", "");
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"AES Key: {aesKey}");

                    // クリップボードにコピー
                    ClipboardHelper.SetText(aesKey);
                    Console.WriteLine("AESキーをコピーしました");

                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"GUID: {guid}");
                    Console.ResetColor();
                }
                else
                {
                    Console.WriteLine("Map is not encrypted");
                }
            }
            catch (InvalidTokenException)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("トークンが期限切れです。再ログインしてください。");
                Console.ResetColor();

                savedAuth = await AuthService.LoginAsync();
                if (savedAuth == null)
                {
                    throw new Exception("Failed to re-login.");
                }
                goto RetryContent; //re
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during content retrieval: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }

        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
    }
}
