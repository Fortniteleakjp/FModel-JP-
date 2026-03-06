using FModel.Services;
using AdonisUI.Controls;
using System.Collections;
using FModel.Views;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.UE4.Objects.Core.Misc;
using Newtonsoft.Json;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.VirtualFileSystem;
using System.Windows;
using FModel;
using FModel.Views.Resources.Controls;
using Serilog;

namespace FModel.Features.Athena
{
    public class BruteForceAesFeature : AthenaFeatureBase
    {
        /// <summary>
        /// ランダムに AES-256 キーを生成します (0x で始まる 16 進数形式、256 ビット = 64 文字)
        /// 無限シーケンスを返します。キャンセルされるまで続きます。
        /// </summary>
        private static IEnumerable<string> GenerateRandomAesKeys(HashSet<string> excludedKeys = null)
        {
            var keyBytes = new byte[32]; // 256 bits = 32 bytes
            
            while (true)
            {
                Random.Shared.NextBytes(keyBytes);
                var key = "0x" + Convert.ToHexString(keyBytes);
                if (excludedKeys == null || !excludedKeys.Contains(key))
                {
                    yield return key;
                }
            }
        }

        private static IEnumerable<string> GenerateRandomAesKeysGpu(HashSet<string> excludedKeys = null)
        {
            using var generator = new GpuRandomAesKeyGenerator();
            if (!generator.IsHardwareAccelerated)
            {
                FLogger.Append(ELog.Warning, () => FLogger.Text("GPUアクセラレータが見つからないため、ILGPUのCPUアクセラレータで処理します。", Constants.YELLOW));
            }
            else
            {
                FLogger.Append(ELog.Information, () => FLogger.Text($"GPU鍵生成バッチサイズ: {GpuRandomAesKeyGenerator.DefaultBatchSize:N0} keys/dispatch", Constants.WHITE));
            }

            foreach (var key in generator.GenerateKeys(excludedKeys, GpuRandomAesKeyGenerator.DefaultBatchSize))
                yield return key;
        }

        private static IEnumerable<string> ReadAndFilterKeysFromFile(string filePath, FGuid targetGuid)
        {
            foreach (var line in File.ReadLines(filePath))
            {
                var key = line.Trim();
                if (string.IsNullOrWhiteSpace(key)) continue;

                if (key.Contains(':')) // Keychain format: GUID:Base64Key
                {
                    var parts = key.Split(':', 2);
                    if (parts.Length == 2)
                    {
                        var guidString = parts[0].Trim();
                        var base64Key = parts[1].Trim();

                        if (targetGuid.ToString(EGuidFormats.Digits).Equals(guidString, StringComparison.OrdinalIgnoreCase))
                        {
                            byte[] keyBytes = null;
                            try
                            {
                                keyBytes = Convert.FromBase64String(base64Key);
                            }
                            catch { /* Invalid Base64, ignore */ }

                            if (keyBytes != null)
                                yield return "0x" + Convert.ToHexString(keyBytes).ToUpperInvariant();
                        }
                    }
                }
                else if (key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) // Hex format
                {
                    yield return key;
                }
            }
        }

        public static Task ExecuteAsync() => ExecuteAsync(false);

        public static async Task ExecuteAsync(bool useGpuRandomBackend)
        {
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

            // if (targetPak.Name.Contains("1070"))
            // {
            //     var debugKeys = GenerateRandomAesKeys().Take(199999).ToList();
            //     debugKeys.Add("0x130DE6365CD2AE7C87F4EC5F129983A8F8EBD3C4D9DA3E3F1B34381E506BD1DB");
            //     keys = debugKeys;
            // }

            var foundKeys = new Dictionary<FGuid, string>();
            var triedKeysFilePath = "TriedAesKeys.txt";
            var excludedKeys = new HashSet<string>();

            var box = new MessageBoxModel
            {
                Text = "総当たりの方法を選択してください:",
                Caption = "総当たりモード",
                Icon = AdonisUI.Controls.MessageBoxImage.Question,
                Buttons = new[]
                {
                    MessageBoxButtons.Custom("ランダムなキーを生成", 1),
                    MessageBoxButtons.Custom("キーファイルを使用", 2),
                    MessageBoxButtons.Cancel("キャンセル")
                }
            };
            AdonisUI.Controls.MessageBox.Show(box);

            IEnumerable<string> keys;
            long totalKeys = -1;

            switch (box.Result)
            {
                case AdonisUI.Controls.MessageBoxResult.Custom when (int)box.ButtonPressed.Id == 1: // Random
                    if (useGpuRandomBackend)
                    {
                        FLogger.Append(ELog.Information, () => FLogger.Text($"対象ファイル: {targetPak.Name}. GPUバックエンドでランダムAESキー総当たりを開始します...", Constants.WHITE));
                        keys = GenerateRandomAesKeysGpu(excludedKeys);
                    }
                    else
                    {
                        FLogger.Append(ELog.Information, () => FLogger.Text($"対象ファイル: {targetPak.Name}. ランダムに生成したAESキーの総当たりを開始します...", Constants.WHITE));
                        keys = GenerateRandomAesKeys(excludedKeys);
                    }
                    break;
                case AdonisUI.Controls.MessageBoxResult.Custom when (int)box.ButtonPressed.Id == 2: // File
                    var keyFileDialog = new OpenFileDialog
                    {
                        Title = "キーファイルを選択",
                        Filter = "Text Files|*.txt|All Files|*.*"
                    };
                    if (keyFileDialog.ShowDialog() != true) return;

                    FLogger.Append(ELog.Information, () => FLogger.Text($"対象ファイル: {targetPak.Name}. ファイル '{Path.GetFileName(keyFileDialog.FileName)}' のキーを使用して総当たりを開始します...", Constants.WHITE));
                    var keyList = ReadAndFilterKeysFromFile(keyFileDialog.FileName, targetPak.EncryptionKeyGuid).ToList();
                    keys = keyList;
                    totalKeys = keyList.Count;
                    break;
                default: // Cancel or closed
                    return;
            }

            if (File.Exists(triedKeysFilePath) && totalKeys == -1) // Only use excluded keys for random generation
            {
                try
                {
                    foreach (var line in File.ReadLines(triedKeysFilePath))
                    {
                        excludedKeys.Add(line);
                    }
                }
                catch { /* ignore */ }
            }

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
                var stopwatch = Stopwatch.StartNew();
                var parallelOptions = new ParallelOptions
                {
                    CancellationToken = combinedToken,
                    MaxDegreeOfParallelism = Environment.ProcessorCount
                };

                Log.Information("Starting brute-force for {PakName}...", targetPak.Name);

                foreach (var vfs in paksToScan)
                {
                    if (combinedToken.IsCancellationRequested) break;

                    FLogger.Append(ELog.Information, () => FLogger.Text($"{vfs.Name} のキーを検索中...", Constants.WHITE));

                    try
                    {
                        Parallel.ForEach(keys, parallelOptions, (key, state) =>
                        {
                            var count = Interlocked.Increment(ref currentOp);

                            if (count < 0)
                            {
                                state.Stop();
                                return;
                            }

                            // UI更新頻度を調整 (例: 100回に1回)
                            if (count % 100 == 0)
                            {
                                var elapsed = stopwatch.Elapsed;
                                var rate = count / elapsed.TotalSeconds;

                                progressWindow.Dispatcher.InvokeAsync(() =>
                                {
                                    progressWindow.UpdateAttemptCount(count, totalKeys);
                                    progressWindow.UpdateCurrentKey(key);
                                    progressWindow.UpdateElapsedTime(elapsed);
                                    progressWindow.UpdateRate(rate);
                                });
                            }

                            if (count % 100000 == 0)
                            {
                                Log.Information("Brute-force progress: {Count:N0} keys tested. Elapsed: {Elapsed}", count, stopwatch.Elapsed);
                            }

                            if (string.IsNullOrWhiteSpace(key)) return;

                            try
                            {
                                var aesKey = new FAesKey(key);
                                // キーが正しいかどうかを検証
                                if (vfs.TestAesKey(aesKey))
                                {
                                    Log.Information("Potential key found for {VfsName}: {Key}. Verifying...", vfs.Name, key);
                                    var isVerified = false;
                                    try
                                    {
                                        // AesManager と同じ Provider 経由のマウント処理で、実際に復号可能かを検証する
                                        var mountedCount = provider.SubmitKey(vfs.EncryptionKeyGuid, aesKey);
                                        isVerified = mountedCount > 0 || provider.Keys.ContainsKey(vfs.EncryptionKeyGuid);
                                        if (!isVerified)
                                            Log.Warning("Key {Key} passed TestAesKey but failed actual provider decryption/mount.", key);
                                    }
                                    catch (Exception ex)
                                    {
                                        Log.Warning(ex, "Key verification failed for {Key}", key);
                                    }

                                    if (isVerified)
                                    {
                                        // 見つかった場合、ロックして登録し、ループを停止
                                        lock (foundKeys)
                                        {
                                            if (!foundKeys.ContainsKey(vfs.EncryptionKeyGuid))
                                            {
                                                FLogger.Append(ELog.Information, () => FLogger.Text($"キーが見つかりました！ Pak: {vfs.Name}, Key: {key}", Constants.GREEN));
                                                foundKeys[vfs.EncryptionKeyGuid] = key;

                                            // UIスレッドで、同じGUIDを持つすべてのPakにキーを適用し、UIを更新する
                                            Application.Current.Dispatcher.Invoke(() =>
                                            {
                                                foreach (var pak in encryptedPaks.Where(p => p.EncryptionKeyGuid == vfs.EncryptionKeyGuid))
                                                {
                                                    pak.AesKey = aesKey;
                                                }

                                                // GameDirectory.DirectoryFiles内のPakにもキーを適用 (Archives Info用)
                                                var cue4Parse = ApplicationService.ApplicationView.CUE4Parse;
                                                if (cue4Parse?.GameDirectory?.DirectoryFiles is IEnumerable directoryFiles)
                                                {
                                                    foreach (var item in directoryFiles)
                                                    {
                                                        if (item is IAesVfsReader pak && pak.EncryptionKeyGuid == vfs.EncryptionKeyGuid)
                                                        {
                                                            pak.AesKey = aesKey;
                                                        }
                                                    }
                                                }

                                                // MainWindowのDirectoryFilesListBoxを強制的に更新します
                                                if (Application.Current.MainWindow is MainWindow mainWindow)
                                                {
                                                    mainWindow.DirectoryFilesListBox.Items.Refresh();
                                                }
                                            });
                                            }
                                        }
                                        state.Stop();
                                    }
                                }
                            }
                            catch (Exception) { /* Invalid key format, ignore. */ }
                        });
                    }
                    catch (OperationCanceledException) { /* キャンセルされた場合 */ }
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
