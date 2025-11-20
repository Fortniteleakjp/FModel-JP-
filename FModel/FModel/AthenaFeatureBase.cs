using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FModel.Views.Resources.Controls;

namespace FModel.Features.Athena

{
    public abstract class AthenaFeatureBase
    {
        protected static async Task GenerateProfile(string url, string fileName, string logMessage, bool withPaks = false)
        {
            try
            {
                FLogger.Append(ELog.Information, () => FLogger.Text(logMessage, Constants.WHITE));

                string json = await Task.Run(() => AthenaGenerator.BuildProfileJson(url, withPaks));

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

                FLogger.Append(ELog.Information, () => FLogger.Text("プロファイルの生成が完了しました。", Constants.WHITE));
            }
            catch (Exception ex)
            {
                FLogger.Append(ELog.Error, () => FLogger.Text($"プロファイルの生成に失敗しました。: {ex.Message}", Constants.RED));
            }
        }

        public static void LogNotImplemented(string featureName)
        {
            FLogger.Append(ELog.Warning, () => FLogger.Text($"{featureName}機能は現在実装されていません。", Constants.YELLOW));
        }
    }
}
