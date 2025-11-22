using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using CUE4Parse.UE4.Assets.Exports;
using FModel.Views.Resources.Controls;
using Newtonsoft.Json.Linq;

namespace FModel.AthenaProfile
{
    public class AthenaGenerator
    {
        protected JObject ATHENA_PROFILE;

        public AthenaGenerator(bool addDefaultPickaxe = true, bool addDefaultGlider = true)
        {
            ATHENA_PROFILE = Init(addDefaultPickaxe, addDefaultGlider);
        }

        public static JObject Init(bool addDefaultPickaxe, bool addDefaultGlider)
        {
            JObject result = new JObject();

            result["created"] = DateTime.Now.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss.fff'Z'");
            result["updated"] = DateTime.Now.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss.fff'Z'");
            result["rvn"] = 1;
            result["wipeNumber"] = 5;
            result["accountId"] = "birufn";
            result["profileId"] = "athena";
            result["version"] = "";
            result["stats"] = new JObject();
            result["stats"]["attributes"] = new JObject();
            result["stats"]["attributes"]["habanero_unlocked"] = false;
            result["stats"]["attributes"]["locker_loadout_migration"] = "FortniteReadFortniteWrite";
            result["stats"]["attributes"]["locker_two_phase_commit"] = "COMMITTED";
            result["stats"]["attributes"]["loadouts"] = new JArray();
            result["stats"]["attributes"]["level"] = 1;
            result["stats"]["attributes"]["pinned_quest"] = null;
            result["stats"]["attributes"]["last_applied_loadout"] = null;
            result["stats"]["attributes"]["book_level"] = 1;
            result["stats"]["attributes"]["season_num"] = 38;
            result["stats"]["attributes"]["accountLevel"] = 1;
            result["commandRevision"] = 0;
            result["_id"] = "3e60bab6176044f0a4c3fb2346d4486d";
            result["items"] = new JObject();

            {
                string itemGuid = "45f24600-ce74-458c-95c9-7214ce8e58df";

                result["items"][itemGuid] = new JObject();
                result["items"][itemGuid]["templateId"] = "CosmeticLocker:cosmeticlocker_athena";
                result["items"][itemGuid]["attributes"] = new JObject();
                result["items"][itemGuid]["attributes"]["locker_slots_data"] = new JObject();
                result["items"][itemGuid]["attributes"]["locker_slots_data"]["slots"] = new JObject();
                result["items"][itemGuid]["attributes"]["locker_slots_data"]["slots"]["Dance"] = new JObject();
                result["items"][itemGuid]["attributes"]["locker_slots_data"]["slots"]["Dance"]["items"] = new JArray("", "", "", "", "", "");
                result["items"][itemGuid]["attributes"]["locker_slots_data"]["slots"]["Dance"]["activeVariants"] = new JArray("", "", "", "", "", "");
                result["items"][itemGuid]["attributes"]["locker_slots_data"]["slots"]["Pickaxe"] = new JObject();
                result["items"][itemGuid]["attributes"]["locker_slots_data"]["slots"]["Pickaxe"]["items"] = new JArray(addDefaultPickaxe ? "AthenaPickaxe:defaultpickaxe" : "");
                result["items"][itemGuid]["attributes"]["locker_slots_data"]["slots"]["Pickaxe"]["activeVariants"] = new JArray("");
                result["items"][itemGuid]["attributes"]["locker_slots_data"]["slots"]["Glider"] = new JObject();
                result["items"][itemGuid]["attributes"]["locker_slots_data"]["slots"]["Glider"]["items"] = new JArray(addDefaultGlider ? "AthenaGlider:defaultglider" : "");
                result["items"][itemGuid]["attributes"]["locker_slots_data"]["slots"]["Glider"]["activeVariants"] = new JArray("");
                result["items"][itemGuid]["quantity"] = 1;

                result["stats"]["attributes"]["loadouts"] = new JArray(itemGuid);
            }

            if (addDefaultPickaxe)
            {
                string itemGuid = "a797af08-0aa3-446f-8c63-ad0edb4a5f41";

                result["items"][itemGuid] = new JObject();
                result["items"][itemGuid]["templateId"] = "AthenaPickaxe:defaultpickaxe";
                result["items"][itemGuid]["attributes"] = new JObject();
                result["items"][itemGuid]["attributes"]["creation_time"] = DateTime.Now.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss.fff'Z'");
                result["items"][itemGuid]["attributes"]["level"] = 1;
                result["items"][itemGuid]["attributes"]["item_seen"] = true;
                result["items"][itemGuid]["quantity"] = 1;
            }

            if (addDefaultGlider)
            {
                string itemGuid = "ad144593-4712-465d-ba1e-9bb0d78fb952";

                result["items"][itemGuid] = new JObject();
                result["items"][itemGuid]["templateId"] = "AthenaGlider:defaultglider";
                result["items"][itemGuid]["attributes"] = new JObject();
                result["items"][itemGuid]["attributes"]["creation_time"] = DateTime.Now.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss.fff'Z'");
                result["items"][itemGuid]["attributes"]["level"] = 1;
                result["items"][itemGuid]["attributes"]["item_seen"] = true;
                result["items"][itemGuid]["quantity"] = 1;
            }

            return result;
        }

        public void AddItem(UObject itemDefinition, List<KeyValuePair<string, List<string>>> itemVariants = null)
        {
            if (itemDefinition == null || itemDefinition.Class == null)
                return;

            JArray variants = null;

            if (itemVariants != null && itemVariants.Count != 0)
            {
                variants = new JArray();

                foreach (KeyValuePair<string, List<string>> itemVariant in itemVariants)
                {
                    string channel = itemVariant.Key;
                    string active = itemVariant.Value[0];
                    JArray owned = new JArray();

                    foreach (string value in itemVariant.Value)
                    {
                        owned.Add(value);
                    }

                    JObject variant = new JObject();

                    variant["channel"] = channel;
                    variant["active"] = active;
                    variant["owned"] = owned;

                    variants.Add(variant);
                }
            }

            string templateId = $"{GetItemType(itemDefinition.Class.Name)}:{itemDefinition.Name.ToLower()}";

            ATHENA_PROFILE["items"][templateId] = new JObject();
            ATHENA_PROFILE["items"][templateId]["templateId"] = templateId;
            ATHENA_PROFILE["items"][templateId]["attributes"] = new JObject();
            ATHENA_PROFILE["items"][templateId]["attributes"]["creation_time"] = DateTime.Now.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss.fff'Z'");
            ATHENA_PROFILE["items"][templateId]["attributes"]["level"] = 1;
            ATHENA_PROFILE["items"][templateId]["attributes"]["item_seen"] = true;

            if (variants != null)
            {
                ATHENA_PROFILE["items"][templateId]["attributes"]["variants"] = variants;
            }

            ATHENA_PROFILE["items"][templateId]["quantity"] = 1;
        }

        private string GetItemType(string type)
        {
            // バトルロイヤル
            if (type == "AthenaCharacterItemDefinition")
                return "AthenaCharacter";

            if (type == "AthenaBackpackItemDefinition" || type == "AthenaPetItemDefinition" || type == "AthenaPetCarrierItemDefinition")
                return "AthenaBackpack";

            if (type == "AthenaPickaxeItemDefinition")
                return "AthenaPickaxe";

            if (type == "AthenaGliderItemDefinition")
                return "AthenaGlider";

            if (type == "AthenaSkyDiveContrailItemDefinition")
                return "AthenaSkyDiveContrail";

            if (type == "AthenaDanceItemDefinition" || type == "AthenaEmojiItemDefinition" || type == "AthenaSprayItemDefinition" || type == "AthenaToyItemDefinition")
                return "AthenaDance";

            if (type == "AthenaItemWrapDefinition")
                return "AthenaItemWrap";

            if (type == "AthenaMusicPackItemDefinition")
                return "AthenaMusicPack";

            if (type == "AthenaLoadingScreenItemDefinition")
                return "AthenaLoadingScreen";

            //フェスティバル
            if (type == "SparksGuitarItemDefinition")
                return "SparksGuitar";

            if (type == "SparksBassItemDefinition")
                return "SparksBass";

            if (type == "SparksDrumItemDefinition")
                return "SparksDrums";

            if (type == "SparksMicItemDefinition")
                return "SparksMicrophone";

            return "None";
        }

        public void SaveFile()
        {
            SaveFileDialog saveFileDialog = new SaveFileDialog();
            saveFileDialog.Title = "保存先を選択してください";
            saveFileDialog.Filter = "JSON Files|*.json";
            saveFileDialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            saveFileDialog.FileName = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "\\athena.json";
            saveFileDialog.OverwritePrompt = true;

            if (saveFileDialog.ShowDialog() == DialogResult.OK)
            {
                File.WriteAllText(saveFileDialog.FileName, ATHENA_PROFILE.ToString());

                Process process = new Process();
                process.StartInfo.FileName = "explorer.exe";
                process.StartInfo.Arguments = $"/select,\"{saveFileDialog.FileName}\"";
                process.StartInfo.UseShellExecute = true;
                process.Start();
            }

            FLogger.Append(ELog.Information, () => FLogger.Text($"プロファイルの生成が完了しました。{Constants.APP_VERSION}", Constants.WHITE, true));
        }
    }
}
