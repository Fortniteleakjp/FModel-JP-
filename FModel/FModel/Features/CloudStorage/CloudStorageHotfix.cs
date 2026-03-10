using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using FModel.Framework;
using FModel.Services;
using FModel.Views.Resources.Controls;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace FModel.Features.CloudStorage
{
    /// <summary>
    /// クラウドストレージAPI経由でホットフィックスを適用する機能
    /// </summary>
    public class CloudStorageHotfix
    {
        // デフォルトのAPIエンドポイント
        public const string DefaultApiEndpoint = "https://fljpapi.jp/api/v2/cloudstorage/a22d837b6a2b46349421259c0a5411bf";

        private static readonly HttpClient _httpClient = new HttpClient();

        /// <summary>
        /// ホットフィックスの種類
        /// </summary>
        public enum HotfixType
        {
            RowUpdate,
            TableUpdate
        }

        /// <summary>
        /// ホットフィックスエントリ
        /// </summary>
        public class HotfixEntry
        {
            public string DataTable { get; set; }
            public HotfixType Type { get; set; }
            public string RowName { get; set; }
            public string PropertyName { get; set; }
            public string PropertyValue { get; set; }
            public string JsonData { get; set; }
        }

        /// <summary>
        /// ホットフィックスを適用します
        /// </summary>
        public static async Task ExecuteAsync(string apiEndpoint = DefaultApiEndpoint)
        {
            var selectedTab = ApplicationService.ApplicationView.CUE4Parse?.TabControl?.SelectedTab;
            if (selectedTab?.Entry == null || selectedTab.Entry is FakeGameFile)
            {
                FLogger.Append(ELog.Warning, () => FLogger.Text("先に対象ファイルを開いてから実行してください。", Constants.YELLOW));
                return;
            }

            if (selectedTab.Document == null || string.IsNullOrWhiteSpace(selectedTab.Document.Text))
            {
                FLogger.Append(ELog.Warning, () => FLogger.Text("現在のタブに適用可能なテキストデータがありません。", Constants.YELLOW));
                return;
            }

            // クラウドストレージから現在の設定を取得
            var currentData = await FetchCurrentDataAsync(apiEndpoint);
            if (currentData == "{}")
            {
                FLogger.Append(ELog.Warning, () => FLogger.Text("クラウドストレージからホットフィックスデータを取得できませんでした。", Constants.YELLOW));
                return;
            }

            var filePath = NormalizePath(selectedTab.Entry.PathWithoutExtension);
            var rawHotfixText = ExtractHotfixText(currentData, filePath);
            if (string.IsNullOrWhiteSpace(rawHotfixText))
            {
                FLogger.Append(ELog.Information, () => FLogger.Text($"このファイルに適用可能なクラウドホットフィックスは見つかりませんでした: {filePath}", Constants.WHITE));
                return;
            }

            var entries = ParseHotfixText(rawHotfixText)
                .Where(x => IsSameDataTable(filePath, x.DataTable))
                .ToList();

            if (entries.Count == 0)
            {
                FLogger.Append(ELog.Information, () => FLogger.Text($"対象DataTableのホットフィックス行が見つかりませんでした: {filePath}", Constants.WHITE));
                return;
            }

            var applyResult = MessageBox.Show(
                $"クラウドストレージにホットフィックスが見つかりました。\n\n対象: {filePath}\n件数: {entries.Count}\n\n現在開いている内容に適用しますか？",
                "クラウドストレージ ホットフィックス",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (applyResult != MessageBoxResult.Yes)
                return;

            try
            {
                var currentToken = JToken.Parse(selectedTab.Document.Text);
                var updated = ApplyEntriesToOpenedDocument(currentToken, entries, filePath);
                selectedTab.SetDocumentText(updated.ToString(Formatting.Indented), save: false, updateUi: false);

                FLogger.Append(ELog.Information, () => FLogger.Text($"クラウドホットフィックスを適用しました: {filePath}", Constants.GREEN));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to apply cloud storage hotfix to opened document");
                FLogger.Append(ELog.Error, () => FLogger.Text($"開いているドキュメントへの適用に失敗しました: {ex.Message}", Constants.RED));
            }
        }

        private static string ExtractHotfixText(string payload, string filePath)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return string.Empty;

            //生テキストなんだよな～これが
            var directLines = ExtractHotfixLinesFromRawText(payload);
            if (directLines.Count > 0)
                return string.Join(Environment.NewLine, directLines.Distinct(StringComparer.OrdinalIgnoreCase));

            try
            {
                var token = JToken.Parse(payload);

                // 旧形式: { "<path>": [...] } のような辞書
                if (token is JObject obj)
                {
                    var hotfixToken = FindHotfixForFile(obj, filePath);
                    if (hotfixToken != null)
                    {
                        if (hotfixToken.Type == JTokenType.String)
                            return hotfixToken.Value<string>();

                        var lines = new List<string>();
                        CollectHotfixLines(hotfixToken, lines);
                        return string.Join(Environment.NewLine, lines.Distinct(StringComparer.OrdinalIgnoreCase));
                    }
                }

                // 新形式: 配列またはネスト構造から +DataTable 行を抽出
                var fallbackLines = new List<string>();
                CollectHotfixLines(token, fallbackLines);
                return string.Join(Environment.NewLine, fallbackLines.Distinct(StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                // JSON解析に失敗しても、非JSONレスポンス内の +DataTable 行を再抽出
                var fallbackLines = ExtractHotfixLinesFromRawText(payload);
                if (fallbackLines.Count > 0)
                {
                    Log.Warning(ex, "Cloud storage payload is not JSON, fallback to raw hotfix lines");
                    return string.Join(Environment.NewLine, fallbackLines.Distinct(StringComparer.OrdinalIgnoreCase));
                }

                Log.Error(ex, "Failed to parse cloud storage payload");
                FLogger.Append(ELog.Error, () => FLogger.Text($"クラウドストレージのJSON解析に失敗しました: {ex.Message}", Constants.RED));
                return string.Empty;
            }
        }

        private static List<string> ExtractHotfixLinesFromRawText(string text)
        {
            var lines = new List<string>();
            var split = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var raw in split)
            {
                var line = raw.Trim();
                if (line.StartsWith("+DataTable=", StringComparison.OrdinalIgnoreCase))
                    lines.Add(line);
            }

            return lines;
        }

        private static void CollectHotfixLines(JToken token, List<string> lines)
        {
            if (token == null)
                return;

            switch (token.Type)
            {
                case JTokenType.String:
                {
                    var text = token.Value<string>();
                    if (string.IsNullOrWhiteSpace(text))
                        return;

                    var split = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var raw in split)
                    {
                        var line = raw.Trim();
                        if (line.StartsWith("+DataTable=", StringComparison.OrdinalIgnoreCase))
                            lines.Add(line);
                    }

                    return;
                }
                case JTokenType.Array:
                    foreach (var child in token.Children())
                        CollectHotfixLines(child, lines);
                    return;
                case JTokenType.Object:
                    foreach (var child in token.Children())
                        CollectHotfixLines(child, lines);
                    return;
            }
        }

        private static bool IsSameDataTable(string filePath, string dataTable)
        {
            var left = CanonicalAssetPath(filePath);
            var right = CanonicalAssetPath(dataTable);

            return left.Equals(right, StringComparison.OrdinalIgnoreCase)
                   || left.EndsWith(right, StringComparison.OrdinalIgnoreCase)
                   || right.EndsWith(left, StringComparison.OrdinalIgnoreCase);
        }

        private static string CanonicalAssetPath(string path)
        {
            var normalized = NormalizePath(path).TrimStart('/');
            if (string.IsNullOrWhiteSpace(normalized))
                return string.Empty;

            if (normalized.StartsWith("FortniteGame/Content/", StringComparison.OrdinalIgnoreCase))
                return "/Game/" + normalized.Substring("FortniteGame/Content/".Length);

            if (normalized.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
                return "/" + normalized;

            return "/" + normalized;
        }

        private static JToken ApplyEntriesToOpenedDocument(JToken currentToken, List<HotfixEntry> entries, string filePath)
        {
            var selectedPath = NormalizePath(filePath);
            var result = currentToken.DeepClone();

            foreach (var entry in entries)
            {
                if (!IsSameDataTable(selectedPath, entry.DataTable))
                    continue;

                if (entry.Type == HotfixType.TableUpdate)
                {
                    try
                    {
                        result = JToken.Parse(entry.JsonData);
                    }
                    catch
                    {
                        result = JValue.CreateString(entry.JsonData);
                    }

                    continue;
                }

                if (result is not JObject rootObj)
                    continue;

                if (string.IsNullOrWhiteSpace(entry.RowName) || string.IsNullOrWhiteSpace(entry.PropertyName))
                    continue;

                if (rootObj[entry.RowName] is not JObject rowObj)
                {
                    rowObj = new JObject();
                    rootObj[entry.RowName] = rowObj;
                }

                rowObj[entry.PropertyName] = ParseValueToken(entry.PropertyValue);
            }

            return result;
        }

        private static JToken ParseValueToken(string value)
        {
            var trimmed = value?.Trim() ?? string.Empty;

            if (bool.TryParse(trimmed, out var boolValue))
                return new JValue(boolValue);

            // 数値リテラルは JRaw を使って桁(例: 27500.000000)をそのまま保持する
            if (IsJsonNumberLiteral(trimmed))
                return new JRaw(trimmed);

            return new JValue(trimmed);
        }

        private static bool IsJsonNumberLiteral(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            // JSON 数値に千区切りや先頭 + は使えない
            if (value.Contains(',') || value.StartsWith('+'))
                return false;

            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        }

        private static JToken FindHotfixForFile(JObject root, string filePath)
        {
            var normalized = NormalizePath(filePath);

            var exact = root.Properties().FirstOrDefault(p => NormalizePath(p.Name).Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact.Value.DeepClone();

            var fuzzy = root.Properties().FirstOrDefault(p =>
            {
                var key = NormalizePath(p.Name);
                return key.EndsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
                       normalized.EndsWith(key, StringComparison.OrdinalIgnoreCase);
            });

            return fuzzy?.Value.DeepClone();
        }

        private static string NormalizePath(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : path.Replace('\\', '/').Trim();
        }

        private static JToken MergeWithHotfix(JToken current, JToken hotfix)
        {
            if (current is JObject currentObj && hotfix is JObject hotfixObj)
            {
                var result = (JObject) currentObj.DeepClone();
                foreach (var property in hotfixObj.Properties())
                {
                    if (result.TryGetValue(property.Name, out var existing))
                    {
                        result[property.Name] = MergeWithHotfix(existing, property.Value);
                    }
                    else
                    {
                        result[property.Name] = property.Value.DeepClone();
                    }
                }

                return result;
            }

            return hotfix.DeepClone();
        }

        /// <summary>
        /// ホットフィックステキストをパースします
        /// 対応フォーマット:
        /// +DataTable=/Game/Athena/Items/Weapons/AthenaRangedWeapons;RowUpdate;SMG_GoliathBurst_Scoped_Athena_UR_Ore_T03;DmgPB;18.000000
        /// +DataTable=/DragonCartLoot/DataTables/NoBuildComp/DragonCartLootPackages_Client_Comp_Backup;TableUpdate;"[JSON_DATA]"
        /// </summary>
        public static List<HotfixEntry> ParseHotfixText(string text)
        {
            var entries = new List<HotfixEntry>();
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (!trimmedLine.StartsWith("+DataTable="))
                    continue;

                try
                {
                    // +DataTable= を削除
                    var content = trimmedLine.Substring("+DataTable=".Length);
                    
                    // セミコロンで分割
                    var parts = content.Split(';');
                    if (parts.Length < 3)
                        continue;

                    var entry = new HotfixEntry
                    {
                        DataTable = parts[0],
                        Type = parts[1].Equals("TableUpdate", StringComparison.OrdinalIgnoreCase) 
                            ? HotfixType.TableUpdate 
                            : HotfixType.RowUpdate
                    };

                    if (entry.Type == HotfixType.RowUpdate)
                    {
                        // RowUpdate: DataTable;RowUpdate;RowName;PropertyName;PropertyValue
                        if (parts.Length >= 5)
                        {
                            entry.RowName = parts[2];
                            entry.PropertyName = parts[3];
                            entry.PropertyValue = parts[4];
                        }
                    }
                    else
                    {
                        // TableUpdate: DataTable;TableUpdate;JSON_DATA
                        // JSONデータは複数のセミコロンで構成されている可能性があるため、最初と2番目以降の部分を結合
                        if (parts.Length >= 3)
                        {
                            // 最初の2つの parts (DataTable と TableUpdate) を除外して結合
                            entry.JsonData = string.Join(";", parts.Skip(2));
                            
                            // JSONデータの前後にあるダブルクオートを削除
                            // ただし、JSONデータ内部のダブルクォートは保持
                            entry.JsonData = entry.JsonData.Trim('"');
                            
                            // JSONエスケープシーケンスを元に戻す
                            // \\" を " に、\\\\ を \\ に変換
                            entry.JsonData = entry.JsonData.Replace("\\\"", "\"").Replace("\\\\", "\\");
                        }
                    }

                    entries.Add(entry);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to parse hotfix line: {Line}", trimmedLine);
                }
            }

            return entries;
        }

        /// <summary>
        /// クラウドストレージから現在のデータを取得します
        /// </summary>
        private static async Task<string> FetchCurrentDataAsync(string apiEndpoint)
        {
            try
            {
                var response = await _httpClient.GetAsync(apiEndpoint);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to fetch data from cloud storage");
                FLogger.Append(ELog.Error, () => FLogger.Text($"クラウドストレージからの取得に失敗しました: {ex.Message}", Constants.RED));
                return "{}";
            }
        }

        /// <summary>
        /// ホットフィックスをデータに適用します
        /// </summary>
        private static string ApplyHotfixes(string currentData, List<HotfixEntry> entries)
        {
            try
            {
                // 現在のデータをパース
                var dataDict = JsonConvert.DeserializeObject<Dictionary<string, object>>(currentData) 
                    ?? new Dictionary<string, object>();

                foreach (var entry in entries)
                {
                    if (entry.Type == HotfixType.RowUpdate)
                    {
                        ApplyRowUpdate(dataDict, entry);
                    }
                    else if (entry.Type == HotfixType.TableUpdate)
                    {
                        ApplyTableUpdate(dataDict, entry);
                    }
                }

                return JsonConvert.SerializeObject(dataDict, Formatting.Indented);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to apply hotfixes");
                throw;
            }
        }

        /// <summary>
        /// RowUpdateホットフィックスを適用します
        /// </summary>
        private static void ApplyRowUpdate(Dictionary<string, object> dataDict, HotfixEntry entry)
        {
            var key = entry.DataTable;
            if (!dataDict.ContainsKey(key))
            {
                dataDict[key] = new Dictionary<string, Dictionary<string, object>>();
            }

            var dataTable = dataDict[key] as Dictionary<string, object>;
            if (dataTable == null)
            {
                // 異なる形式の場合は変換
                var existing = dataDict[key];
                if (existing is Dictionary<string, Dictionary<string, object>> typedDict)
                {
                    dataTable = typedDict.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                }
                else
                {
                    dataTable = new Dictionary<string, object>();
                    dataDict[key] = dataTable;
                }
            }

            if (!dataTable.ContainsKey(entry.RowName))
            {
                dataTable[entry.RowName] = new Dictionary<string, object>();
            }

            var row = dataTable[entry.RowName] as Dictionary<string, object>;
            if (row == null)
            {
                row = new Dictionary<string, object>();
                dataTable[entry.RowName] = row;
            }

            // プロパティの値をパース
            row[entry.PropertyName] = ParseValue(entry.PropertyValue);
        }

        /// <summary>
        /// TableUpdateホットフィックスを適用します
        /// </summary>
        private static void ApplyTableUpdate(Dictionary<string, object> dataDict, HotfixEntry entry)
        {
            var key = entry.DataTable;
            
            try
            {
                var jsonData = JsonConvert.DeserializeObject<List<object>>(entry.JsonData);
                dataDict[key] = jsonData;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to parse JSON data for TableUpdate: {Key}", key);
                // JSONとしてパースできない場合は、文字列として保存
                dataDict[key] = entry.JsonData;
            }
        }

        /// <summary>
        /// 値を適切な型にパースします
        /// </summary>
        private static object ParseValue(string value)
        {
            // 数値のパース
            if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleValue))
            {
                // 整数の場合は整数として返す
                if (doubleValue == Math.Floor(doubleValue))
                {
                    return (long)doubleValue;
                }
                return doubleValue;
            }

            // ブール値
            if (bool.TryParse(value, out var boolValue))
            {
                return boolValue;
            }

            // 文字列
            return value;
        }

        /// <summary>
        /// データをクラウドストレージにアップロードします
        /// </summary>
        private static async Task<bool> UploadDataAsync(string apiEndpoint, string data)
        {
            try
            {
                var content = new StringContent(data, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(apiEndpoint, content);
                
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Log.Error("Failed to upload data. Status: {Status}, Content: {Content}", response.StatusCode, errorContent);
                    FLogger.Append(ELog.Error, () => FLogger.Text($"アップロードに失敗しました。ステータス: {response.StatusCode}", Constants.RED));
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to upload data to cloud storage");
                FLogger.Append(ELog.Error, () => FLogger.Text($"アップロードに失敗しました: {ex.Message}", Constants.RED));
                return false;
            }
        }

        /// <summary>
        /// クラウドストレージからデータをダウンロードして適用します（読み取り専用）
        /// </summary>
        public static async Task DownloadAndApplyAsync(string apiEndpoint = DefaultApiEndpoint)
        {
            try
            {
                var data = await FetchCurrentDataAsync(apiEndpoint);
                if (data == "{}")
                {
                    FLogger.Append(ELog.Warning, () => FLogger.Text("クラウドストレージにデータがありません。", Constants.YELLOW));
                    return;
                }

                var dataDict = JsonConvert.DeserializeObject<Dictionary<string, object>>(data);
                if (dataDict == null || dataDict.Count == 0)
                {
                    FLogger.Append(ELog.Warning, () => FLogger.Text("解析できるデータがありません。", Constants.YELLOW));
                    return;
                }

                // データをログに出力
                FLogger.Append(ELog.Information, () => FLogger.Text($"クラウドストレージから {dataDict.Count} 件のデータをダウンロードしました。", Constants.WHITE));
                
                // データの概要を表示
                foreach (var kvp in dataDict)
                {
                    FLogger.Append(ELog.Information, () => FLogger.Text($"  - {kvp.Key}: {kvp.Value?.GetType().Name ?? "null"}", Constants.WHITE));
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to download and apply data");
                FLogger.Append(ELog.Error, () => FLogger.Text($"ダウンロードに失敗しました: {ex.Message}", Constants.RED));
            }
        }
    }
}
