using Serilog;
using System;
using System.Collections.Generic; // List<> を使用するために追加
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives; // GeneratorStatus 用
using System.Windows.Input;
using AdonisUI.Controls;
using CUE4Parse.FileProvider.Objects;
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
using FModel.ViewModels.CUE4Parse;

namespace FModel;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow
{
    private void LogToFile(string message)
    {
        Log.Information(message);
    }
    public static MainWindow YesWeCats;
    private ThreadWorkerViewModel _threadWorkerView => ApplicationService.ThreadWorkerView;
    private static ApplicationViewModel _applicationView => ApplicationService.ApplicationView;
    private DiscordHandler _discordHandler => DiscordService.DiscordHandler;

    public ICommand OpenRecentFileCommand { get; }
    public ICommand ClearRecentFilesCommand { get; }

    public MainWindow()
    {
        DataContext = _applicationView;
        InitializeComponent();

        LogToFile("MainWindow initialized.");

        CommandBindings.Add(new CommandBinding(new RoutedCommand("ReloadMappings", typeof(MainWindow), new InputGestureCollection { new KeyGesture(Key.F12) }), OnMappingsReload));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) => OnOpenAvalonFinder()));

        OpenRecentFileCommand = new RelayCommand(OnOpenRecentFile);
        ClearRecentFilesCommand = new RelayCommand(OnClearRecentFiles);

        // テクスチャプレビュー設定の変更を監視
        UserSettings.Default.PropertyChanged += OnUserSettingsPropertyChanged;

        FLogger.Logger = LogRtbName;
        YesWeCats = this;

        // 閲覧履歴メニューの初期化と更新
        UpdateRecentFilesMenu();
        UserSettings.Default.RecentFiles.CollectionChanged += RecentFiles_CollectionChanged;
    }

    private void RecentFiles_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateRecentFilesMenu();
    }

    private void OnClosing(object sender, CancelEventArgs e)
    {
        _discordHandler.Dispose();
        UserSettings.Default.PropertyChanged -= OnUserSettingsPropertyChanged;
        LogToFile("MainWindow closing.");
        if (UserSettings.Default.RestoreTabsOnStartup)
        {
            var tabPaths = _applicationView.CUE4Parse.TabControl.TabsItems.Select(t => t.Entry.Path).Where(p => p != "New Tab").ToList();
            UserSettings.Default.CurrentDir.LastOpenedTabs = tabPaths;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        LogToFile("MainWindow loaded.");
        var newOrUpdated = UserSettings.Default.ShowChangelog;
#if !DEBUG
        ApplicationService.ApiEndpointView.FModelApi.CheckForUpdates(true);
#endif

        switch (UserSettings.Default.AesReload)
        {
            case EAesReload.Always:
                await _applicationView.CUE4Parse.RefreshAesForAllAsync();
                break;
            case EAesReload.OncePerDay when UserSettings.Default.CurrentDir.LastAesReload != DateTime.Today:
                UserSettings.Default.CurrentDir.LastAesReload = DateTime.Today;
                await _applicationView.CUE4Parse.RefreshAesForAllAsync();
                break;
        }

        await ApplicationViewModel.InitOodle();
        await ApplicationViewModel.InitZlib();
        await _applicationView.CUE4Parse.Initialize();
        await _applicationView.AesManager.InitAes();
        await _applicationView.UpdateProvider(true);
#if !DEBUG
        await _applicationView.CUE4Parse.InitInformation();
#endif
        await Task.WhenAll(
            _applicationView.CUE4Parse.VerifyConsoleVariables(),
            _applicationView.CUE4Parse.VerifyOnDemandArchives(),
            _applicationView.CUE4Parse.InitAllMappings(),
            ApplicationViewModel.InitDetex(),
            ApplicationViewModel.InitVgmStream(),
            ApplicationViewModel.InitImGuiSettings(newOrUpdated),
            Task.Run(() =>
            {
                if (UserSettings.Default.DiscordRpc == EDiscordRpc.Always)
                    _discordHandler.Initialize(_applicationView.GameDisplayName);
            })
        ).ConfigureAwait(false);

        await Dispatcher.InvokeAsync(() =>
        {
            LogToFile("MainWindow post-initialization complete.");
            if (UserSettings.Default.RestoreTabsOnStartup && UserSettings.Default.CurrentDir.LastOpenedTabs?.Any() == true)
            {
                var paths = UserSettings.Default.CurrentDir.LastOpenedTabs;
                _applicationView.CUE4Parse.TabControl.RemoveAllTabs(); // "新しいタブ"を削除
                foreach (var path in paths)
                {
                    if (_applicationView.CUE4Parse.Provider.TryGetGameFile(path, out var gameFile))
                        _applicationView.CUE4Parse.TabControl.AddTab(gameFile);
                }
            }
        });

#if DEBUG
        // await _threadWorkerView.Begin(cancellationToken =>
        //     _applicationView.CUE4Parse.Extract(cancellationToken,
        //         _applicationView.CUE4Parse.Provider["Marvel/Content/Marvel/Wwise/Assets/Events/Music/music_new/event/Entry.uasset"]));
#endif
    }

    private void OnGridSplitterDoubleClick(object sender, MouseButtonEventArgs e)
    {
        LogToFile("GridSplitter double-clicked.");
        RootGrid.ColumnDefinitions[0].Width = GridLength.Auto;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        LogToFile($"KeyDown: {e.Key}, Modifiers: {Keyboard.Modifiers}");
        if (e.OriginalSource is TextBox || e.OriginalSource is TextArea && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;

        if (_threadWorkerView.CanBeCanceled && e.Key == Key.Escape)
        {
            LogToFile("Cancel requested by ESC key.");
            _applicationView.Status.SetStatus(EStatusKind.Stopping);
            _threadWorkerView.Cancel();
        }
        else if (_applicationView.Status.IsReady && UserSettings.Default.FeaturePreviewNewAssetExplorer && UserSettings.Default.SwitchAssetExplorer.IsTriggered(e.Key))
            _applicationView.IsAssetsExplorerVisible = !_applicationView.IsAssetsExplorerVisible;
        else if (UserSettings.Default.AssetAddTab.IsTriggered(e.Key))
            _applicationView.CUE4Parse.TabControl.AddTab();
        else if (UserSettings.Default.AssetRemoveTab.IsTriggered(e.Key))
            _applicationView.CUE4Parse.TabControl.RemoveTab();
        else if (UserSettings.Default.AssetLeftTab.IsTriggered(e.Key))
            _applicationView.CUE4Parse.TabControl.GoLeftTab();
        else if (UserSettings.Default.AssetRightTab.IsTriggered(e.Key))
            _applicationView.CUE4Parse.TabControl.GoRightTab();
        // else if (UserSettings.Default.DirLeftTab.IsTriggered(e.Key) && _applicationView.SelectedLeftTabIndex > 0)
        //     _applicationView.SelectedLeftTabIndex--;
        // else if (UserSettings.Default.DirRightTab.IsTriggered(e.Key) && _applicationView.SelectedLeftTabIndex < LeftTabControl.Items.Count - 1)
        //     _applicationView.SelectedLeftTabIndex++;
    }

    private void OnSearchViewClick(object sender, RoutedEventArgs e)
    {
        LogToFile("SearchView opened.");
        var searchView = Helper.GetWindow<SearchView>("検索", () => new SearchView());
        searchView.FocusTab(ESearchViewTab.SearchView);
    }

    private void OnRefViewClick(object sender, RoutedEventArgs e)
    {
        LogToFile("RefView opened.");
        var searchView = Helper.GetWindow<SearchView>("検索ウィンドウ", () => new SearchView());
        searchView.FocusTab(ESearchViewTab.RefView);
    }

    private void OnContentSearchViewClick(object sender, RoutedEventArgs e)
    {
        LogToFile("ContentSearchView opened.");
        Helper.OpenWindow<AdonisWindow>("ファイル内検索", () => new ContentSearchView().Show());
    }

    private void OnProfileViewClick(object sender, RoutedEventArgs e)
    {
        LogToFile("ProfileView opened.");
        Helper.OpenWindow<AdonisWindow>("プロファイル", () => new ProfileWindow().Show());
    }

    private bool _isHandlingTabChange = false;
    private void OnTabItemChange(object sender, SelectionChangedEventArgs e)
    {
        if (_isHandlingTabChange) return;
        _isHandlingTabChange = true;
        try
        {
            LogToFile($"TabItem changed: {((TabControl)sender).SelectedIndex}");
            if (e.OriginalSource is not TabControl tabControl)
                return;

            // SelectedLeftTabIndexはここで変更しない！
            switch (tabControl.SelectedIndex)
            {
                case 0:
                    DirectoryFilesListBox.Focus();
                    break;
                case 1:
                    AssetsFolderName.Focus();
                    break;
                case 2:
                    AssetsListName.Focus();
                    break;
            }
        }
        finally
        {
            _isHandlingTabChange = false;
        }
    }

    private async void OnMappingsReload(object sender, ExecutedRoutedEventArgs e)
    {
        LogToFile("Mappings reloaded.");
        await _applicationView.CUE4Parse.InitAllMappings(true);
    }

    private void OnOpenAvalonFinder()
    {
        LogToFile("AvalonFinder opened.");
        if (_applicationView.IsAssetsExplorerVisible)
        {
            AssetsExplorerSearch.TextBox.Focus();
            AssetsExplorerSearch.TextBox.SelectAll();
        }
        else if (_applicationView.CUE4Parse.TabControl.SelectedTab is { } tab)
        {
            tab.HasSearchOpen = true;
            AvalonEditor.YesWeSearch.Focus();
            AvalonEditor.YesWeSearch.SelectAll();
        }
    }

    private void OnAssetsTreeMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        LogToFile("AssetsTree double-clicked.");
        if (sender is not TreeView { SelectedItem: TreeItem treeItem } || treeItem.Folders.Count > 0) return;

        LeftTabControl.SelectedIndex++;
    }

    // AssetsExplorerは存在しないため、AssetsFolderNameに修正
    private void OnPreviewTexturesToggled(object sender, RoutedEventArgs e) => ItemContainerGenerator_StatusChanged(AssetsListName.ItemContainerGenerator, EventArgs.Empty);
    private void ItemContainerGenerator_StatusChanged(object sender, EventArgs e)
    {
        if (sender is not ItemContainerGenerator { Status: GeneratorStatus.ContainersGenerated } generator)
            return;

        var foundVisibleItem = false;
        var itemCount = generator.Items.Count;

        for (var i = 0; i < itemCount; i++)
        {
            var container = generator.ContainerFromIndex(i);
            if (container == null)
            {
                if (foundVisibleItem) break; // we're past the visible range already
                continue; // keep scrolling to find visible items
            }

            if (container is FrameworkElement { IsVisible: true } && generator.Items[i] is GameFileViewModel file)
            {
                foundVisibleItem = true;
                file.OnIsVisible();
            }
        }
    }

    private bool _isHandlingSelectionChanged;
    private void OnAssetsTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_isHandlingSelectionChanged) return;
        if (sender is not TreeView { SelectedItem: TreeItem }) return;
        try
        {
            //_isHandlingSelectionChanged = true;
            //if (!_applicationView.IsAssetsExplorerVisible)
            //    _applicationView.IsAssetsExplorerVisible = true;
            // _applicationView.SelectedLeftTabIndex = 1; // 無限ループ防止のため削除
        }
        finally
        {
            _isHandlingSelectionChanged = false;
        }
    }    private async void OnAssetsListMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        LogToFile("AssetsList double-clicked.");
        if (sender is not ListBox listBox) return;

        var selectedItems = listBox.SelectedItems.Cast<GameFile>().ToList();
        await _threadWorkerView.Begin(cancellationToken => { _applicationView.CUE4Parse.ExtractSelected(cancellationToken, selectedItems); });
        foreach (var item in selectedItems)
        {
            AddFileToRecent(item.Path);
        }
    }

    private async void OnFolderExtractClick(object sender, RoutedEventArgs e)
    {
        LogToFile("Folder extract requested.");
        if (AssetsFolderName.SelectedItem is TreeItem folder)
        {
            await _threadWorkerView.Begin(cancellationToken => { _applicationView.CUE4Parse.ExtractFolder(cancellationToken, folder); });
        }
    }

    private void OnClearFilterClick(object sender, RoutedEventArgs e)
    {
        LogToFile("Clear filter clicked.");
        if (AssetsFolderName.SelectedItem is TreeItem folder)
        {
            folder.SearchText = string.Empty;
            folder.SelectedCategory = EAssetCategory.All;
        }
    }

    private async void OnFolderExportClick(object sender, RoutedEventArgs e)
    {
        LogToFile("Folder export requested.");
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
        LogToFile("Folder save requested.");
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
    private async void OnFolderTextureClick(object sender, RoutedEventArgs e)
    {
        LogToFile("Folder texture export requested.");
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
        LogToFile("Folder model export requested.");
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
        LogToFile("Folder animation export requested.");
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
        LogToFile("Favorite directory added.");
        if (AssetsFolderName.SelectedItem is not TreeItem folder) return;

        _applicationView.CustomDirectories.Add(new CustomDirectory(folder.Header, folder.PathAtThisPoint));
        FLogger.Append(ELog.Information, () =>
            FLogger.Text($"Successfully saved '{folder.PathAtThisPoint}' as a new favorite directory", Constants.WHITE, true));
    }

    private void OnCopyDirectoryPathClick(object sender, RoutedEventArgs e)
    {
        LogToFile("Directory path copied.");
        if (AssetsFolderName.SelectedItem is not TreeItem folder) return;
        Clipboard.SetText(folder.PathAtThisPoint);
    }

    private void OnDeleteSearchClick(object sender, RoutedEventArgs e)
    {
        LogToFile("Delete search clicked.");
        AssetsSearchName.Text = string.Empty;
        AssetsListName.ScrollIntoView(AssetsListName.SelectedItem);
    }

    private void OnFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        LogToFile("Filter text changed.");
        if (sender is not TextBox textBox || AssetsFolderName.SelectedItem is not TreeItem folder)
            return;

        var filters = textBox.Text.Trim().Split(' ');
        folder.AssetsList.AssetsView.Filter = o => { return o is GameFile entry && filters.All(x => entry.Name.Contains(x, StringComparison.OrdinalIgnoreCase)); };
    }

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        LogToFile("ListBox double-clicked.");
        if (!_applicationView.Status.IsReady || sender is not ListBox listBox) return;
        UserSettings.Default.LoadingMode = ELoadingMode.Multiple;
        _applicationView.LoadingModes.LoadCommand.Execute(listBox.SelectedItems);
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        LogToFile($"PreviewKeyDown: {e.Key}");
        if (!_applicationView.Status.IsReady || sender is not ListBox listBox)
            return;
        if (e.Key != Key.Enter)
            return;
        if (listBox.SelectedItem == null)
            return;

        switch (listBox.SelectedItem)
        {
            case GameFileViewModel file:
                _applicationView.IsAssetsExplorerVisible = false;
                // ApplicationService.ApplicationView.SelectedLeftTabIndex = 2; // 無限ループ防止のため削除
                await _threadWorkerView.Begin(cancellationToken => _applicationView.CUE4Parse.ExtractSelected(cancellationToken, [file.Asset]));
                break;
            case TreeItem folder:
                // ApplicationService.ApplicationView.SelectedLeftTabIndex = 1; // 無限ループ防止のため削除

                var parent = folder.Parent;
                while (parent != null)
                {
                    parent.IsExpanded = true;
                    parent = parent.Parent;
                }

                var childFolder = folder;
                while (childFolder.Folders.Count == 1 && childFolder.AssetsList.Assets.Count == 0)
                {
                    childFolder.IsExpanded = true;
                    childFolder = childFolder.Folders[0];
                }

                childFolder.IsExpanded = true;
                childFolder.IsSelected = true;
                break;
        }
    }

    private void FeaturePreviewOnUnchecked(object sender, RoutedEventArgs e)
    {
        LogToFile("Feature preview unchecked.");
        _applicationView.IsAssetsExplorerVisible = false;
    }

    private async void OnFoldersPreviewKeyDown(object sender, KeyEventArgs e)
    {
        LogToFile($"FoldersPreviewKeyDown: {e.Key}");
        if (e.Key != Key.Enter || sender is not TreeView treeView || treeView.SelectedItem is not TreeItem folder)
            return;

        if ((folder.IsExpanded || folder.Folders.Count == 0) && folder.AssetsList.Assets.Count > 0)
        {
            // _applicationView.SelectedLeftTabIndex++; // 無限ループ防止のため削除
            return;
        }

        var childFolder = folder;
        while (childFolder.Folders.Count == 1 && childFolder.AssetsList.Assets.Count == 0)
        {
            childFolder.IsExpanded = true;
            childFolder = childFolder.Folders[0];
        }

        childFolder.IsExpanded = true;
        childFolder.IsSelected = true;
    }

    private async void OnOpenRecentFile(object? parameter)
    {
        LogToFile($"Open recent file: {parameter}");
        if (parameter is not string filePath || string.IsNullOrEmpty(filePath)) return;

        if (_applicationView.CUE4Parse.Provider.TryGetGameFile(filePath, out var gameFile))
        {
            var selectedItems = new List<GameFile> { gameFile };
            await _threadWorkerView.Begin(cancellationToken => { _applicationView.CUE4Parse.ExtractSelected(cancellationToken, selectedItems); });
            FLogger.Append(ELog.Information, () => FLogger.Text($"Opening recent file: {filePath}", Constants.WHITE, true));
        }
        else
        {
            FLogger.Append(ELog.Warning, () => FLogger.Text($"Failed to open recent file: {filePath}. File not found in provider.", Constants.RED, true));
        }

        // Add to recent files (and ensure no duplicates, limit size)
        AddFileToRecent(filePath);
    }

    private void OnClearRecentFiles(object? parameter)
    {
        LogToFile("Clear recent files requested.");
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
        if (string.IsNullOrEmpty(filePath)) return;

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

//「テスト」内のボタンを押した際に出てくる注意ウインドウ
    private bool ShowBetaFeatureWarning()
    {
        var message = "これらの機能はβ、α版です。意" +
                      "図しない処理が行われる可能性があります。" +
                      "これらの不具合に開発者は一切の責任を負いません。";
        var caption = "注意";
        var result = AdonisUI.Controls.MessageBox.Show(this, message, caption, AdonisUI.Controls.MessageBoxButton.OKCancel, AdonisUI.Controls.MessageBoxImage.Warning);
        return result == AdonisUI.Controls.MessageBoxResult.OK;
    }

    private async void OnAthenaAllCosmeticsClick(object sender, RoutedEventArgs e)
    {
        if (ShowBetaFeatureWarning())
            await GenerateAllCosmeticsFeature.ExecuteAsync();
    }

    private async void OnAthenaNewCosmeticsClick(object sender, RoutedEventArgs e)
    {
        if (ShowBetaFeatureWarning())
            await GenerateNewCosmeticsFeature.ExecuteAsync();
    }

    private async void OnAthenaNewCosmeticsWithPaksClick(object sender, RoutedEventArgs e)
    {
        if (ShowBetaFeatureWarning())
            await GenerateNewCosmeticsWithPaksFeature.ExecuteAsync();
    }

    private async void OnAthenaCustomByIdClick(object sender, RoutedEventArgs e)
    {
        if (ShowBetaFeatureWarning())
            await GenerateCustomCosmeticsByIdFeature.ExecuteAsync();
    }

    private void OnAthenaPakCosmeticsClick(object sender, RoutedEventArgs e)
    {
        if (ShowBetaFeatureWarning())
            AthenaFeatureBase.LogNotImplemented("Pak Cosmetics");
    }
    private void OnAthenaPaksBulkClick(object sender, RoutedEventArgs e)
    {
        if (ShowBetaFeatureWarning())
            AthenaFeatureBase.LogNotImplemented("Paks Bulk");
    }

    private void OnAthenaBackClick(object sender, RoutedEventArgs e)
    {
        if (ShowBetaFeatureWarning())
            AthenaFeatureBase.LogNotImplemented("Back");
    }

    private void OnUserSettingsPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        // if (e.PropertyName == nameof(UserSettings.PreviewTexturesAssetExplorer))
        // {
        //     RefreshAssetListPreview();
        // }
    }

    private void RefreshAssetListPreview()
    {
        if (AssetsFolderName.SelectedItem is not TreeItem folder)
            return;

        // アセットリストのプレビュー画像をリフレッシュ
        foreach (var item in folder.AssetsList.Assets)
        {
            if (item is GameFileViewModel viewModel)
            {
                if (UserSettings.Default.PreviewTexturesAssetExplorer)
                {
                    viewModel.OnIsVisible();
                }
            }
        }
        
        // ビューを更新
        folder.AssetsList.AssetsView.Refresh();
    }
}
