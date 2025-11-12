using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
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
            RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[ERROR] " + ex);
        }
    }

    static async Task RunAsync()
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
            
            string json = await httpClient.GetStringAsync("https://fortnite-api.com/v2/cosmetics");
            var apiData = JsonSerializer.Deserialize<ApiDataRoot>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            int categoryCount = 0;
            foreach (var category in apiData.Data)
            {
                categoryCount++;
                if (category.Key == "lego" || category.Key == "beans")
                    continue;

                foreach (var item in category.Value)
                {
                    if (item.Id == null)
                        continue;
                    if (item.Id.ToLower().Contains("random"))
                        continue;
                    if (item.Type == null)
                        continue;

                    if (category.Key == "tracks")
                        item.Type.BackendValue = "SparksSong";
                    if (backendFixMap.ContainsKey(item.Type.BackendValue))
                        item.Type.BackendValue = backendFixMap[item.Type.BackendValue];

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
                await File.WriteAllTextAsync(savePath, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
                Process.Start("explorer.exe", $"/select,\"{savePath}\"");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error during RunAsync] {ex.Message}");
        }
    }
}
