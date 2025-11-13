using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

class AthenaGenerator
{
    private static readonly HttpClient httpClient = new HttpClient();

    [STAThread]
    public static void Main()
    {
        try
        {
            Generate("https://fortnite-api.com/v2/cosmetics");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[ERROR] " + ex);
        }
    }

    // Backwards-compatible: still provides a method that shows the save dialog from a new STA thread.
    public static void Generate(string apiUrl)
    {
        try
        {
            var json = BuildProfileJson(apiUrl);

            // STAスレッドでファイルダイアログを呼び出す
            string savePath = null;
            Thread staThread = new Thread(() =>
            {
                savePath = FileUtil.SelectSaveFile();
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();

            if (savePath != null)
            {
                File.WriteAllText(savePath, json);
                Process.Start("explorer.exe", $"/select,\"{savePath}\"");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[ERROR] " + ex);
            throw;
        }
    }

    // New API: build and return profile JSON string (no UI interaction)
    public static string BuildProfileJson(string apiUrl)
    {
        return BuildAsync(apiUrl).GetAwaiter().GetResult();
    }

    static async Task<string> BuildAsync(string apiUrl)
    {
        var profile = AthenaProfileBuilder.BuildBaseTemplate();

        var backendFixMap = new Dictionary<string, string>
        {
            ["AthenaEmoji"] = "AthenaDance",
            ["AthenaSpray"] = "AthenaDance",
            ["AthenaToy"] = "AthenaDance",
            ["AthenaPetCarrier"] = "AthenaBackpack",
            ["AthenaPet"] = "AthenaBackpack",
            ["SparksDrum"] = "SparksDrums",
            ["SparksMic"] = "SparksMicrophone"
        };

        try
        {
            string json = await httpClient.GetStringAsync(apiUrl);
            var doc = JsonSerializer.Deserialize<ApiDataRootFlexible>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (doc?.Data != null)
            {
                //再帰処理
                void ProcessArray(JsonElement arr, string parentKey)
                {
                    if (arr.ValueKind != JsonValueKind.Array) return;
                    List<ApiItem> items = null;
                    try
                    {
                        items = arr.Deserialize<List<ApiItem>>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                    catch
                    {
                        return;
                    }

                    if (items == null) return;

                    foreach (var item in items)
                    {
                        if (item == null) continue;
                        if (item.Id == null) continue;
                        if (item.Id.ToLower().Contains("random")) continue;
                        if (item.Type == null) continue;

                        //parentKeyがtracksの場合特例を適用
                        if (string.Equals(parentKey, "tracks", StringComparison.OrdinalIgnoreCase))
                        {
                            item.Type.BackendValue = "SparksSong";
                        }

                        if (item.Type.BackendValue != null && backendFixMap.ContainsKey(item.Type.BackendValue))
                        {
                            item.Type.BackendValue = backendFixMap[item.Type.BackendValue];
                        }

                        string itemId = $"{item.Type.BackendValue}:{item.Id}";
                        profile.Items[itemId] = new AthenaItem
                        {
                            TemplateId = itemId,
                            Attributes = new ItemAttributes
                            {
                                MaxLevelBonus = 0,
                                Level = 1,
                                ItemSeen = true,
                                Xp = 0,
                                Variants = AthenaProfileBuilder.BuildVariants(item.Variants),
                                Favorite = false
                            },
                            Quantity = 1
                        };
                    }
                }

                void Recurse(JsonElement element, string parentKey = null)
                {
                    if (element.ValueKind == JsonValueKind.Array)
                    {
                        ProcessArray(element, parentKey);
                    }
                    else if (element.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in element.EnumerateObject())
                        {
                            
                            Recurse(prop.Value, prop.Name);
                        }
                    }
                    
                }

                
                foreach (var kv in doc.Data)
                {
                    Recurse(kv.Value, kv.Key);
                }
            }

            return JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error during BuildAsync] {ex.Message}");
            throw;
        }
    }
}

public class ApiDataRootFlexible
{
    [JsonPropertyName("data")]
    public Dictionary<string, JsonElement> Data { get; set; }
}
