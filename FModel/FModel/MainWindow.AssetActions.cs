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
    private void OnAssetsTreeMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeView { SelectedItem: TreeItem treeItem } || treeItem.Folders.Count > 0)
            return;

        LeftTabControl.SelectedIndex++;
    }

    private async void OnAssetsListMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        var selectedItems = listBox.SelectedItems.Cast<GameFile>().ToList();

        if (selectedItems.Count == 1 && selectedItems[0] is { } file && file.Extension.Equals("mp4", StringComparison.OrdinalIgnoreCase))
        {
            await OpenVideoPlayer(file);
            AddFileToRecent(file.Path);
            return;
        }

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
        foreach (var item in selectedItems)
        {
            AddFileToRecent(item.Path);
        }
    }

    private void OnSaveFontClick(object sender, RoutedEventArgs e)
    {
        if (AssetsListName.SelectedItems.Count == 0) return;

        var selectedFiles = AssetsListName.SelectedItems.OfType<GameFile>()
            .Where(x => x.Extension.Equals("ufont", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (selectedFiles.Count == 0) return;

        if (selectedFiles.Count == 1)
        {
            var gameFile = selectedFiles[0];
            var saveFileDialog = new SaveFileDialog
            {
                Filter = "TrueType Font (*.ttf)|*.ttf",
                FileName = Path.ChangeExtension(gameFile.Name, "ttf")
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    var data = gameFile.Read();
                    var result = UFontExtractor.ExtractTTF(data, saveFileDialog.FileName);
                    if (!result.StartsWith("抽出成功"))
                    {
                        MessageBox.Show($"エラー: {result}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"エラーが発生しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        else
        {
            var folderDialog = new VistaFolderBrowserDialog
            {
                Description = "フォントの保存先フォルダを選択してください",
                UseDescriptionForTitle = true
            };

            if (folderDialog.ShowDialog() == true)
            {
                var outputFolder = folderDialog.SelectedPath;
                int successCount = 0;
                int failCount = 0;

                foreach (var gameFile in selectedFiles)
                {
                    try
                    {
                        var data = gameFile.Read();
                        var outputPath = Path.Combine(outputFolder, Path.ChangeExtension(gameFile.Name, "ttf"));
                        var result = UFontExtractor.ExtractTTF(data, outputPath, true);
                        if (result.StartsWith("抽出成功")) successCount++;
                        else failCount++;
                    }
                    catch (Exception ex)
                    {
                        FLogger.Append(ELog.Error, () => FLogger.Text($"Failed to convert {gameFile.Name}: {ex.Message}", Constants.RED));
                        failCount++;
                    }
                }

                MessageBox.Show($"{successCount} 個のファイルを変換しました。" + (failCount > 0 ? $"\n{failCount} 個のファイルの変換に失敗しました。" : ""), "一括変換完了", MessageBoxButton.OK, MessageBoxImage.Information);
                if (successCount > 0)
                {
                    Process.Start("explorer.exe", outputFolder);
                }
            }
        }
    }

    private async void OnPlayVideoClick(object sender, RoutedEventArgs e)
    {
        if (AssetsListName.SelectedItem is GameFile file)
        {
            await OpenVideoPlayer(file);
        }
    }

    private async Task OpenVideoPlayer(GameFile file)
    {
        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_{file.Name}");
            await Task.Run(() =>
            {
                var bytes = file.Read();
                File.WriteAllBytes(tempPath, bytes);
            });

            var videoPlayer = new VideoPlayer(tempPath);
            var tab = new FModel.ViewModels.TabItem(file, file.Name) { Content = videoPlayer };
            _applicationView.CUE4Parse.TabControl.AddTab(tab);
            _applicationView.CUE4Parse.TabControl.SelectedTab = tab;
        }
        catch (Exception ex)
        {
            FLogger.Append(ELog.Error, () => FLogger.Text($"Failed to open video: {ex.Message}", Constants.RED));
        }
    }

    private void OnShowReferenceChainClick(object sender, RoutedEventArgs e)
    {
        if (AssetsListName.SelectedItems.Count == 0) return;
        var assets = AssetsListName.SelectedItems.Cast<GameFile>().ToList();
        var window = new ReferenceChainWindow(assets);
        window.Show();
    }

    private async void OnFolderExtractClick(object sender, RoutedEventArgs e)
    {
        if (AssetsFolderName.SelectedItem is TreeItem folder)
        {
            await _threadWorkerView.Begin(cancellationToken => { _applicationView.CUE4Parse.ExtractFolder(cancellationToken, folder); });
        }
    }

    private async void OnFolderExportClick(object sender, RoutedEventArgs e)
    {
        if (AssetsFolderName.SelectedItem is TreeItem folder)
        {
            await _threadWorkerView.Begin(cancellationToken => { _applicationView.CUE4Parse.ExportFolder(cancellationToken, folder); });
            FLogger.Append(ELog.Information, () =>
            {
                FLogger.Text("Successfully exported ", Constants.WHITE);
                FLogger.Link(folder.PathAtThisPoint, UserSettings.Default.RawDataDirectory, true);
            });
        }
    }

    private async void OnFolderSaveClick(object sender, RoutedEventArgs e)
    {
        if (AssetsFolderName.SelectedItem is TreeItem folder)
        {
            await _threadWorkerView.Begin(cancellationToken => { _applicationView.CUE4Parse.SaveFolder(cancellationToken, folder); });
            FLogger.Append(ELog.Information, () =>
            {
                FLogger.Text("Successfully saved ", Constants.WHITE);
                FLogger.Link(folder.PathAtThisPoint, UserSettings.Default.PropertiesDirectory, true);
            });
        }
    }
    private async void OnFolderSaveDecompiled(object sender, RoutedEventArgs e)
    {
        if (AssetsFolderName.SelectedItem is TreeItem folder)
        {
            await _threadWorkerView.Begin(cancellationToken => { _applicationView.CUE4Parse.SaveDecompiled(cancellationToken, folder); });
            FLogger.Append(ELog.Information, () =>
            {
                FLogger.Text("Successfully saved ", Constants.WHITE);
                FLogger.Link(folder.PathAtThisPoint, UserSettings.Default.PropertiesDirectory, true);
            });
        }
    }
    private async void OnFolderTextureClick(object sender, RoutedEventArgs e)
    {
        if (AssetsFolderName.SelectedItem is TreeItem folder)
        {
            await _threadWorkerView.Begin(cancellationToken => { _applicationView.CUE4Parse.TextureFolder(cancellationToken, folder); });
            FLogger.Append(ELog.Information, () =>
            {
                FLogger.Text("Successfully saved textures from ", Constants.WHITE);
                FLogger.Link(folder.PathAtThisPoint, UserSettings.Default.TextureDirectory, true);
            });
        }
    }

    private async void OnFolderModelClick(object sender, RoutedEventArgs e)
    {
        if (AssetsFolderName.SelectedItem is TreeItem folder)
        {
            await _threadWorkerView.Begin(cancellationToken => { _applicationView.CUE4Parse.ModelFolder(cancellationToken, folder); });
            FLogger.Append(ELog.Information, () =>
            {
                FLogger.Text("Successfully saved models from ", Constants.WHITE);
                FLogger.Link(folder.PathAtThisPoint, UserSettings.Default.ModelDirectory, true);
            });
        }
    }

    private async void OnFolderAnimationClick(object sender, RoutedEventArgs e)
    {
        if (AssetsFolderName.SelectedItem is TreeItem folder)
        {
            await _threadWorkerView.Begin(cancellationToken => { _applicationView.CUE4Parse.AnimationFolder(cancellationToken, folder); });
            FLogger.Append(ELog.Information, () =>
            {
                FLogger.Text("Successfully saved animations from ", Constants.WHITE);
                FLogger.Link(folder.PathAtThisPoint, UserSettings.Default.ModelDirectory, true);
            });
        }
    }

    private void OnFavoriteDirectoryClick(object sender, RoutedEventArgs e)
    {
        if (AssetsFolderName.SelectedItem is not TreeItem folder)
            return;

        _applicationView.CustomDirectories.Add(new CustomDirectory(folder.Header, folder.PathAtThisPoint));
        FLogger.Append(ELog.Information, () =>
            FLogger.Text($"Successfully saved '{folder.PathAtThisPoint}' as a new favorite directory", Constants.WHITE, true));
    }

    private void OnCopyDirectoryPathClick(object sender, RoutedEventArgs e)
    {
        if (AssetsFolderName.SelectedItem is not TreeItem folder)
            return;
        Clipboard.SetText(folder.PathAtThisPoint);
    }

    private void OnAssetsFolderSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        var target = e.NewValue ?? _applicationView?.CUE4Parse?.AssetsFolder;
        ApplyNewExplorerFilter(target);

        if (e.NewValue is TreeItem selectedFolder)
        {
            TrackNewExplorerLocation(selectedFolder);
            UpdateNewExplorerPathBox(selectedFolder);
        }
        else
        {
            UpdateNewExplorerPathBox();
        }

        UpdateNewExplorerNavigationButtons();
    }

    private void OnDeleteSearchClick(object sender, RoutedEventArgs e)
    {
        AssetsSearchName.Text = string.Empty;
        AssetsListName.ScrollIntoView(AssetsListName.SelectedItem);
    }

    private void OnFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox || AssetsFolderName.SelectedItem is not TreeItem folder)
            return;

        var filters = textBox.Text.Trim().Split(' ');
        folder.AssetsList.AssetsView.Filter = o => { return o is GameFile entry && filters.All(x => entry.Name.Contains(x, StringComparison.OrdinalIgnoreCase)); };
    }

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_applicationView.Status.IsReady || sender is not ListBox listBox)
            return;
        UserSettings.Default.LoadingMode = ELoadingMode.Multiple;
        _applicationView.LoadingModes.LoadCommand.Execute(listBox.SelectedItems);
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_applicationView.Status.IsReady || sender is not ListBox listBox)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                var selectedItems = listBox.SelectedItems.Cast<GameFile>().ToList();
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
                foreach (var item in selectedItems)
                {
                    AddFileToRecent(item.Path);
                }
                break;
        }
    }

    private void OnUserSettingsPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UserSettings.PreviewTexturesAssetExplorer))
        {
            RefreshAssetListPreview();
        }
    }

    private void RefreshAssetListPreview()
    {
        if (AssetsFolderName.SelectedItem is not TreeItem folder)
            return;

        // アセットリストのプレビュー画像をリフレッシュ
        foreach (var item in folder.AssetsList.Assets)
        {
            if (item is GameFile gameFile)
            {
                var viewModel = new GameFileViewModel(gameFile);
                if (UserSettings.Default.PreviewTexturesAssetExplorer)
                {
                    viewModel.OnVisibleChanged(true);
                }
            }
        }

        // ビューを更新
        folder.AssetsList.AssetsView.Refresh();
    }

    private void OnAssetsOpenGraphViewerClick(object sender, RoutedEventArgs e)
    {
        if (AssetsListName?.SelectedItems == null || AssetsListName.SelectedItems.Count == 0)
            return;

        var selectedFile = AssetsListName.SelectedItems.OfType<GameFile>().FirstOrDefault();
        if (selectedFile is null)
            return;

        _applicationView.CUE4Parse.ShowAnimGraph(selectedFile);
    }

    private async void OnAssetListClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
        {
            if (path.StartsWith("Search:"))
            {
                var query = path.Substring(7);
                var searchWeaponDefinitionsOnly = string.Equals(query, "WID_", StringComparison.OrdinalIgnoreCase) ||
                                                  string.Equals(query, "WeaponOnly", StringComparison.OrdinalIgnoreCase);
                var files = await Task.Run(() => BuildSearchResultFiles(query, searchWeaponDefinitionsOnly));

                if (files.Count > 0)
                {
                    if (AssetsFolderName.SelectedItem is TreeItem selected)
                        selected.IsSelected = false;
                    _applicationView.CUE4Parse.AssetsFolder.Folders?.Clear();
                    _applicationView.CUE4Parse.AssetsFolder.BulkPopulate(files);
                    NewExplorerMenuItem.IsChecked = true;
                    FLogger.Append(ELog.Information, () => FLogger.Text($"Found {files.Count} items matching '{query}'", Constants.WHITE));
                }
                else
                {
                    FLogger.Append(ELog.Warning, () => FLogger.Text($"No assets found matching: {query}", Constants.YELLOW));
                }
                return;
            }
            else if (path.StartsWith("Filter:"))
            {
                AssetsSearchName.Text = path.Substring(7);
                LeftTabControl.SelectedIndex = 2;
                return;
            }
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            var currentFolders = _applicationView.CUE4Parse.AssetsFolder.Folders;
            TreeItem targetItem = null;
            var pathItems = new List<TreeItem>();
            foreach (var part in parts)
            {
                if (currentFolders == null)
                {
                    targetItem = null;
                    break;
                }
                targetItem = currentFolders.FirstOrDefault(x => x.Header.Equals(part, StringComparison.OrdinalIgnoreCase));
                if (targetItem == null) break;
                pathItems.Add(targetItem);
                currentFolders = targetItem.Folders;
            }
            if (targetItem != null)
            {
                foreach (var item in pathItems) item.IsExpanded = true;
                targetItem.IsSelected = true;
                AssetsFolderName.Focus();
            }
            else
            {
                FLogger.Append(ELog.Warning, () => FLogger.Text($"Directory not found: {path}", Constants.YELLOW));
            }
        }
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        if (_applicationView.CUE4Parse.Provider == null) return;

        if (_applicationView.LoadingModes.LoadCommand.CanExecute(DirectoryFilesListBox.SelectedItems))
            _applicationView.LoadingModes.LoadCommand.Execute(DirectoryFilesListBox.SelectedItems);

        NewExplorerMenuItem.IsChecked = false;
        LeftTabControl.SelectedIndex = 1;
    }
}
