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

        // テクスチャプレビュー設定の変更を監視
        UserSettings.Default.PropertyChanged += OnUserSettingsPropertyChanged;

        FLogger.Logger = LogRtbName;
        YesWeCats = this;

        // 閲覧履歴メニューの初期化と更新
        UpdateRecentFilesMenu();
        UserSettings.Default.RecentFiles.CollectionChanged += RecentFiles_CollectionChanged;
        UpdateNewExplorerNavigationButtons();

        _newExplorerFilterDebounceTimer.Tick += (_, _) =>
        {
            _newExplorerFilterDebounceTimer.Stop();
            ApplyNewExplorerFilter();
        };
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

    // このバージョンの初回起動時のみ、エクスポート方式（旧/新パイプライン）を選択させる。
    private void ShowExportPipelineFirstRunDialog()
    {
        if (UserSettings.Default.HasChosenExportPipeline) return;
        try
        {
            var dialog = new ExportPipelineDialog { Owner = this };
            dialog.ShowDialog();
            UserSettings.Default.ExportPipeline = dialog.SelectedPipeline;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to show export pipeline selection dialog; defaulting to Legacy.");
            UserSettings.Default.ExportPipeline = EExportPipeline.Legacy;
        }
        finally
        {
            UserSettings.Default.HasChosenExportPipeline = true;
            UserSettings.Save();
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // このバージョンの初回起動時のみ、エクスポート方式（旧/新パイプライン）の選択を促す。
            ShowExportPipelineFirstRunDialog();

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







































}
