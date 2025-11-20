using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using FModel.Services;
using FModel.Views.Resources.Controls;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FModel
{
    public static class AthenaGenerator
    {
        private static readonly HttpClient HttpClient = new HttpClient();

        public static string BuildProfileJson(string url, bool withPaks = false)
        {
            var response = HttpClient.GetStringAsync(url).GetAwaiter().GetResult();
            var json = JObject.Parse(response);

            if (json["status"]?.Value<int>() != 200)
            {
                throw new Exception($"APIから不正なステータスコードが返されました: {json["status"]?.Value<int>()}");
            }

            var cosmetics = json["data"] as JArray;
            if (cosmetics == null)
            {
                throw new Exception("APIレスポンスにコスメティックデータが含まれていません。");
            }

            return ProcessCosmetics(cosmetics, withPaks);
        }

        public static string BuildProfileJsonByIds(string[] ids)
        {
            var cosmetics = new JArray();
            foreach (var id in ids)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;

                var url = $"https://fortnite-api.com/v2/cosmetics/br/{id}";
                var response = HttpClient.GetStringAsync(url).GetAwaiter().GetResult();
                var json = JObject.Parse(response);

                if (json["status"]?.Value<int>() == 200 && json["data"] != null)
                {
                    cosmetics.Add(json["data"]);
                }
                else
                {
                    FLogger.Append(ELog.Warning, () => FLogger.Text($"ID '{id}' のコスメティックが見つかりませんでした。", Constants.YELLOW));
                }
            }

            if (cosmetics.Count == 0)
            {
                throw new Exception("有効なコスメティックIDが見つかりませんでした。");
            }

            return ProcessCosmetics(cosmetics, false);
        }

        private static string ProcessCosmetics(JArray cosmetics, bool withPaks)
        {
            var profile = new JObject
            {
                ["created"] = DateTime.UtcNow,
                ["profileId"] = "athena",
                ["stats"] = new JObject
                {
                    ["attributes"] = new JObject
                    {
                        ["past_seasons"] = new JArray(),
                        ["season_num"] = 99,
                        ["level"] = 100,
                        ["book_level"] = 100,
                        ["book_purchased"] = true
                    }
                },
                ["items"] = new JObject(),
                ["quests"] = new JArray()
            };

            var items = profile["items"] as JObject;

            foreach (var cosmetic in cosmetics)
            {
                var cosmeticId = cosmetic["id"]?.ToString();
                if (string.IsNullOrEmpty(cosmeticId)) continue;

                var item = new JObject
                {
                    ["templateId"] = cosmetic["type"]?["backendValue"] + ":" + cosmeticId,
                    ["attributes"] = new JObject
                    {
                        ["max_level_bonus"] = 0,
                        ["level"] = 1,
                        ["item_seen"] = true,
                        ["xp"] = 0,
                        ["variants"] = new JArray(),
                        ["favorite"] = false
                    },
                    ["quantity"] = 1
                };

                if (withPaks && cosmetic["path"] != null)
                {
                    item["attributes"]["pak_path"] = cosmetic["path"];
                }

                items.Add(cosmeticId, item);
            }

            return profile.ToString(Formatting.Indented);
        }
    }
}
