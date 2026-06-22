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
    private void OnReplayAnalysisClick(object sender, RoutedEventArgs e)
    {
        // ReplayAnalysisWindow を表示する
        new Views.ReplayAnalysisWindow().Show();
    }

    private void OnDynamicBackgroundApiClick(object sender, RoutedEventArgs e)
    {
        new Views.DynamicBackgroundApiWindow().Show();
    }

    private void OnFortniteStatusApiClick(object sender, RoutedEventArgs e)
    {
        new Views.DynamicBackgroundApiWindow(initialTab: 2).Show();
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

    private void OnThemeDarkClick(object sender, RoutedEventArgs e)
    {
        if (UserSettings.Default.UseDarkTheme)
            return;

        UserSettings.Default.UseDarkTheme = true;
        if (Application.Current is App app)
            app.ApplyTheme(true);
    }

    private void OnThemeLightClick(object sender, RoutedEventArgs e)
    {
        if (!UserSettings.Default.UseDarkTheme)
            return;

        UserSettings.Default.UseDarkTheme = false;
        if (Application.Current is App app)
            app.ApplyTheme(false);
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

    private void TabControlName_SelectionChanged_2(object sender, SelectionChangedEventArgs e)
    {

    }
}
