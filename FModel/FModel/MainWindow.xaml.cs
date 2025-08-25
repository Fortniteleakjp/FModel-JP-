using System;
using System.Collections.Generic; // List<> を使用するために追加
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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

namespace FModel;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow
{
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
    }

    private void OnSearchViewClick(object sender, RoutedEventArgs e)
    {
        Helper.OpenWindow<AdonisWindow>("検索ウィンドウ", () => new SearchView().Show());
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
        if (sender is not TreeView { SelectedItem: TreeItem treeItem } || treeItem.Folders.Count > 0) return;

        LeftTabControl.SelectedIndex++;
    }

    private async void OnAssetsListMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
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
        if (AssetsFolderName.SelectedItem is not TreeItem folder) return;

        _applicationView.CustomDirectories.Add(new CustomDirectory(folder.Header, folder.PathAtThisPoint));
        FLogger.Append(ELog.Information, () =>
            FLogger.Text($"Successfully saved '{folder.PathAtThisPoint}' as a new favorite directory", Constants.WHITE, true));
    }

    private void OnCopyDirectoryPathClick(object sender, RoutedEventArgs e)
    {
        if (AssetsFolderName.SelectedItem is not TreeItem folder) return;
        Clipboard.SetText(folder.PathAtThisPoint);
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
        if (!_applicationView.Status.IsReady || sender is not ListBox listBox) return;
        UserSettings.Default.LoadingMode = ELoadingMode.Multiple;
        _applicationView.LoadingModes.LoadCommand.Execute(listBox.SelectedItems);
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_applicationView.Status.IsReady || sender is not ListBox listBox) return;

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
}
