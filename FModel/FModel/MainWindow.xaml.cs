using System;
using System.Collections.Generic; // List<> を使用するために追加
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdonisUI.Controls;
using CUE4Parse.UE4.Assets;
using CUE4Parse.FileProvider;
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

namespace FModel;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow
{
    // NEW エクスプローラー ON/OFF切り替え（タブ制御）
    private const string ExplorerTabHeader = "NEW エクスプローラー";
    private FModel.ViewModels.TabItem _explorerTabVm;
    private void OnNewExplorerChecked(object sender, RoutedEventArgs e)
    {
        // 現在選択中のファイルまたはフォルダのパスを取得
        string selectedPath = null;
        // ファイルリスト(ListBox)で選択されている場合
        if (AssetsListName?.SelectedItem is GameFile file)
        {
            selectedPath = file.Path;
        }
        // フォルダツリー(TreeView)で選択されている場合
        else if (AssetsFolderName?.SelectedItem is TreeItem folder)
        {
            selectedPath = folder.PathAtThisPoint;
        }
        // どちらも選択されていない場合、selectedPathはnullになり、エクスプローラーはルートを表示します
        if (_explorerTabVm == null)
        {
            // ExplorerTab用のViewModelを生成
            _explorerTabVm = new FModel.ViewModels.TabItem(null, "NEW エクスプローラー");
            _explorerTabVm.Content = new FModel.Views.ExplorerTab(selectedPath);
            _applicationView.CUE4Parse.TabControl.AddTab(_explorerTabVm);
        }
        _applicationView.CUE4Parse.TabControl.SelectedTab = _explorerTabVm;
    }

    private void OnNewExplorerUnchecked(object sender, RoutedEventArgs e)
    {
        if (_explorerTabVm != null)
        {
            _applicationView.CUE4Parse.TabControl.RemoveTab(_explorerTabVm);
            _explorerTabVm = null;
        }
    }
    public static MainWindow YesWeCats;
    private ThreadWorkerViewModel _threadWorkerView => ApplicationService.ThreadWorkerView;
    private ApplicationViewModel _applicationView => ApplicationService.ApplicationView;
    private DiscordHandler _discordHandler => DiscordService.DiscordHandler;

    public ICommand OpenRecentFileCommand { get; }
    public ICommand ClearRecentFilesCommand { get; }

    public MainWindow()
    {
        CommandBindings.Add(new CommandBinding(new RoutedCommand("ReloadMappings", typeof(MainWindow), new InputGestureCollection { new KeyGesture(Key.F12) }), OnMappingsReload));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) => OnOpenAvalonFinder()));

        OpenRecentFileCommand = new RelayCommand(OnOpenRecentFile);
        ClearRecentFilesCommand = new RelayCommand(OnClearRecentFiles);

        DataContext = _applicationView;
        InitializeComponent();

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
        if (UserSettings.Default.RestoreTabsOnStartup)
        {
            var tabPaths = _applicationView.CUE4Parse.TabControl.TabsItems.Select(t => t.Entry.Path).Where(p => p != "New Tab").ToList();
            UserSettings.Default.CurrentDir.LastOpenedTabs = tabPaths;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
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
        await ApplicationViewModel.InitACL();
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
        RootGrid.ColumnDefinitions[0].Width = GridLength.Auto;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || e.OriginalSource is TextArea && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;

        if (_threadWorkerView.CanBeCanceled && e.Key == Key.Escape)
        {
            _applicationView.Status.SetStatus(EStatusKind.Stopping);
            _threadWorkerView.Cancel();
        }
        else if (_applicationView.Status.IsReady && e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            OnSearchViewClick(null, null);
        else if (e.Key == Key.Left && _applicationView.CUE4Parse.TabControl.SelectedTab.HasImage)
            _applicationView.CUE4Parse.TabControl.SelectedTab.GoPreviousImage();
        else if (e.Key == Key.Right && _applicationView.CUE4Parse.TabControl.SelectedTab.HasImage)
            _applicationView.CUE4Parse.TabControl.SelectedTab.GoNextImage();
        else if (UserSettings.Default.AssetAddTab.IsTriggered(e.Key))
            _applicationView.CUE4Parse.TabControl.AddTab();
        else if (UserSettings.Default.AssetRemoveTab.IsTriggered(e.Key))
            _applicationView.CUE4Parse.TabControl.RemoveTab();
        else if (UserSettings.Default.AssetLeftTab.IsTriggered(e.Key))
            _applicationView.CUE4Parse.TabControl.GoLeftTab();
        else if (UserSettings.Default.AssetRightTab.IsTriggered(e.Key))
            _applicationView.CUE4Parse.TabControl.GoRightTab();
        else if (UserSettings.Default.DirLeftTab.IsTriggered(e.Key) && LeftTabControl.SelectedIndex > 0)
            LeftTabControl.SelectedIndex--;
        else if (UserSettings.Default.DirRightTab.IsTriggered(e.Key) && LeftTabControl.SelectedIndex < LeftTabControl.Items.Count - 1)
            LeftTabControl.SelectedIndex++;
        else
            return;
    }

    private void OnSearchViewClick(object sender, RoutedEventArgs e)
    {
        Helper.OpenWindow<AdonisWindow>("検索ウィンドウ", () => new SearchView().Show());
    }

    private void OnContentSearchViewClick(object sender, RoutedEventArgs e)
    {
        Helper.OpenWindow<AdonisWindow>("ファイル内検索", () => new ContentSearchView().Show());
    }

    private void OnProfileViewClick(object sender, RoutedEventArgs e)
    {
        Helper.OpenWindow<AdonisWindow>("プロファイル", () => new ProfileWindow().Show());
    }

    private void OnTabItemChange(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is not TabControl tabControl)
            return;

        (tabControl.SelectedItem as System.Windows.Controls.TabItem)?.Focus();
    }

    private async void OnMappingsReload(object sender, ExecutedRoutedEventArgs e)
    {
        await _applicationView.CUE4Parse.InitAllMappings(true);
    }

    private void OnOpenAvalonFinder()
    {
        _applicationView.CUE4Parse.TabControl.SelectedTab.HasSearchOpen = true;
    }

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

        await _threadWorkerView.Begin(cancellationToken => { _applicationView.CUE4Parse.ExtractSelected(cancellationToken, selectedItems); });
        foreach (var item in selectedItems)
        {
            AddFileToRecent(item.Path);
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
                await _threadWorkerView.Begin(cancellationToken => { _applicationView.CUE4Parse.ExtractSelected(cancellationToken, selectedItems); });
                foreach (var item in selectedItems)
                {
                    AddFileToRecent(item.Path);
                }
                break;
        }
    }

    private async void OnOpenRecentFile(object? parameter)
    {
        if (parameter is not string filePath || string.IsNullOrEmpty(filePath))
            return;

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

    private void TabControlName_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {

    }

    private void NewExplorerMenuItem_Click()
    {

    }

    private void TabControlName_SelectionChanged_1(object sender, SelectionChangedEventArgs e)
    {

    }

    private void OnNewExplorerFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyNewExplorerFilter();
    }

    private void OnNewExplorerClassFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyNewExplorerFilter();
    }

    private void ApplyNewExplorerFilter(object dataContext = null)
    {
        if (NewExplorerSearchBox == null || NewExplorerClassFilter == null) return;

        var filterText = NewExplorerSearchBox.Text;
        var filters = filterText.Trim().Split(' ');
        var hasTextFilter = !string.IsNullOrWhiteSpace(filterText);

        var selectedItem = NewExplorerClassFilter.SelectedItem as ComboBoxItem;
        var selectedClass = selectedItem?.Content?.ToString();
        var hasClassFilter = !string.IsNullOrEmpty(selectedClass) && selectedClass != "All Classes";

        bool FilterAsset(object o)
        {
            if (o is not GameFile file) return false;

            // Text Filter
            if (hasTextFilter)
            {
                if (!filters.All(x => file.Name.Contains(x, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }

            // Class Filter
            if (hasClassFilter)
            {
                // Optimization: Skip if not uasset/umap
                if (!file.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                    !file.Path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                try
                {
                    if (_applicationView.CUE4Parse.Provider.TryLoadPackage(file, out var package))
                    {
                        var export = package.GetExports().FirstOrDefault();
                        if (export == null) return false;
                        return string.Equals(export.ExportType, selectedClass, StringComparison.OrdinalIgnoreCase);
                    }
                    return false;
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }

        dataContext ??= NewExplorerGrid.DataContext;

        if (dataContext is FModel.ViewModels.AssetsFolderViewModel rootVm)
        {
            rootVm.FoldersView.Filter = o =>
            {
                if (!hasTextFilter) return true;
                return o is FModel.ViewModels.TreeItem item && filters.All(x => item.Header.Contains(x, StringComparison.OrdinalIgnoreCase));
            };
            rootVm.FoldersView.Refresh();
        }
        else if (dataContext is FModel.ViewModels.TreeItem treeItem)
        {
            treeItem.FoldersView.Filter = o =>
            {
                if (!hasTextFilter) return true;
                return o is FModel.ViewModels.TreeItem item && filters.All(x => item.Header.Contains(x, StringComparison.OrdinalIgnoreCase));
            };
            treeItem.FoldersView.Refresh();

            treeItem.AssetsList.AssetsView.Filter = FilterAsset;
            treeItem.AssetsList.AssetsView.Refresh();
        }
    }

    private void OnNewExplorerDeleteSearchClick(object sender, RoutedEventArgs e)
    {
        NewExplorerSearchBox.Text = string.Empty;
    }

    private void OnNewExplorerListMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // リストボックスで選択されたアイテムを取得（動的な型解決を使用）
        if (NewExplorerListBox.SelectedItem is { } folder)
        {
            // 選択されたフォルダの IsSelected プロパティを true に設定
            // これによりツリービューの選択が更新され、連動してNEWエクスプローラーの表示も更新されます
            try
            {
                // folderオブジェクトが IsSelected/IsExpanded プロパティを持っていると仮定
                dynamic dynamicFolder = folder;
                dynamicFolder.IsSelected = true;
                dynamicFolder.IsExpanded = true;
            }
            catch
            {
                // プロパティが存在しない場合の安全策
            }
        }
    }
    private void OnNewExplorerFileMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // クリックされた場所がアイテム上か確認（余白のダブルクリックを無視）
        var item = NewExplorerFilesListBox.ContainerFromElement(e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item == null)
            return;

        // ViewModelを取得してコマンドを実行
        if (DataContext is ApplicationViewModel vm)
        {
            // コマンドパラメータを作成 ("Assets_Extract_New_Tab", 選択されたアイテム)
            var parameters = new object[] { "Assets_Extract_New_Tab", NewExplorerFilesListBox.SelectedItems };

            if (vm.RightClickMenuCommand.CanExecute(parameters))
            {
                vm.RightClickMenuCommand.Execute(parameters);
            }
        }

        // NEW エクスプローラー UI を閉じる (メニューのチェックを外す)
        NewExplorerMenuItem.IsChecked = false;
    }

    private void OnAssetListClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
        {
            if (path.StartsWith("Search:"))
            {
                var query = path.Substring(7);
                var files = new List<GameFile>();
                var stack = new Stack<IEnumerable<TreeItem>>();

                if (_applicationView.CUE4Parse.AssetsFolder.Folders != null)
                    stack.Push(_applicationView.CUE4Parse.AssetsFolder.Folders);

                while (stack.Count > 0)
                {
                    var folders = stack.Pop();
                    foreach (var folder in folders)
                    {
                        if (folder.AssetsList?.Assets != null)
                        {
                            foreach (var asset in folder.AssetsList.Assets)
                            {
                                if (asset is GameFile gf && gf.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                                    files.Add(gf);
                            }
                        }

                        if (folder.Folders != null && folder.Folders.Count > 0)
                            stack.Push(folder.Folders);
                    }
                }

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
