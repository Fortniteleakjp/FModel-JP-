using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AdonisUI.Controls;
using CUE4Parse.FileProvider.Vfs;
using FModel.Services;
using FModel.ViewModels;
using FModel.Views.Resources.Controls;

namespace FModel.Views;

/// <summary>
/// Interaction logic for GameplayTagBrowserWindow.xaml
/// Lists the gameplay tags a build declares as a hierarchy, the way the editor's tag manager does.
/// </summary>
public partial class GameplayTagBrowserWindow : AdonisWindow
{
    private GameplayTagCollection _collection;
    private CancellationTokenSource _cancellationTokenSource;
    private bool _isScanning;

    public GameplayTagBrowserWindow()
    {
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await ScanAsync();

    private void OnClosing(object sender, CancelEventArgs e) => Cancel();

    private async void OnRescanClick(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (_isScanning) return;

        AbstractVfsFileProvider provider = null;
        try
        {
            provider = ApplicationService.ApplicationView?.CUE4Parse?.Provider;
        }
        catch (Exception)
        {
            // no build loaded yet
        }

        if (provider == null)
        {
            SetStatus(TryFindResource("UI_GameplayTags_NoProvider") as string);
            return;
        }

        _isScanning = true;
        RescanButton.IsEnabled = false;
        TagTree.ItemsSource = null;
        SetStatus(TryFindResource("UI_GameplayTags_Scanning") as string);

        Cancel();
        _cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _cancellationTokenSource.Token;

        try
        {
            var progress = new Progress<string>(path => SetStatus(path));
            _collection = await Task.Run(
                () => GameplayTagCollector.Collect(provider, ((IProgress<string>) progress).Report, cancellationToken),
                cancellationToken).ConfigureAwait(true);

            ApplyFilter();
            SetStatus($"{_collection.TagCount} tags from {_collection.SourceCount} sources");
        }
        catch (OperationCanceledException)
        {
            SetStatus(TryFindResource("UI_GameplayTags_Cancelled") as string);
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message);
            FLogger.Append(ELog.Error, () => FLogger.Text($"Gameplay tag scan failed: {exception.Message}", Constants.WHITE, true));
        }
        finally
        {
            _isScanning = false;
            RescanButton.IsEnabled = true;
        }
    }

    private void OnFilterTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (_collection == null) return;

        var filter = FilterBox.Text;
        if (string.IsNullOrWhiteSpace(filter))
        {
            TagTree.ItemsSource = _collection.Roots;
            return;
        }

        var filtered = new List<GameplayTagNode>();
        foreach (var root in _collection.Roots)
        {
            var kept = root.Filtered(filter);
            if (kept != null) filtered.Add(kept);
        }

        TagTree.ItemsSource = filtered;
        if (filtered.Count == 0)
            SetStatus(TryFindResource("UI_GameplayTags_NoResult") as string);
    }

    private void OnTagSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not GameplayTagNode node)
        {
            SelectedTagBox.Text = string.Empty;
            CommentText.Text = string.Empty;
            SourceList.ItemsSource = null;
            return;
        }

        SelectedTagBox.Text = node.FullTag;
        CommentText.Text = node.Comment;
        SourceList.ItemsSource = node.Sources;
    }

    private void OnCopyTagClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(SelectedTagBox.Text)) return;

        try
        {
            Clipboard.SetText(SelectedTagBox.Text);
        }
        catch (Exception exception)
        {
            FLogger.Append(ELog.Error, () => FLogger.Text($"Could not copy to the clipboard: {exception.Message}", Constants.WHITE, true));
        }
    }

    /// <summary>
    /// Opens the config file or tag table declaring the selected tag in a regular FModel tab.
    /// </summary>
    private void OnSourceDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SourceList.SelectedItem is not string path) return;

        var cue4Parse = ApplicationService.ApplicationView.CUE4Parse;
        if (!cue4Parse.Provider.Files.TryGetValue(path, out var entry))
        {
            SetStatus($"{path} is not part of the loaded build anymore");
            return;
        }

        ApplicationService.ThreadWorkerView.Begin(cancellationToken => cue4Parse.Extract(cancellationToken, entry, true));
    }

    private void Cancel()
    {
        if (_cancellationTokenSource == null) return;

        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
        _cancellationTokenSource = null;
    }

    private void SetStatus(string status) => Status.Text = status ?? string.Empty;
}
