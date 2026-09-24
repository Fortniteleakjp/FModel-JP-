using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using AdonisUI.Controls;
using CUE4Parse.FileProvider.Objects;
using FModel.Services;
using FModel.Views.Resources.Controls;

namespace FModel.Views;

/// <summary>
/// lists the Wwise events and packages that use a sound, double click opens them
/// </summary>
public partial class AudioReverseLookupWindow : AdonisWindow
{
    private readonly AudioReverseLookupResult _result;
    private readonly ICollectionView _view;

    public AudioReverseLookupWindow(AudioReverseLookupResult result)
    {
        _result = result;
        InitializeComponent();

        Title = $"{Text("UI_AudioLookup_Title", "Sound Usage")} - {result.TargetName}";
        TargetTitle.Text = result.TargetName;

        var details = new System.Collections.Generic.List<string>();
        if (result.SourceAsset != null)
            details.Add(string.Format(Text("UI_AudioLookup_Source", "Source: {0}"), result.SourceAsset.Path));
        if (result.MediaIds.Count > 0)
            details.Add(string.Format(Text("UI_AudioLookup_MediaIds", "Wwise media: {0}"), string.Join(", ", result.MediaIds)));
        TargetDetails.Text = string.Join(Environment.NewLine, details);

        var usages = result.Usages
            .OrderByDescending(u => u.IsWwiseEvent)
            .ThenBy(u => u.Depth)
            .ThenBy(u => u.ClassName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _view = CollectionViewSource.GetDefaultView(usages);
        UsageGrid.ItemsSource = _view;
        EmptyText.Visibility = usages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailColumn.Visibility = usages.Any(u => u.Detail != null) ? Visibility.Visible : Visibility.Collapsed;

        var notes = result.Notes.Select(n => Text($"UI_AudioLookupNote_{n}", n.ToString())).ToList();
        if (!result.Notes.Contains(EAudioLookupNote.NotIoStore) && result.SourceAsset != null)
            notes.Add(Text("UI_AudioLookup_HardRefsOnly", "Only hard references are listed, soft references are not."));
        NoteList.ItemsSource = notes;

        UpdateStatus();
    }

    /// <summary>
    /// runs the lookup, call from a worker thread
    /// </summary>
    public static void RunAndShow(GameFile sourceAsset, string audioName, CancellationToken cancellationToken)
    {
        var result = AudioReverseLookup.Run(ApplicationService.ApplicationView.CUE4Parse, sourceAsset, audioName, cancellationToken);
        FLogger.Append(ELog.Information, () =>
            FLogger.Text($"Found {result.Usages.Count} assets using {result.TargetName}", Constants.WHITE, true));
        Application.Current.Dispatcher.Invoke(() => new AudioReverseLookupWindow(result).Show());
    }

    private void UpdateStatus()
    {
        var events = _result.Usages.Count(u => u.IsWwiseEvent);
        var status = _result.Notes.Contains(EAudioLookupNote.NoWwise)
            ? string.Format(Text("UI_AudioLookup_SummaryPackages", "{0} packages"), _result.Usages.Count)
            : string.Format(Text("UI_AudioLookup_Summary", "{0} Wwise events, {1} packages"), events, _result.Usages.Count - events);
        if (_result.IndexedEventCount > 0)
            status += " " + string.Format(Text("UI_AudioLookup_Indexed", "({0} events indexed)"), _result.IndexedEventCount);

        var visible = _view.Cast<object>().Count();
        if (visible != _result.Usages.Count)
            status += $" - {visible} / {_result.Usages.Count}";
        Status.Text = status;
    }

    private void OnFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        var tokens = FilterBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _view.Filter = tokens.Length == 0
            ? null
            : o => o is AudioUsage usage && tokens.All(t =>
                usage.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                usage.ClassName.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                usage.Path.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                (usage.Via?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false));
        UpdateStatus();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OpenButton.IsEnabled = UsageGrid.SelectedItem is AudioUsage;
    }

    private void OnUsageDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // ignore double clicks on the headers or the scroll bar
        if (e.OriginalSource is not DependencyObject source || ItemsControl.ContainerFromElement(UsageGrid, source) is not DataGridRow)
            return;

        OpenSelected();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        var files = UsageGrid.SelectedItems.OfType<AudioUsage>().Select(u => u.File).ToList();
        if (files.Count == 0) return;

        var cue4Parse = ApplicationService.ApplicationView.CUE4Parse;
        ApplicationService.ThreadWorkerView.Begin(cancellationToken =>
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cue4Parse.Extract(cancellationToken, file, true);
            }
        });
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        var paths = UsageGrid.SelectedItems.OfType<AudioUsage>().Select(u => u.Path).ToList();
        if (paths.Count > 0)
            Clipboard.SetText(string.Join(Environment.NewLine, paths));
    }

    private string Text(string key, string fallback) => TryFindResource(key) as string ?? fallback;
}
