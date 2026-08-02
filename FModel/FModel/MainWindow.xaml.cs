using System;
using System.Collections.Generic; // List<> 繧剃ｽｿ逕ｨ縺吶ｋ縺溘ａ縺ｫ霑ｽ蜉
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
using FModel.Framework; // RelayCommand 繧剃ｽｿ逕ｨ縺吶ｋ縺溘ａ縺ｮ繧・▽
using System.Collections.Specialized; // NotifyCollectionChangedEventArgs 繧剃ｽｿ逕ｨ縺吶ｋ縺溘ａ縺ｮ繧・▽
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

        // 繝・け繧ｹ繝√Ε繝励Ξ繝薙Η繝ｼ險ｭ螳壹・螟画峩繧堤屮隕・
        UserSettings.Default.PropertyChanged += OnUserSettingsPropertyChanged;

        FLogger.Logger = LogRtbName;
        YesWeCats = this;

        // 髢ｲ隕ｧ螻･豁ｴ繝｡繝九Η繝ｼ縺ｮ蛻晄悄蛹悶→譖ｴ譁ｰ
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

    // 縺薙・繝舌・繧ｸ繝ｧ繝ｳ縺ｮ蛻晏屓襍ｷ蜍墓凾縺ｮ縺ｿ縲√お繧ｯ繧ｹ繝昴・繝域婿蠑擾ｼ域立/譁ｰ繝代う繝励Λ繧､繝ｳ・峨ｒ驕ｸ謚槭＆縺帙ｋ縲・
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 縺薙・繝舌・繧ｸ繝ｧ繝ｳ縺ｮ蛻晏屓襍ｷ蜍墓凾縺ｮ縺ｿ縲√お繧ｯ繧ｹ繝昴・繝域婿蠑擾ｼ域立/譁ｰ繝代う繝励Λ繧､繝ｳ・峨・驕ｸ謚槭ｒ菫・☆縲・
            UserSettings.Default.ExportPipeline = EExportPipeline.New;

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
                        AdonisUI.Controls.MessageBox.Show("繝槭ャ繝斐Φ繧ｰ繝輔ぃ繧､繝ｫ縺瑚ｪｭ縺ｿ霎ｼ繧√∪縺帙ｓ縺ｧ縺励◆縲∵怙譁ｰ縺ｮ繝槭ャ繝斐Φ繧ｰ繝輔ぃ繧､繝ｫ繧偵Ο繝ｼ繧ｫ繝ｫ縺ｧ隱ｭ縺ｿ霎ｼ繧薙〒縺上□縺輔＞", "繧ｨ繝ｩ繝ｼ", AdonisUI.Controls.MessageBoxButton.OK, AdonisUI.Controls.MessageBoxImage.Error));
                    throw;
                }
            };

            await Task.WhenAll(
                _applicationView.CUE4Parse.VerifyConsoleVariables(),
                _applicationView.CUE4Parse.VerifyOnDemandArchives(),
                _applicationView.CUE4Parse.VerifyCloudArchives(),
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
                AdonisUI.Controls.MessageBox.Show("繝槭ャ繝斐Φ繧ｰ繝輔ぃ繧､繝ｫ縺瑚ｪｭ縺ｿ霎ｼ繧√∪縺帙ｓ縺ｧ縺励◆縲∵怙譁ｰ縺ｮ繝槭ャ繝斐Φ繧ｰ繝輔ぃ繧､繝ｫ繧偵Ο繝ｼ繧ｫ繝ｫ縺ｧ隱ｭ縺ｿ霎ｼ繧薙〒縺上□縺輔＞", "繧ｨ繝ｩ繝ｼ", AdonisUI.Controls.MessageBoxButton.OK, AdonisUI.Controls.MessageBoxImage.Error);
            }
            FLogger.Append(ELog.Error, () => FLogger.Text($"蛻晄悄蛹紋ｸｭ縺ｫ繧ｨ繝ｩ繝ｼ縺檎匱逕溘＠縺ｾ縺励◆: {ex.Message}", Constants.RED));
        }

        await Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (UserSettings.Default.RestoreTabsOnStartup && UserSettings.Default.CurrentDir.LastOpenedTabs?.Any() == true)
                {
                    var paths = UserSettings.Default.CurrentDir.LastOpenedTabs;
                    _applicationView.CUE4Parse.TabControl.RemoveAllTabs(); // "譁ｰ縺励＞繧ｿ繝・繧貞炎髯､
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
                AdonisUI.Controls.MessageBox.Show("繝槭ャ繝斐Φ繧ｰ繝輔ぃ繧､繝ｫ縺瑚ｪｭ縺ｿ霎ｼ繧√∪縺帙ｓ縺ｧ縺励◆縲∵怙譁ｰ縺ｮ繝槭ャ繝斐Φ繧ｰ繝輔ぃ繧､繝ｫ繧偵Ο繝ｼ繧ｫ繝ｫ縺ｧ隱ｭ縺ｿ霎ｼ繧薙〒縺上□縺輔＞", "繧ｨ繝ｩ繝ｼ", AdonisUI.Controls.MessageBoxButton.OK, AdonisUI.Controls.MessageBoxImage.Error));
        }
    }







































}


