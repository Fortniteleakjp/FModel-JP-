using System;
using System.Collections.Generic; // List<> を使用するために追加
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AdonisUI.Controls;
using UAssetAPI;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using CUE4Parse.UE4.Assets;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Exceptions;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels;
using FModel.Views;
using FModel.Views.Resources.Controls;
using ICSharpCode.AvalonEdit.Editing;
using FModel.Framework; // RelayCommand を使用するためのやつ
using System.Collections.Specialized; // NotifyCollectionChangedEventArgs を使用するためのやつ
using Microsoft.Win32;
using FModel.Features.Athena;
using Serilog;
using Ookii.Dialogs.Wpf;
using UAssetAPI.PropertyTypes.Structs;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxButton = AdonisUI.Controls.MessageBoxButton;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;
using Newtonsoft.Json;

namespace FModel;

public partial class MainWindow
{
    public void AddAssetPathToExportCart(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        if (UserSettings.Default.ExportCartPaths.Contains(filePath, StringComparer.OrdinalIgnoreCase))
        {
            FLogger.Append(ELog.Information, () => FLogger.Text($"Already in export cart: {filePath}", Constants.WHITE, true));
            return;
        }

        UserSettings.Default.ExportCartPaths.Add(filePath);
        UserSettings.Save();
        FLogger.Append(ELog.Information, () => FLogger.Text($"Added to export cart: {filePath}", Constants.WHITE, true));
    }

    public void RemoveAssetPathFromExportCart(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        var existingPath = UserSettings.Default.ExportCartPaths
            .FirstOrDefault(path => path.Equals(filePath, StringComparison.OrdinalIgnoreCase));
        if (existingPath == null)
            return;

        UserSettings.Default.ExportCartPaths.Remove(existingPath);
        UserSettings.Save();
        FLogger.Append(ELog.Information, () => FLogger.Text($"Removed from export cart: {filePath}", Constants.WHITE, true));
    }

    public void AddAssetsToExportCart(IEnumerable<GameFile> files)
    {
        var addedCount = 0;
        foreach (var file in files.Where(file => file != null))
        {
            if (string.IsNullOrWhiteSpace(file.Path) || UserSettings.Default.ExportCartPaths.Contains(file.Path, StringComparer.OrdinalIgnoreCase))
                continue;

            UserSettings.Default.ExportCartPaths.Add(file.Path);
            addedCount++;
        }

        if (addedCount <= 0)
        {
            FLogger.Append(ELog.Information, () => FLogger.Text("Export cart was unchanged.", Constants.WHITE, true));
            return;
        }

        UserSettings.Save();
        FLogger.Append(ELog.Information, () => FLogger.Text($"Added {addedCount} asset(s) to export cart.", Constants.WHITE, true));
    }

    public void RemoveAssetsFromExportCart(IEnumerable<GameFile> files)
    {
        var removedCount = 0;
        foreach (var file in files.Where(file => file != null))
        {
            if (string.IsNullOrWhiteSpace(file.Path))
                continue;

            var existingPath = UserSettings.Default.ExportCartPaths
                .FirstOrDefault(path => path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
            if (existingPath == null)
                continue;

            UserSettings.Default.ExportCartPaths.Remove(existingPath);
            removedCount++;
        }

        if (removedCount <= 0)
        {
            FLogger.Append(ELog.Information, () => FLogger.Text("No selected assets were in the export cart.", Constants.WHITE, true));
            return;
        }

        UserSettings.Save();
        FLogger.Append(ELog.Information, () => FLogger.Text($"Removed {removedCount} asset(s) from export cart.", Constants.WHITE, true));
    }

    public async Task ExportAssetCartAsync()
    {
        var cartPaths = UserSettings.Default.ExportCartPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (cartPaths.Length == 0)
        {
            MessageBox.Show(this, "カートは空です。先にアセットを追加してください。", "エクスポートカート", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var exportTargets = new List<GameFile>(cartPaths.Length);
        var missingPaths = new List<string>();
        foreach (var cartPath in cartPaths)
        {
            if (_applicationView.CUE4Parse.Provider.TryGetGameFile(cartPath, out var gameFile))
                exportTargets.Add(gameFile);
            else
                missingPaths.Add(cartPath);
        }

        if (exportTargets.Count == 0)
        {
            MessageBox.Show(this, "カート内のアセットを現在のプロバイダーで解決できませんでした。ゲームディレクトリかカート内容を確認してください。", "エクスポートカート", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var exportedCount = 0;
        var failedCount = 0;
        await _threadWorkerView.Begin(cancellationToken =>
        {
            foreach (var entry in exportTargets)
            {
                Thread.Yield();
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    ExportCartAsset(cancellationToken, entry);
                    exportedCount++;
                }
                catch (Exception ex)
                {
                    failedCount++;
                    Log.Error(ex, "Failed to export cart asset {Path}", entry.Path);
                }
            }
        });

        foreach (var missingPath in missingPaths)
        {
            FLogger.Append(ELog.Warning, () => FLogger.Text($"Cart asset not found in provider: {missingPath}", Constants.YELLOW, true));
        }

        MessageBox.Show(this,
            $"エクスポート完了: {exportedCount} 件\n失敗: {failedCount} 件\n未解決: {missingPaths.Count} 件",
            "エクスポートカート",
            MessageBoxButton.OK,
            failedCount > 0 || missingPaths.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void ExportCartAsset(CancellationToken cancellationToken, GameFile entry)
    {
        if (TryGetEncodedAudioExtension(entry, out var encodedAudioExtension))
        {
            ExportEncodedAudioAsWav(entry, encodedAudioExtension);
            return;
        }

        if (!entry.IsUePackage)
        {
            _applicationView.CUE4Parse.Extract(cancellationToken, entry, false, EBulkType.Properties | EBulkType.Auto);
            return;
        }

        if (TryGetAssetExportType(entry, out var exportType))
        {
            if (IsSoundWaveExportType(exportType))
            {
                _applicationView.CUE4Parse.Extract(cancellationToken, entry, false, EBulkType.Audio | EBulkType.Auto);
                return;
            }

            if (IsTextureExportType(exportType))
            {
                _applicationView.CUE4Parse.Extract(cancellationToken, entry, false, EBulkType.Properties | EBulkType.Auto);
                _applicationView.CUE4Parse.Extract(cancellationToken, entry, false, EBulkType.Textures | EBulkType.Auto);
                return;
            }
        }

        _applicationView.CUE4Parse.Extract(cancellationToken, entry, false, EBulkType.Properties | EBulkType.Auto);
    }

    private static bool TryGetEncodedAudioExtension(GameFile entry, out string extension)
    {
        extension = entry.Extension?.ToLowerInvariant();
        if (extension is "rada" or "binka")
            return true;

        if (entry.Name.EndsWith(".rada", StringComparison.OrdinalIgnoreCase))
        {
            extension = "rada";
            return true;
        }

        if (entry.Name.EndsWith(".binka", StringComparison.OrdinalIgnoreCase))
        {
            extension = "binka";
            return true;
        }

        return false;
    }

    private static void ExportEncodedAudioAsWav(GameFile entry, string extension)
    {
        var data = entry.Read();
        var relativePath = UserSettings.Default.KeepDirectoryStructure
            ? entry.PathWithoutExtension
            : entry.NameWithoutExtension;

        if (relativePath.StartsWith('/'))
            relativePath = relativePath[1..];

        var destinationPath = Path.Combine(UserSettings.Default.AudioDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)) + ".wav";
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        if (AudioPlayerViewModel.TryDecode(extension, tempPath, data, out var wavPath))
        {
            if (File.Exists(destinationPath))
                File.Delete(destinationPath);

            File.Move(wavPath, destinationPath);
            Log.Information("Successfully saved {FilePath}", destinationPath);
            return;
        }

        throw new InvalidOperationException($"Failed to decode encoded audio '{entry.Path}' as {extension}.");
    }

    private static bool IsTextureExportType(string exportType)
        => exportType.Contains("Texture", StringComparison.OrdinalIgnoreCase);

    private static bool IsSoundWaveExportType(string exportType)
        => exportType.Contains("SoundWave", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<GameFile> GetSelectedGameFilesFromContextMenu(object sender)
    {
        if (sender is not FrameworkElement { Parent: ContextMenu { PlacementTarget: ListBox listBox } })
            return Array.Empty<GameFile>();

        return listBox.SelectedItems.Cast<object?>().OfType<GameFile>().ToArray();
    }

    private void OnAddAssetsToCartClick(object sender, RoutedEventArgs e)
    {
        AddAssetsToExportCart(GetSelectedGameFilesFromContextMenu(sender));
    }

    private void OnRemoveAssetsFromCartClick(object sender, RoutedEventArgs e)
    {
        RemoveAssetsFromExportCart(GetSelectedGameFilesFromContextMenu(sender));
    }

    private async void OnExportCartClick(object sender, RoutedEventArgs e)
    {
        await ExportAssetCartAsync();
    }

    private void OnShowExportCartClick(object sender, RoutedEventArgs e)
    {
        var cartPaths = UserSettings.Default.ExportCartPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (cartPaths.Length == 0)
        {
            MessageBox.Show(this, "カートは空です。", "エクスポートカート", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cartWindow = new ExportCartWindow(cartPaths)
        {
            Owner = this,
            OpenAssetAsync = path => OpenAssetPathAsync(path, true),
            RemoveAssetsAction = RemoveAssetPathsFromExportCart
        };
        cartWindow.ShowDialog();
    }

    private void RemoveAssetPathsFromExportCart(IReadOnlyList<string> paths)
    {
        if (paths == null || paths.Count <= 0)
            return;

        var removedCount = 0;
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var existingPath = UserSettings.Default.ExportCartPaths
                .FirstOrDefault(saved => saved.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existingPath == null)
                continue;

            UserSettings.Default.ExportCartPaths.Remove(existingPath);
            removedCount++;
        }

        if (removedCount <= 0)
            return;

        UserSettings.Save();
        FLogger.Append(ELog.Information, () => FLogger.Text($"Removed {removedCount} asset(s) from export cart.", Constants.WHITE, true));
    }

    private void OnClearExportCartClick(object sender, RoutedEventArgs e)
    {
        if (UserSettings.Default.ExportCartPaths.Count <= 0)
            return;

        var result = MessageBox.Show(this, "カートの中身をすべて削除しますか？", "エクスポートカート", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != AdonisUI.Controls.MessageBoxResult.OK)
            return;

        UserSettings.Default.ExportCartPaths.Clear();
        UserSettings.Save();
        FLogger.Append(ELog.Information, () => FLogger.Text("Export cart cleared.", Constants.WHITE, true));
    }
}
