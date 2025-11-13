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

    // New API: fetches all cosmetics, then constructs the minimal AthenaProfile in memory for the user.
    public static string BuildProfileJson(string apiUrl)
    {
        try
        {
            var json = httpClient.GetStringAsync(apiUrl).GetAwaiter().GetResult();
            var deserializeOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            var allApiItems = new List<ApiItem>();

            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataElement))
                {
                    if (dataElement.ValueKind == JsonValueKind.Array)
                    {
                        var arr = JsonSerializer.Deserialize<List<ApiItem>>(dataElement.GetRawText(), deserializeOptions);
                        if (arr != null) allApiItems.AddRange(arr);
                    }
                    else if (dataElement.ValueKind == JsonValueKind.Object)
                    {
                        
                        foreach (var prop in dataElement.EnumerateObject())
                        {
                            var v = prop.Value;
                            if (v.ValueKind == JsonValueKind.Array)
                            {
                                var list = JsonSerializer.Deserialize<List<ApiItem>>(v.GetRawText(), deserializeOptions);
                                if (list != null) allApiItems.AddRange(list);
                            }
                            else if (v.ValueKind == JsonValueKind.Object)
                            {
                                
                                try
                                {
                                    var single = JsonSerializer.Deserialize<ApiItem>(v.GetRawText(), deserializeOptions);
                                    if (single != null && !string.IsNullOrEmpty(single.Id))
                                    {
                                        allApiItems.Add(single);
                                        continue;
                                    }
                                }
                                catch { }

                                
                                try
                                {
                                    var map = JsonSerializer.Deserialize<Dictionary<string, ApiItem>>(v.GetRawText(), deserializeOptions);
                                    if (map != null)
                                    {
                                        foreach (var item in map.Values)
                                            if (item != null && !string.IsNullOrEmpty(item.Id))
                                                allApiItems.Add(item);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    // ルートが配列になっているケース
                    var arr = JsonSerializer.Deserialize<List<ApiItem>>(root.GetRawText(), deserializeOptions);
                    if (arr != null) allApiItems.AddRange(arr);
                }

                
                if (allApiItems.Count == 0)
                {
                    ExtractApiItemsFromElement(root, allApiItems, deserializeOptions);
                }
            }

            
            if (allApiItems.Count == 0)
            {
                try
                {
                    var typed = JsonSerializer.Deserialize<ApiDataRoot>(json, deserializeOptions);
                    if (typed?.Data != null)
                    {
                        foreach (var list in typed.Data.Values)
                            if (list != null)
                                allApiItems.AddRange(list);
                    }
                }
                catch
                {
                    try
                    {
                        var flexible = JsonSerializer.Deserialize<ApiDataRootFlexible>(json, deserializeOptions);
                        if (flexible?.Data != null)
                        {
                            foreach (var elem in flexible.Data.Values)
                            {
                                try
                                {
                                    var list = JsonSerializer.Deserialize<List<ApiItem>>(elem.GetRawText(), deserializeOptions);
                                    if (list != null)
                                        allApiItems.AddRange(list);
                                }
                                catch
                                {
                                    // skip
                                }
                            }
                        }
                    }
                    catch
                    {

                    }
                }
            }

            Console.WriteLine($"[Info] Found {allApiItems.Count} API items.");

            var profile = AthenaProfileBuilder.BuildBaseTemplate();
            var items = new Dictionary<string, AthenaItem>(profile.Items);

            // Populate items from API results.
            foreach (var api in allApiItems)
            {
                if (api == null || string.IsNullOrEmpty(api.Id))
                    continue;

                // Build a TemplateId consistent with other entries: "{backendValue}:{id}".
                var backend = api.Type?.BackendValue ?? string.Empty;
                var templateId = string.IsNullOrEmpty(backend) ? api.Id : $"{backend}:{api.Id}";

                // Avoid duplicates and skip reserved keys already in template
                if (items.ContainsKey(templateId) || items.ContainsKey(api.Id))
                    continue;

                var newItem = new AthenaItem
                {
                    TemplateId = templateId,
                    Attributes = new ItemAttributes
                    {
                        Variants = AthenaProfileBuilder.BuildVariants(api.Variants),
                        Favorite = false
                    },
                    Quantity = 1
                };

                items[templateId] = newItem;
            }

            profile.Items = items;

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = new SnakeCaseNamingPolicy(),
                DictionaryKeyPolicy = null, // Items のキーはそのまま
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNameCaseInsensitive = true
            };

            return JsonSerializer.Serialize(profile, options);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error during BuildProfileJson] {ex.Message}");
            throw;
        }
    }

    private static void ExtractApiItemsFromElement(JsonElement element, List<ApiItem> collector, JsonSerializerOptions opts)
    {
        try
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("id", out var idProp))
                {
                    try
                    {
                        var ai = JsonSerializer.Deserialize<ApiItem>(element.GetRawText(), opts);
                        if (ai != null && !string.IsNullOrEmpty(ai.Id))
                        {
                            collector.Add(ai);
                            return;
                        }
                    }
                    catch
                    {
                       
                    }
                }

                foreach (var prop in element.EnumerateObject())
                {
                    ExtractApiItemsFromElement(prop.Value, collector, opts);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    ExtractApiItemsFromElement(item, collector, opts);
                }
            }
        }
        catch
        {
            
        }
    }
}

public class ApiDataRootFlexible
{
    [JsonPropertyName("data")]
    public Dictionary<string, JsonElement> Data { get; set; }
}

public class SnakeCaseNamingPolicy : JsonNamingPolicy
{
    public override string ConvertName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        var result = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                    result.Append('_');
                result.Append(char.ToLowerInvariant(c));
            }
            else
            {
                result.Append(c);
            }
        }
        return result.ToString();
    }
}
