using System;
using System.Collections.Generic; // List<> を使用するために追加
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow
{
    // NEW エクスプローラー ON/OFF切り替え（タブ制御）
    private const string ExplorerTabHeader = "NEW エクスプローラー";
    private FModel.ViewModels.TabItem _explorerTabVm;
    private readonly List<string> _newExplorerLocationHistory = new();
    private int _newExplorerLocationHistoryIndex = -1;
    private bool _isNavigatingNewExplorerHistory;
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
        UpdateNewExplorerNavigationButtons();
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
        UserSettings.Save();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
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

            Func<Task> initMappingsSafe = async () =>
            {
                try
                {
                    await _applicationView.CUE4Parse.InitAllMappings();
                }
                catch
                {
                    Application.Current.Dispatcher.Invoke(() =>
                        AdonisUI.Controls.MessageBox.Show("マッピングファイルが読み込めませんでした、最新のマッピングファイルをローカルで読み込んでください", "エラー", AdonisUI.Controls.MessageBoxButton.OK, AdonisUI.Controls.MessageBoxImage.Error));
                    throw;
                }
            };

            await Task.WhenAll(
                _applicationView.CUE4Parse.VerifyConsoleVariables(),
                _applicationView.CUE4Parse.VerifyOnDemandArchives(),
                initMappingsSafe(),
                ApplicationViewModel.InitDetex(),
                ApplicationViewModel.InitVgmStream(),
                ApplicationViewModel.InitImGuiSettings(newOrUpdated),
                Task.Run(() =>
                {
                    if (UserSettings.Default.DiscordRpc == EDiscordRpc.Always)
                        _discordHandler.Initialize(_applicationView.GameDisplayName);
                })
            ).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "An error occurred during initialization");
            if (ex.GetBaseException() is ParserException && ex.GetBaseException().Message.Contains("mapping file is missing"))
            {
                AdonisUI.Controls.MessageBox.Show("マッピングファイルが読み込めませんでした、最新のマッピングファイルをローカルで読み込んでください", "エラー", AdonisUI.Controls.MessageBoxButton.OK, AdonisUI.Controls.MessageBoxImage.Error);
            }
            FLogger.Append(ELog.Error, () => FLogger.Text($"初期化中にエラーが発生しました: {ex.Message}", Constants.RED));
        }

        await Dispatcher.InvokeAsync(() =>
        {
            try
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
            }
            catch (Exception ex)
            {
                CheckForMappingError(ex);
                throw;
            }
        });

#if DEBUG
        // await _threadWorkerView.Begin(cancellationToken =>
        //     _applicationView.CUE4Parse.Extract(cancellationToken,
        //         _applicationView.CUE4Parse.Provider["Marvel/Content/Marvel/Wwise/Assets/Events/Music/music_new/event/Entry.uasset"]));
#endif
    }

    private void CheckForMappingError(Exception ex)
    {
        if (ex.GetBaseException() is ParserException && ex.GetBaseException().Message.Contains("mapping file is missing"))
        {
            Application.Current.Dispatcher.Invoke(() =>
                AdonisUI.Controls.MessageBox.Show("マッピングファイルが読み込めませんでした、最新のマッピングファイルをローカルで読み込んでください", "エラー", AdonisUI.Controls.MessageBoxButton.OK, AdonisUI.Controls.MessageBoxImage.Error));
        }
    }
    private void OnReplayAnalysisClick(object sender, RoutedEventArgs e)
    {
        // ReplayAnalysisWindow を表示する
        new Views.ReplayAnalysisWindow().Show();
    }

    private void OnDynamicBackgroundApiClick(object sender, RoutedEventArgs e)
    {
        new Views.DynamicBackgroundApiWindow().Show();
    }

    private async void OnCloudStorageHotfixClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // UIスレッド上でクラウドストレージホットフィックスを実行
            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                await FModel.Features.CloudStorage.CloudStorageHotfix.ExecuteAsync();
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to execute cloud storage hotfix");
            MessageBox.Show(this, $"ホットフィックスの実行に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
        try
        {
            await _applicationView.CUE4Parse.InitAllMappings(true);
        }
        catch
        {
            AdonisUI.Controls.MessageBox.Show("マッピングファイルが読み込めませんでした、最新のマッピングファイルをローカルで読み込んでください", "エラー", AdonisUI.Controls.MessageBoxButton.OK, AdonisUI.Controls.MessageBoxImage.Error);
        }
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

    private async void OnOpenRecentFile(object? parameter)
    {
        if (parameter is not string filePath || string.IsNullOrEmpty(filePath))
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

    private async void OnBruteForceAesClick(object sender, RoutedEventArgs e)
    {
        if (ShowBetaFeatureWarning())
        {
            if (UserSettings.Default.BruteForceAesMode == EBruteForceAesMode.Gpu)
                await BruteForceAesGpuFeature.ExecuteAsync();
            else
                await BruteForceAesFeature.ExecuteAsync();
        }
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
        System.Text.RegularExpressions.Regex regex = null;
        NewExplorerSearchBox.ClearValue(Control.BackgroundProperty);

        if (filterText.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                regex = new System.Text.RegularExpressions.Regex(filterText.Substring(6), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                NewExplorerSearchBox.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(50, 100, 149, 237));
            }
            catch
            {
                NewExplorerSearchBox.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(50, 255, 0, 0));
            }
        }

        var filters = filterText.Trim().Split(' ');
        var hasTextFilter = !string.IsNullOrWhiteSpace(filterText);

        var selectedIndex = NewExplorerClassFilter.SelectedIndex;
        var selectedItem = NewExplorerClassFilter.SelectedItem as ComboBoxItem;
        var hasClassFilter = selectedIndex > 0;
        var selectedClass = hasClassFilter ? selectedItem?.Content?.ToString() : null;

        bool FilterAsset(object o)
        {
            if (o is not GameFile file) return false;

            // Text Filter
            if (hasTextFilter)
            {
                if (regex != null)
                {
                    if (!regex.IsMatch(file.Name))
                        return false;
                }
                else if (filterText.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                else if (!filters.All(x => file.Name.Contains(x, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
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
                if (o is not FModel.ViewModels.TreeItem item) return false;

                if (regex != null) return regex.IsMatch(item.Header);
                if (filterText.StartsWith("regex:", StringComparison.OrdinalIgnoreCase)) return false;
                return filters.All(x => item.Header.Contains(x, StringComparison.OrdinalIgnoreCase));
            };
            rootVm.FoldersView.Refresh();
        }
        else if (dataContext is FModel.ViewModels.TreeItem treeItem)
        {
            treeItem.FoldersView.Filter = o =>
            {
                if (!hasTextFilter) return true;
                if (o is not FModel.ViewModels.TreeItem item) return false;

                if (regex != null) return regex.IsMatch(item.Header);
                if (filterText.StartsWith("regex:", StringComparison.OrdinalIgnoreCase)) return false;
                return filters.All(x => item.Header.Contains(x, StringComparison.OrdinalIgnoreCase));
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

    private void OnNewExplorerGoParentFolderClick(object sender, RoutedEventArgs e)
    {
        var currentFolder = NewExplorerGrid.DataContext as TreeItem ?? AssetsFolderName.SelectedItem as TreeItem;
        if (currentFolder == null)
        {
            UpdateNewExplorerNavigationButtons();
            return;
        }

        var lastSlash = currentFolder.PathAtThisPoint.LastIndexOf('/');
        if (lastSlash <= 0)
        {
            currentFolder.IsSelected = false;
            UpdateNewExplorerNavigationButtons();
            return;
        }

        var parentPath = currentFolder.PathAtThisPoint[..lastSlash];
        if (!TrySelectFolderByPath(parentPath))
        {
            FLogger.Append(ELog.Warning, () => FLogger.Text($"Parent directory not found: {parentPath}", Constants.YELLOW));
        }
    }

    private void OnNewExplorerGoPreviousFileClick(object sender, RoutedEventArgs e)
    {
        if (_newExplorerLocationHistoryIndex <= 0)
        {
            UpdateNewExplorerNavigationButtons();
            return;
        }

        var oldIndex = _newExplorerLocationHistoryIndex;
        _newExplorerLocationHistoryIndex--;
        var previousLocationPath = _newExplorerLocationHistory[_newExplorerLocationHistoryIndex];

        bool moved;
        try
        {
            _isNavigatingNewExplorerHistory = true;
            moved = TrySelectFolderByPath(previousLocationPath);
        }
        finally
        {
            _isNavigatingNewExplorerHistory = false;
        }

        if (!moved)
        {
            _newExplorerLocationHistoryIndex = oldIndex;
            FLogger.Append(ELog.Warning, () => FLogger.Text($"Failed to move to previous location: {previousLocationPath}", Constants.YELLOW));
            UpdateNewExplorerNavigationButtons();
            return;
        }

        UpdateNewExplorerNavigationButtons();
    }

    private void OnNewExplorerGoNextFileClick(object sender, RoutedEventArgs e)
    {
        if (_newExplorerLocationHistoryIndex >= _newExplorerLocationHistory.Count - 1)
        {
            UpdateNewExplorerNavigationButtons();
            return;
        }

        var oldIndex = _newExplorerLocationHistoryIndex;
        _newExplorerLocationHistoryIndex++;
        var nextLocationPath = _newExplorerLocationHistory[_newExplorerLocationHistoryIndex];

        bool moved;
        try
        {
            _isNavigatingNewExplorerHistory = true;
            moved = TrySelectFolderByPath(nextLocationPath);
        }
        finally
        {
            _isNavigatingNewExplorerHistory = false;
        }

        if (!moved)
        {
            _newExplorerLocationHistoryIndex = oldIndex;
            FLogger.Append(ELog.Warning, () => FLogger.Text($"Failed to move to next location: {nextLocationPath}", Constants.YELLOW));
            UpdateNewExplorerNavigationButtons();
            return;
        }

        UpdateNewExplorerNavigationButtons();
    }

    private void OnNewExplorerPathBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        NavigateToPathFromAddressBar();
        e.Handled = true;
    }

    private void OnNewExplorerGoPathClick(object sender, RoutedEventArgs e)
    {
        NavigateToPathFromAddressBar();
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

        if (item.DataContext is not GameFile selectedFile)
            return;

        OpenNewExplorerFile(selectedFile, closeExplorer: true);
    }

    private void OpenNewExplorerFile(GameFile file, bool closeExplorer)
    {
        if (DataContext is not ApplicationViewModel vm)
            return;

        if (!NewExplorerFilesListBox.SelectedItems.Contains(file))
            NewExplorerFilesListBox.SelectedItem = file;

        var parameters = new object[] { "Assets_Extract_New_Tab", NewExplorerFilesListBox.SelectedItems };
        if (vm.RightClickMenuCommand.CanExecute(parameters))
        {
            vm.RightClickMenuCommand.Execute(parameters);
        }

        if (closeExplorer)
            NewExplorerMenuItem.IsChecked = false;
    }

    private void TrackNewExplorerLocation(TreeItem selectedFolder)
    {
        if (selectedFolder == null || string.IsNullOrEmpty(selectedFolder.PathAtThisPoint) || _isNavigatingNewExplorerHistory)
            return;

        var locationPath = selectedFolder.PathAtThisPoint;
        if (_newExplorerLocationHistoryIndex >= 0 &&
            _newExplorerLocationHistoryIndex < _newExplorerLocationHistory.Count &&
            string.Equals(_newExplorerLocationHistory[_newExplorerLocationHistoryIndex], locationPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_newExplorerLocationHistoryIndex < _newExplorerLocationHistory.Count - 1)
        {
            _newExplorerLocationHistory.RemoveRange(_newExplorerLocationHistoryIndex + 1, _newExplorerLocationHistory.Count - (_newExplorerLocationHistoryIndex + 1));
        }

        _newExplorerLocationHistory.Add(locationPath);
        _newExplorerLocationHistoryIndex = _newExplorerLocationHistory.Count - 1;
        UpdateNewExplorerNavigationButtons();
        UpdateNewExplorerPathBox(selectedFolder);
    }

    private void NavigateToPathFromAddressBar()
    {
        if (NewExplorerPathBox == null)
            return;

        var rawInput = NewExplorerPathBox.Text?.Trim();
        if (string.IsNullOrEmpty(rawInput))
            return;

        var normalizedInput = rawInput.Replace('\\', '/').Trim();
        if (string.IsNullOrEmpty(normalizedInput))
            return;

        if (TrySelectFolderByPath(normalizedInput.Trim('/')))
            return;

        if (TryResolveAssetPathnameToFile(normalizedInput, out var gameFile))
        {
            if (TrySelectFolderByPath(GetFolderPathFromFile(gameFile.Path)))
            {
                NewExplorerFilesListBox.SelectedItem = gameFile;
                return;
            }
        }

        SearchAllLoadedFilesFromAddressBar(normalizedInput);
    }

    private bool TryResolveAssetPathnameToFile(string input, out GameFile file)
    {
        file = null;
        if (string.IsNullOrWhiteSpace(input) || _applicationView.CUE4Parse.Provider == null)
            return false;

        var candidate = input.Trim();

        var quoteStart = candidate.IndexOf('\'');
        var quoteEnd = candidate.LastIndexOf('\'');
        if (quoteStart >= 0 && quoteEnd > quoteStart)
            candidate = candidate.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);

        candidate = candidate.Trim();
        if (!candidate.StartsWith("/", StringComparison.Ordinal))
            return false;

        var packagePath = candidate;
        var objectDotIndex = packagePath.LastIndexOf('.');
        var lastSlashIndex = packagePath.LastIndexOf('/');
        if (objectDotIndex > lastSlashIndex)
            packagePath = packagePath.Substring(0, objectDotIndex);

        var fixedPath = _applicationView.CUE4Parse.Provider.FixPath(packagePath);
        if (_applicationView.CUE4Parse.Provider.TryGetGameFile(fixedPath, out var fixedFile))
        {
            file = fixedFile;
            return true;
        }

        if (!fixedPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
            _applicationView.CUE4Parse.Provider.TryGetGameFile($"{fixedPath}.uasset", out var fixedAssetFile))
        {
            file = fixedAssetFile;
            return true;
        }

        return false;
    }

    private string GetFolderPathFromFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return string.Empty;

        var normalizedPath = filePath.Replace('\\', '/');
        var lastSlash = normalizedPath.LastIndexOf('/');
        if (lastSlash <= 0)
            return string.Empty;

        return normalizedPath.Substring(0, lastSlash);
    }

    private void SearchAllLoadedFilesFromAddressBar(string query)
    {
        var provider = _applicationView.CUE4Parse.Provider;
        if (provider?.Files == null || provider.Files.Count == 0)
            return;

        var trimmedQuery = query.Trim();
        if (string.IsNullOrEmpty(trimmedQuery))
            return;

        var files = provider.Files.Values
            .Where(f => f != null &&
                        (f.Name.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase) ||
                         f.Path.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase) ||
                         f.PathWithoutExtension.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(f => f.Path)
            .ToList();

        if (files.Count == 0)
        {
            FLogger.Append(ELog.Warning, () => FLogger.Text($"No assets found matching: {trimmedQuery}", Constants.YELLOW));
            return;
        }

        if (AssetsFolderName.SelectedItem is TreeItem selected)
            selected.IsSelected = false;

        _applicationView.CUE4Parse.AssetsFolder.Folders?.Clear();
        _applicationView.CUE4Parse.AssetsFolder.BulkPopulate(files);
        NewExplorerMenuItem.IsChecked = true;

        FLogger.Append(ELog.Information, () => FLogger.Text($"Found {files.Count} items matching '{trimmedQuery}'", Constants.WHITE));
    }

    private void UpdateNewExplorerPathBox(TreeItem selectedFolder = null)
    {
        if (NewExplorerPathBox == null)
            return;

        var currentFolder = selectedFolder ?? NewExplorerGrid?.DataContext as TreeItem ?? AssetsFolderName?.SelectedItem as TreeItem;
        var path = currentFolder?.PathAtThisPoint ?? string.Empty;

        if (!string.Equals(NewExplorerPathBox.Text, path, StringComparison.Ordinal))
            NewExplorerPathBox.Text = path;
    }

    private void UpdateNewExplorerNavigationButtons()
    {
        if (NewExplorerBackButton != null)
            NewExplorerBackButton.IsEnabled = _newExplorerLocationHistoryIndex > 0;

        if (NewExplorerForwardButton != null)
            NewExplorerForwardButton.IsEnabled = _newExplorerLocationHistoryIndex >= 0 && _newExplorerLocationHistoryIndex < _newExplorerLocationHistory.Count - 1;

        var currentFolder = NewExplorerGrid?.DataContext as TreeItem ?? AssetsFolderName?.SelectedItem as TreeItem;
        if (NewExplorerUpButton != null)
            NewExplorerUpButton.IsEnabled = currentFolder != null;

        UpdateNewExplorerPathBox(currentFolder);
    }

    private bool TrySelectFolderByPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        var currentFolders = _applicationView.CUE4Parse.AssetsFolder.Folders;
        TreeItem targetItem = null;
        var pathItems = new List<TreeItem>();

        foreach (var part in parts)
        {
            if (currentFolders == null)
                return false;

            targetItem = currentFolders.FirstOrDefault(x => x.Header.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (targetItem == null)
                return false;

            pathItems.Add(targetItem);
            currentFolders = targetItem.Folders;
        }

        foreach (var item in pathItems)
            item.IsExpanded = true;

        targetItem.IsSelected = true;
        AssetsFolderName.Focus();
        return true;
    }

    // NEW エクスプローラー用：右クリックメニューから選択されたファイルを取得
    private IList<GameFile> GetSelectedFilesFromNewExplorerContextMenu()
    {
        if (NewExplorerFilesListBox?.SelectedItems != null)
        {
            return NewExplorerFilesListBox.SelectedItems.Cast<GameFile>().ToList();
        }
        return null;
    }

    // NEW エクスプローラー用：新しいタブで開く
    private void OnNewExplorerOpenNewTabClick(object sender, RoutedEventArgs e)
    {
        var selectedFiles = GetSelectedFilesFromNewExplorerContextMenu();
        if (selectedFiles == null || selectedFiles.Count == 0)
            return;

        if (_applicationView.RightClickMenuCommand.CanExecute(new object[] { "Assets_Extract_New_Tab", selectedFiles }))
        {
            _applicationView.RightClickMenuCommand.Execute(new object[] { "Assets_Extract_New_Tab", selectedFiles });
        }
    }

    // NEW エクスプローラー用：メタデータを表示
    private void OnNewExplorerShowMetadataClick(object sender, RoutedEventArgs e)
    {
        var selectedFiles = GetSelectedFilesFromNewExplorerContextMenu();
        if (selectedFiles == null || selectedFiles.Count == 0)
            return;

        if (_applicationView.RightClickMenuCommand.CanExecute(new object[] { "Assets_Show_Metadata", selectedFiles }))
        {
            _applicationView.RightClickMenuCommand.Execute(new object[] { "Assets_Show_Metadata", selectedFiles });
        }
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

    private void OnNewExplorerOpenGraphViewerClick(object sender, RoutedEventArgs e)
    {
        var selectedFiles = GetSelectedFilesFromNewExplorerContextMenu();
        if (selectedFiles == null || selectedFiles.Count == 0)
            return;

        _applicationView.CUE4Parse.ShowAnimGraph(selectedFiles[0]);
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
    private void OnTestEditAssetClick(object sender, RoutedEventArgs e)
    {
        // 1. ファイルリストから選択中のアセットを取得
        if (AssetsListName.SelectedItem is not GameFile selectedAsset)
        {
            MessageBox.Show("Please select an asset from the file list first.", "No Asset Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 2. CUE4Parseからアセットデータをバイト配列として取得
        var provider = _applicationView.CUE4Parse.Provider;
        // ISavable, CanSaveは存在しないため、providerがnullかどうかのみ判定
        if (provider is null)
        {
            MessageBox.Show("The file provider is not ready.", "Provider Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ★★★ 編集ダイアログを表示 ★★★
        var dialog = new PropertyEditDialog();
        if (dialog.ShowDialog() != true)
        {
            return; // ユーザーがキャンセルした
        }

        // 一時ディレクトリを作成
        var tempDir = Path.Combine(Path.GetTempPath(), $"FModel_Edit_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // 3. アセットを一時ファイルとしてディスクに書き出す (UAssetAPIはファイルパスから読み込むため)
            if (!provider.TryGetGameFile(selectedAsset.Path, out var selectedGameFile))
            {
                MessageBox.Show("Failed to extract asset data.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var data = selectedGameFile.Read();
            if (data == null || data.Length == 0)
            {
                MessageBox.Show("Failed to extract asset data.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var tempAssetPath = Path.Combine(tempDir, selectedAsset.Name);
            File.WriteAllBytes(tempAssetPath, data);

            // .uexp ファイルも存在する場合は一緒に書き出す
            var uexpPath = Path.ChangeExtension(selectedAsset.Path, ".uexp");
            // FileExistsは存在しないため、TryGetGameFileで存在確認
            if (provider.TryGetGameFile(uexpPath, out var uexpGameFile))
            {
                var uexpData = uexpGameFile.Read();
                var tempUexpPath = Path.Combine(tempDir, Path.GetFileName(uexpPath));
                File.WriteAllBytes(tempUexpPath, uexpData);
            }

            // 4. 編集後のアセットの保存先をユーザーに選択させる
            var saveFileDialog = new SaveFileDialog
            {
                FileName = $"{Path.GetFileNameWithoutExtension(selectedAsset.Name)}_Edited.uasset",
                Filter = "Unreal Asset (*.uasset)|*.uasset",
                Title = "Save Edited Asset As"
            };

            if (saveFileDialog.ShowDialog() != true)
            {
                return; // ユーザーがキャンセルした
            }
            var destinationPath = saveFileDialog.FileName;

            // 5. AssetEditor.EditAndSave を使ってアセットを編集・保存
            // UE4VersionではなくEngineVersion（UAssetAPI.EngineVersion）を使用
            var engineVersion = EngineVersion.VER_UE4_27; // 自動取得ができないため固定値を指定

            AssetEditor.EditAndSave(tempAssetPath, destinationPath, engineVersion, asset =>
            {
                // --- ここからがアセットの編集ロジック ---
                Log.Information($"Attempting to edit asset: {selectedAsset.Name}");
                bool modified = false;

                // DataTableアセットの場合、最初の行を対象とする
                if (asset.Exports.Count > 0 && asset.Exports[0] is DataTableExport dataTableExport && dataTableExport.Table.Data.Count > 0)
                {
                    var firstRow = dataTableExport.Table.Data[0];
                    var property = firstRow.Value.FirstOrDefault(x => x.Name.ToString() == dialog.PropertyName);
                    if (property != null)
                    {
                        modified = TrySetPropertyValue(property, dialog.PropertyType, dialog.PropertyValue, dialog.EnumType, asset);
                    }
                    else
                    {
                        Log.Warning($"Property '{dialog.PropertyName}' not found in the first row of the DataTable.");
                    }
                }
                // 通常のBlueprintアセットなどの場合
                else if (asset.Exports.Count > 0 && asset.Exports[0] is NormalExport normalExport)
                {
                    var property = normalExport.Data.FirstOrDefault(x => x.Name.ToString() == dialog.PropertyName);
                    if (property != null)
                    {
                        modified = TrySetPropertyValue(property, dialog.PropertyType, dialog.PropertyValue, dialog.EnumType, asset);
                    }
                    else
                    {
                        Log.Warning($"Property '{dialog.PropertyName}' not found in the export.");
                    }
                }

                if (!modified)
                {
                    Log.Warning("Failed to modify the property. The asset will be saved without changes.");
                }
                // --- 編集ロジックここまで ---
            });

            MessageBox.Show($"Asset successfully edited and saved to:\n{destinationPath}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to edit and save asset.");
            MessageBox.Show($"An error occurred while editing the asset:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // 6. 一時ファイルをクリーンアップ
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    private bool TrySetPropertyValue(PropertyData property, string targetType, string stringValue, string enumType, UAsset asset)
    {
        try
        {
            if (property.GetType().Name != targetType)
            {
                Log.Warning($"Property '{property.Name}' was found, but its type '{property.GetType().Name}' did not match the selected type '{targetType}'.");
                return false;
            }

            switch (targetType)
            {
                case "TextPropertyData":
                    if (property is TextPropertyData textProp) { textProp.Value = new UAssetAPI.UnrealTypes.FString(stringValue); }
                    break;
                case "IntPropertyData":
                    if (property is IntPropertyData intProp && int.TryParse(stringValue, out var intVal)) { intProp.Value = intVal; }
                    break;
                case "FloatPropertyData":
                    if (property is FloatPropertyData floatProp && float.TryParse(stringValue, out var floatVal)) { floatProp.Value = floatVal; }
                    break;
                case "NamePropertyData":
                    if (property is NamePropertyData nameProp) { nameProp.Value = FName.FromString(asset, stringValue); }
                    break;
                case "BoolPropertyData":
                    if (property is BoolPropertyData boolProp && bool.TryParse(stringValue, out var boolVal)) { boolProp.Value = boolVal; }
                    break;
                case "BytePropertyData":
                    if (property is BytePropertyData byteProp)
                    {
                        if (!string.IsNullOrWhiteSpace(enumType))
                        {
                            byteProp.EnumType = FName.FromString(asset, enumType);
                            // byteProp.Value = FName.FromString(asset, stringValue);
                        }
                        else if (byte.TryParse(stringValue, out var byteVal))
                        {
                            byteProp.ByteType = BytePropertyType.Byte;
                            var enumValueField = typeof(BytePropertyData).GetField("EnumValue", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (enumValueField != null)
                            {
                                enumValueField.SetValue(byteProp, byteVal);
                                byteProp.Value = byteVal;
                            }
                            else { return false; }
                        }
                        else { return false; }
                    }
                    break;
                default:
                    return false;
            }

            Log.Information($"Successfully set {targetType.Replace("Data", "")} '{property.Name}' to '{stringValue}'.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Error setting property value for '{property.Name}'.");
            return false;
        }
    }
}
