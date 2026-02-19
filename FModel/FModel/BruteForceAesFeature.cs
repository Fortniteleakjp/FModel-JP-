using FModel.Services;
using FModel.Views;
using FModel.Views.Resources.Controls;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.UE4.Objects.Core.Misc;
using Newtonsoft.Json;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.VirtualFileSystem;
using System.Windows;

namespace FModel.Features.Athena
{
    public class BruteForceAesFeature : AthenaFeatureBase
    {
        /// <summary>
        /// ランダムに AES-256 キーを生成します (0x で始まる 16 進数形式、256 ビット = 64 文字)
        /// 無限シーケンスを返します。キャンセルされるまで続きます。
        /// </summary>
        private static IEnumerable<string> GenerateRandomAesKeys()
        {
            var random = new Random();
            var keyBytes = new byte[32]; // 256 bits = 32 bytes
            
            while (true)
            {
                random.NextBytes(keyBytes);
                yield return "0x" + string.Concat(keyBytes.Select(b => b.ToString("X2")));
            }
        }

        public static async Task ExecuteAsync()
        {
            var keys = GenerateRandomAesKeys(); // 無限シーケンス

            var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
            if (provider == null)
            {
                FLogger.Append(ELog.Error, () => FLogger.Text("Provider is not initialized.", Constants.RED));
                return;
            }

            var encryptedPaks = provider.MountedVfs
                .Concat(provider.UnloadedVfs)
                .Where(vfs => vfs.IsEncrypted && vfs.EncryptionKeyGuid != Constants.ZERO_GUID)
                .ToList();
            if (encryptedPaks.Count == 0)
            {
                FLogger.Append(ELog.Information, () => FLogger.Text("暗号化されたPakファイルが見つかりませんでした。", Constants.WHITE));
                return;
            }

            // ターゲットファイルを選択
            var openFileDialog = new OpenFileDialog
            {
                Title = "総当たりを試すファイルを選択",
                Filter = "PAK Files|*.pak|All Files|*.*",
                Multiselect = false
            };

            if (openFileDialog.ShowDialog() != true) return;

            var selectedFileName = Path.GetFileName(openFileDialog.FileName);
            var targetPak = encryptedPaks.FirstOrDefault(p => p.Name.EndsWith(selectedFileName, StringComparison.OrdinalIgnoreCase));
            
            if (targetPak == null)
            {
                FLogger.Append(ELog.Error, () => FLogger.Text($"選択されたファイルが暗号化Pakリストに見つかりません: {selectedFileName}", Constants.RED));
                return;
            }

            FLogger.Append(ELog.Information, () => FLogger.Text($"対象ファイル: {targetPak.Name}. ランダムに生成したAESキーの総当たりを開始します...", Constants.WHITE));

            var foundKeys = new Dictionary<FGuid, string>();
            
            // 進捗ウィンドウを作成・表示
            var progressWindow = new BruteForceProgressWindow();
            progressWindow.SetTargetFile(targetPak.Name);
            progressWindow.Owner = Application.Current.MainWindow;
            progressWindow.Show();

            await ApplicationService.ThreadWorkerView.Begin(cancellationToken =>
            {
                var combinedToken = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, 
                    progressWindow.CancellationTokenSource.Token
                ).Token;
                var paksToScan = new List<IAesVfsReader> { targetPak };
                if (provider.Keys.ContainsKey(targetPak.EncryptionKeyGuid))
                {
                    FLogger.Append(ELog.Information, () => FLogger.Text("このファイルは既に解読済みです。", Constants.WHITE));
                    return;
                }

                long currentOp = 0;

                foreach (var vfs in paksToScan)
                {
                    if (combinedToken.IsCancellationRequested) break;

                    FLogger.Append(ELog.Information, () => FLogger.Text($"{vfs.Name} のキーを検索中...", Constants.WHITE));

                    foreach (var key in keys)
                    {
                        if (combinedToken.IsCancellationRequested) break;
                        
                        currentOp++;
                        
                        // 進捗ウィンドウを更新
                        progressWindow.Dispatcher.Invoke(() =>
                        {
                            progressWindow.UpdateAttemptCount(currentOp);
                            progressWindow.UpdateCurrentKey(key);
                        });
                        
                        if (string.IsNullOrWhiteSpace(key)) continue;

                        try
                        {
                            var aesKey = new FAesKey(key);
                            // キーが正しいかどうかを検証
                            if (vfs.TestAesKey(aesKey))
                            {
                                vfs.AesKey = aesKey;

                                FLogger.Append(ELog.Information, () => FLogger.Text($"キーが見つかりました！ Pak: {vfs.Name}, Key: {key}", Constants.GREEN));
                                foundKeys[vfs.EncryptionKeyGuid] = key;
                                provider.SubmitKey(vfs.EncryptionKeyGuid, aesKey);
                                break;
                            }
                        }
                        catch (Exception) { /* Invalid key format, ignore. */ }
                    }
                }

                FLogger.Append(ELog.Information, () => FLogger.Text(foundKeys.Count > 0 ? "総当たりが完了しました。" : "キャンセルされました。ランダムに生成されたこれらのキーでは有効なAESキーが見つかりませんでした。", foundKeys.Count > 0 ? Constants.WHITE : Constants.YELLOW));
                
                // 進捗ウィンドウをクローズ
                progressWindow.Dispatcher.Invoke(() => progressWindow.Close());
            });

            if (foundKeys.Count > 0)
            {
                var saveFileDialog = new SaveFileDialog
                {
                    Title = "見つかったAESキーを保存",
                    Filter = "Text File|*.txt|JSON File|*.json",
                    FileName = "FoundAesKeys.txt"
                };

                if (saveFileDialog.ShowDialog() == true)
                {
                    try
                    {
                        if (saveFileDialog.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        {
                            var json = JsonConvert.SerializeObject(foundKeys, Formatting.Indented);
                            await File.WriteAllTextAsync(saveFileDialog.FileName, json);
                        }
                        else
                        {
                            var lines = foundKeys.Select(kvp => $"{kvp.Key}={kvp.Value}");
                            await File.WriteAllLinesAsync(saveFileDialog.FileName, lines);
                        }
                        FLogger.Append(ELog.Information, () => FLogger.Text($"キーを保存しました: {saveFileDialog.FileName}", Constants.GREEN));
                    }
                    catch (Exception ex)
                    {
                        FLogger.Append(ELog.Error, () => FLogger.Text($"保存中にエラーが発生しました: {ex.Message}", Constants.RED));
                    }
                }
            }
        }
    }
}