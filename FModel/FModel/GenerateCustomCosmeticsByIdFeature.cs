﻿using FModel.Views.Resources.Controls;
using FModel.Settings;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FModel.Features.Athena
{
    public class GenerateCustomCosmeticsByIdFeature : AthenaFeatureBase
    {
        public static async Task ExecuteAsync()
        {
            var inputDialog = new InputDialog("IDでカスタムコスメティックを生成", "コスメティックIDをカンマ区切りで入力してください:", "");
            if (inputDialog.ShowDialog() != true) return;

            var ids = inputDialog.InputText;
            if (string.IsNullOrWhiteSpace(ids))
            {
                FLogger.Append(ELog.Warning, () => FLogger.Text("IDが入力されていません。", Constants.YELLOW));
                return;
            }

            try
            {
                FLogger.Append(ELog.Information, () => FLogger.Text("カスタムコスメティックのプロファイルを生成中...", Constants.WHITE));
                string json = await Task.Run(() => AthenaGenerator.BuildProfileJsonByIds(ids.Split(',').Select(id => id.Trim()).ToArray()));

                var idsList = ids.Split(',').Select(id => id.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                var baseFileName = idsList.Count == 1 ? idsList[0] : "athena_custom";

                var format = UserSettings.Default.PropertiesFilenameFormat;
                var fileName = $"{Helper.GenerateFormattedFileName(format, baseFileName)}.json";

                var dlg = new SaveFileDialog
                {
                    Title = "保存先を選択してください",
                    Filter = "JSON Files|*.json",
                    FileName = fileName,
                    OverwritePrompt = true
                };

                if (dlg.ShowDialog() == true)
                {
                    await File.WriteAllTextAsync(dlg.FileName, json);
                    Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{dlg.FileName}\"", UseShellExecute = true });
                }
                FLogger.Append(ELog.Information, () => FLogger.Text("カスタムコスメティックのプロファイル生成が完了しました。", Constants.WHITE));
            }
            catch (Exception ex)
            {
                FLogger.Append(ELog.Error, () => FLogger.Text($"プロファイルの生成に失敗しました。: {ex.Message}", Constants.RED));
            }
        }
    }
}