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
    // NEW エクスプローラー ON/OFF切り替え（タブ制御）
    private const string ExplorerTabHeader = "NEW エクスプローラー";
    private FModel.ViewModels.TabItem _explorerTabVm;
    private readonly List<string> _newExplorerLocationHistory = new();

    private readonly DispatcherTimer _newExplorerFilterDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private string _newExplorerLastFilterText = string.Empty;
    private int _newExplorerLastClassFilterIndex = -1;
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
        _newExplorerFilterDebounceTimer.Stop();
        _newExplorerFilterDebounceTimer.Start();
    }

    private void OnNewExplorerClassFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        _newExplorerFilterDebounceTimer.Stop();
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

        var filters = filterText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hasTextFilter = !string.IsNullOrWhiteSpace(filterText);

        var selectedIndex = NewExplorerClassFilter.SelectedIndex;
        var selectedItem = NewExplorerClassFilter.SelectedItem as ComboBoxItem;
        var hasClassFilter = selectedIndex > 0;
        var selectedClass = hasClassFilter ? selectedItem?.Content?.ToString() : null;

        if (string.Equals(_newExplorerLastFilterText, filterText, StringComparison.Ordinal) &&
            _newExplorerLastClassFilterIndex == selectedIndex)
        {
            return;
        }

        _newExplorerLastFilterText = filterText;
        _newExplorerLastClassFilterIndex = selectedIndex;

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

                return TryGetAssetExportType(file, out var exportType) &&
                       string.Equals(exportType, selectedClass, StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }

        dataContext ??= NewExplorerGrid.DataContext;

        if (dataContext is FModel.ViewModels.AssetsFolderViewModel rootVm)
        {
            rootVm.FoldersView.Filter = hasTextFilter ? o =>
            {
                if (o is not FModel.ViewModels.TreeItem item) return false;

                if (regex != null) return regex.IsMatch(item.Header);
                if (filterText.StartsWith("regex:", StringComparison.OrdinalIgnoreCase)) return false;
                return filters.All(x => item.Header.Contains(x, StringComparison.OrdinalIgnoreCase));
            } : null;
            rootVm.FoldersView.Refresh();
        }
        else if (dataContext is FModel.ViewModels.TreeItem treeItem)
        {
            treeItem.FoldersView.Filter = hasTextFilter ? o =>
            {
                if (o is not FModel.ViewModels.TreeItem item) return false;

                if (regex != null) return regex.IsMatch(item.Header);
                if (filterText.StartsWith("regex:", StringComparison.OrdinalIgnoreCase)) return false;
                return filters.All(x => item.Header.Contains(x, StringComparison.OrdinalIgnoreCase));
            } : null;
            treeItem.FoldersView.Refresh();

            treeItem.AssetsList.AssetsView.Filter = (hasTextFilter || hasClassFilter) ? (Predicate<object>)FilterAsset : null;
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

    private void OnCloseNewExplorerClick(object sender, RoutedEventArgs e)
    {
        NewExplorerMenuItem.IsChecked = false;
    }

    private void OnNewExplorerListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        var scrollViewer = GetScrollViewer(listBox);
        if (scrollViewer != null)
        {
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }

    private void OnNewExplorerListKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox listBox) return;
        if (e.Key != Key.Up && e.Key != Key.Down && e.Key != Key.PageUp && e.Key != Key.PageDown) return;

        int itemCount = listBox.Items.Count;
        if (itemCount == 0) return;

        var wrapPanel = FindVisualChild<WrapPanel>(listBox);
        if (wrapPanel == null) return;

        int selectedIndex = listBox.SelectedIndex < 0 ? 0 : listBox.SelectedIndex;

        // WrapPanel の1行あたりアイテム数を算出
        int itemsPerRow = 1;
        if (listBox.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement firstItem && firstItem.ActualWidth > 0)
        {
            double itemW = firstItem.ActualWidth + firstItem.Margin.Left + firstItem.Margin.Right;
            if (itemW > 0)
                itemsPerRow = Math.Max(1, (int)(wrapPanel.ActualWidth / itemW));
        }

        int delta = e.Key switch
        {
            Key.Up       => -itemsPerRow,
            Key.Down     =>  itemsPerRow,
            Key.PageUp   => -itemsPerRow * 3,
            Key.PageDown =>  itemsPerRow * 3,
            _            => 0
        };

        int newIndex = Math.Max(0, Math.Min(selectedIndex + delta, itemCount - 1));
        if (newIndex == selectedIndex) return;

        listBox.SelectedIndex = newIndex;
        listBox.ScrollIntoView(listBox.Items[newIndex]);
        e.Handled = true;
    }

    private static ScrollViewer GetScrollViewer(DependencyObject depObj)
    {
        if (depObj is ScrollViewer sv) return sv;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
        {
            var child = VisualTreeHelper.GetChild(depObj, i);
            var result = GetScrollViewer(child);
            if (result != null) return result;
        }
        return null;
    }

    private static T FindVisualChild<T>(DependencyObject depObj) where T : DependencyObject
    {
        if (depObj is T match) return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
        {
            var result = FindVisualChild<T>(VisualTreeHelper.GetChild(depObj, i));
            if (result != null) return result;
        }
        return null;
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

    private void OnNewExplorerOpenGraphViewerClick(object sender, RoutedEventArgs e)
    {
        var selectedFiles = GetSelectedFilesFromNewExplorerContextMenu();
        if (selectedFiles == null || selectedFiles.Count == 0)
            return;

        _applicationView.CUE4Parse.ShowAnimGraph(selectedFiles[0]);
    }
}
