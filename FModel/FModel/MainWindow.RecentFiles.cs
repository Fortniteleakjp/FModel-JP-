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
    private void RecentFiles_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateRecentFilesMenu();
    }

    private async void OnOpenRecentFile(object? parameter)
    {
        if (parameter is not string filePath || string.IsNullOrEmpty(filePath))
            return;

        await OpenAssetPathAsync(filePath, true);
    }

    private async Task OpenAssetPathAsync(string filePath, bool addToRecent)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        if (_applicationView.CUE4Parse.Provider.TryGetGameFile(filePath, out var gameFile))
        {
            var selectedItems = new List<GameFile> { gameFile };
            await _threadWorkerView.Begin(cancellationToken =>
            {
                try
                {
                    _applicationView.CUE4Parse.ExtractSelected(cancellationToken, selectedItems);
                }
                catch (Exception ex)
                {
                    CheckForMappingError(ex);
                    throw;
                }
            });
            FLogger.Append(ELog.Information, () => FLogger.Text($"Opening recent file: {filePath}", Constants.WHITE, true));
        }
        else
        {
            FLogger.Append(ELog.Warning, () => FLogger.Text($"Failed to open recent file: {filePath}. File not found in provider.", Constants.RED, true));
        }

        if (addToRecent)
            AddFileToRecent(filePath);
    }

    private void OnClearRecentFiles(object? parameter)
    {
        FLogger.Append(ELog.Information, () => FLogger.Text("Attempting to clear recent files.", Constants.WHITE, true));
        UserSettings.Default.RecentFiles.Clear();
        UserSettings.Save();
        FLogger.Append(ELog.Information, () => FLogger.Text("Recent files cleared and settings saved.", Constants.WHITE, true));
    }

    private void UpdateRecentFilesMenu()
    {
        // 「履歴をクリア」メニュー項目を一時的に保存
        var clearMenuItem = RecentFilesMenuItem.Items.OfType<MenuItem>().FirstOrDefault(item => item.Command == ClearRecentFilesCommand);

        // 既存の履歴アイテムをクリア
        RecentFilesMenuItem.Items.Clear();

        foreach (var filePath in UserSettings.Default.RecentFiles)
        {
            var menuItem = new MenuItem
            {
                Header = filePath,
                Command = OpenRecentFileCommand,
                CommandParameter = filePath
            };
            RecentFilesMenuItem.Items.Add(menuItem);
        }

        // 「履歴をクリア」メニュー項目を最後に追加
        if (clearMenuItem != null)
        {
            if (RecentFilesMenuItem.Items.Count > 0)
            {
                RecentFilesMenuItem.Items.Add(new Separator());
            }
            RecentFilesMenuItem.Items.Add(clearMenuItem);
        }
        else // もしクリアメニュー項目が見つからなかった場合、新しく作成して追加
        {
            if (RecentFilesMenuItem.Items.Count > 0)
            {
                RecentFilesMenuItem.Items.Add(new Separator());
            }
            RecentFilesMenuItem.Items.Add(new MenuItem
            {
                Header = "履歴をクリア",
                Command = ClearRecentFilesCommand
            });
        }
    }

    private void AddFileToRecent(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return;

        // Remove if already exists to move it to the top
        UserSettings.Default.RecentFiles.Remove(filePath);

        // Add to the top
        UserSettings.Default.RecentFiles.Insert(0, filePath);

        // Limit the number of recent files (e.g., to 10)
        const int maxRecentFiles = 10;
        while (UserSettings.Default.RecentFiles.Count > maxRecentFiles)
        {
            UserSettings.Default.RecentFiles.RemoveAt(UserSettings.Default.RecentFiles.Count - 1);
        }

        UserSettings.Save();
    }
}
