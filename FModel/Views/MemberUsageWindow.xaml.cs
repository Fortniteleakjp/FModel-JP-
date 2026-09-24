using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using AdonisUI.Controls;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Objects.UObject;
using FModel.Services;
using FModel.ViewModels;
using FModel.Views.Resources.Controls;
using Serilog;

namespace FModel.Views;

/// <summary>
/// a function or variable of the blueprint the window was opened from, to search without typing its path
/// </summary>
public sealed class MemberChoice
{
    public required string Name { get; init; }
    public required string Query { get; init; }
    public required EMemberKind Kind { get; init; }
    public string Display { get; set; }
}

public sealed class MemberUsageRow(MemberUsage usage, string kindText)
{
    public MemberUsage Usage { get; } = usage;
    public string KindText { get; } = kindText;
    public string Name => Usage.Name;
    public string UsedIn => Usage.UsedIn;
    public string Member => Usage.Member;
    public int Count => Usage.Count;
    public string Path => Usage.Path;
}

/// <summary>
/// lists the blueprints calling a function or reading / writing a variable, double click opens the graph at the use
/// </summary>
public partial class MemberUsageWindow : AdonisWindow
{
    private CancellationTokenSource _cancellation;
    private MemberUsageResult _result;
    private ICollectionView _view;
    private readonly bool _searchOnLoad;

    /// <param name="query">text of a <see cref="MemberUsageQuery"/></param>
    /// <param name="members">functions and variables of a blueprint to pick from</param>
    /// <param name="searchOnLoad">start searching <paramref name="query"/> as soon as the window shows</param>
    public MemberUsageWindow(string query = null, EMemberKind kind = EMemberKind.Any, IReadOnlyList<MemberChoice> members = null,
        string owner = null, bool searchOnLoad = false)
    {
        InitializeComponent();

        if (owner != null) Title = $"{Text("UI_MemberUsage_Title", "Function & Variable Usage")} - {owner}";
        QueryBox.Text = query ?? string.Empty;
        KindBox.SelectedIndex = (int) kind;
        _searchOnLoad = searchOnLoad && !string.IsNullOrWhiteSpace(query);

        if (members is { Count: > 0 })
        {
            foreach (var member in members)
                member.Display = $"{Text(member.Kind == EMemberKind.Function ? "UI_MemberUsage_KindFunction" : "UI_MemberUsage_KindVariable", member.Kind.ToString())}    {member.Name}";
            MemberPicker.ItemsSource = members;
            MemberPanel.Visibility = Visibility.Visible;
        }

        Status.Text = Text("UI_MemberUsage_Ready", "Type a function or variable name and press Search.");
        ClearCacheButton.IsEnabled = false;
        Loaded += OnLoaded;
        Closing += (_, _) => _cancellation?.Cancel();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
        if (_searchOnLoad) await SearchAsync();
        else await UpdateCacheTextAsync();
    }

    /// <summary>
    /// how old the index of the blueprint packages is, it makes every search but the first one of the day fast
    /// </summary>
    private async Task UpdateCacheTextAsync()
    {
        MemberUsageCacheInfo info = null;
        try
        {
            var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
            if (provider.Files.Count > 0) info = await Task.Run(() => MemberUsageLookup.GetCacheInfo(provider));
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to read the blueprint package index");
        }

        CacheText.Text = info == null
            ? Text("UI_MemberUsage_CacheNone", "No cache, the next search over the whole game builds it")
            : string.Format(Text("UI_MemberUsage_CacheInfo", "Cache: {0:N0} blueprint packages, built {1:g}, until {2:g}"), info.PackageCount, info.CreatedAt, info.ExpiresAt);
        ClearCacheButton.IsEnabled = info != null && _cancellation == null;
    }

    private async void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        if (_cancellation != null) return;

        MemberUsageLookup.ClearCache();
        FLogger.Append(ELog.Information, () => FLogger.Text("Function & variable usage cache cleared", Constants.WHITE, true));
        await UpdateCacheTextAsync();
    }

    /// <summary>
    /// the functions and variables a blueprint declares, call from a worker thread
    /// </summary>
    public static (string Owner, List<MemberChoice> Members) ReadMembers(GameFile entry)
    {
        var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
        // the editor only twin carries no class
        if (entry.Path.EndsWith(".o.uasset", StringComparison.OrdinalIgnoreCase) &&
            provider.Files.TryGetValue($"{entry.PathWithoutExtension[..^2]}.uasset", out var runtimeTwin))
            entry = runtimeTwin;

        var blueprint = BlueprintGraphBuilder.FindClass(provider.LoadPackage(entry));
        if (blueprint == null) return (null, null);

        var package = blueprint.Owner?.Name;
        var members = new List<MemberChoice>();
        foreach (var name in (blueprint.FuncMap?.Keys ?? Enumerable.Empty<FName>()).Select(k => k.Text)
                     .Where(n => !n.StartsWith("ExecuteUbergraph", StringComparison.Ordinal))
                     .Order(StringComparer.OrdinalIgnoreCase))
            members.Add(new MemberChoice { Name = name, Query = MemberUsageLookup.QueryFor(package, blueprint.Name, name), Kind = EMemberKind.Function });

        foreach (var name in (blueprint.ChildProperties ?? []).OfType<FProperty>().Select(p => p.Name.Text)
                     .Where(n => n != "UberGraphFrame")
                     .Order(StringComparer.OrdinalIgnoreCase))
            members.Add(new MemberChoice { Name = name, Query = MemberUsageLookup.QueryFor(package, blueprint.Name, name), Kind = EMemberKind.Variable });

        return (blueprint.Name, members);
    }

    private void OnMemberPicked(object sender, SelectionChangedEventArgs e)
    {
        if (MemberPicker.SelectedItem is not MemberChoice member) return;
        QueryBox.Text = member.Query;
        KindBox.SelectedIndex = (int) member.Kind;
    }

    private async void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_cancellation == null) await SearchAsync();
    }

    private async void OnSearchClick(object sender, RoutedEventArgs e)
    {
        if (_cancellation != null)
        {
            _cancellation.Cancel();
            return;
        }

        await SearchAsync();
    }

    private async Task SearchAsync()
    {
        var kind = (EMemberKind) Math.Max(0, KindBox.SelectedIndex);
        if (!MemberUsageQuery.TryParse(QueryBox.Text, kind, ScopeBox.Text, out var query))
        {
            Status.Text = Text("UI_MemberUsage_InvalidQuery", "Type a function or variable name, Class:Name or an object path.");
            return;
        }

        var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
        if (provider.Files.Count == 0)
        {
            Status.Text = Text("UI_MemberUsage_NoGame", "Load a game first.");
            return;
        }

        _cancellation = new CancellationTokenSource();
        var cancellationToken = _cancellation.Token;
        SetBusy(true);
        ShowRows([]);
        NoteList.ItemsSource = null;
        EmptyText.Visibility = Visibility.Collapsed;
        Status.Text = string.Format(Text("UI_MemberUsage_Searching", "Searching {0}..."), query.Display);

        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (_cancellation == null) return;
            if (p.Done >= p.Total)
            {
                Progress.IsIndeterminate = true;
                Status.Text = string.Format(Text("UI_MemberUsage_Analyzing", "Reading the bytecode of the matching packages ({0} packages scanned)..."), p.Total);
                return;
            }

            Progress.IsIndeterminate = false;
            Progress.Maximum = p.Total;
            Progress.Value = p.Done;
            Status.Text = string.Format(Text("UI_MemberUsage_Scanning", "Scanning packages {0:N0} / {1:N0}"), p.Done, p.Total);
        });

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await Task.Run(() => MemberUsageLookup.Run(provider, query, progress, cancellationToken), cancellationToken);
            ShowResult(result, stopwatch.Elapsed);
            FLogger.Append(ELog.Information, () =>
                FLogger.Text($"Found {result.Usages.Count} uses of {query.Display} in {result.Usages.Select(u => u.Path).Distinct().Count()} assets", Constants.WHITE, true));
        }
        catch (OperationCanceledException)
        {
            Status.Text = Text("UI_MemberUsage_Canceled", "Search canceled.");
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to search the uses of {Member}", query.Display);
            Status.Text = e.Message;
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            SetBusy(false);
        }

        await UpdateCacheTextAsync();
    }

    private void SetBusy(bool busy)
    {
        SearchButton.Content = Text(busy ? "UI_MemberUsage_Cancel" : "UI_MemberUsage_Search", busy ? "Cancel" : "Search");
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Progress.IsIndeterminate = true;
        QueryBox.IsEnabled = ScopeBox.IsEnabled = KindBox.IsEnabled = MemberPicker.IsEnabled = !busy;
        if (busy) ClearCacheButton.IsEnabled = false;
    }

    private void ShowResult(MemberUsageResult result, TimeSpan elapsed)
    {
        _result = result;
        ShowRows(result.Usages.Select(u => new MemberUsageRow(u, Text($"UI_MemberUsage_Kind_{u.Kind}", u.Kind.ToString()))).ToList());

        EmptyText.Text = string.Format(Text("UI_MemberUsage_Empty", "No blueprint uses {0}."), result.Query.Display);
        EmptyText.Visibility = result.Usages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoteList.ItemsSource = result.Notes.OrderBy(n => n)
            .Select(n => string.Format(Text($"UI_MemberUsageNote_{n}", n.ToString()), result.OnDemandSkippedCount, result.OnDemandImportCount)).ToList();
        _elapsed = elapsed;
        UpdateStatus();
    }

    private TimeSpan _elapsed;

    private void ShowRows(List<MemberUsageRow> rows)
    {
        _view = CollectionViewSource.GetDefaultView(rows);
        ApplyFilter();
        UsageGrid.ItemsSource = _view;
    }

    private void UpdateStatus()
    {
        if (_result == null) return;

        var assets = _result.Usages.Select(u => u.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var status = string.Format(Text("UI_MemberUsage_Summary", "{0:N0} uses in {1:N0} assets - {2:N0} packages scanned, {3:N0} read ({4:0.0}s)"),
            _result.Usages.Count, assets, _result.ScannedCount, _result.CandidateCount, _elapsed.TotalSeconds);

        var visible = _view?.Cast<object>().Count() ?? 0;
        if (visible != _result.Usages.Count) status += $" - {visible:N0} / {_result.Usages.Count:N0}";
        Status.Text = status;
    }

    private void OnFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
        UpdateStatus();
    }

    private void ApplyFilter()
    {
        if (_view == null) return;

        var tokens = FilterBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _view.Filter = tokens.Length == 0
            ? null
            : o => o is MemberUsageRow row && tokens.All(t =>
                row.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                row.UsedIn.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                row.Member.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                row.KindText.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                row.Path.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<MemberUsageRow> SelectedRows => UsageGrid.SelectedItems.OfType<MemberUsageRow>();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OpenButton.IsEnabled = OpenGraphButton.IsEnabled = UsageGrid.SelectedItem is MemberUsageRow;
    }

    private void OnUsageDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // ignore double clicks on the headers or the scroll bar
        if (e.OriginalSource is not DependencyObject source || ItemsControl.ContainerFromElement(UsageGrid, source) is not DataGridRow)
            return;

        if (UsageGrid.SelectedItem is MemberUsageRow { Usage.FunctionName: not null }) OpenGraph();
        else OpenAssets();
    }

    private void OnOpenGraphClick(object sender, RoutedEventArgs e) => OpenGraph();

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenAssets();

    /// <summary>
    /// the blueprint graph of the first selected row, opened at the function and statement using the member
    /// </summary>
    private void OpenGraph()
    {
        if (UsageGrid.SelectedItem is not MemberUsageRow { Usage: var usage }) return;

        var name = _result?.Query.Name;
        ApplicationService.ThreadWorkerView.Begin(cancellationToken =>
        {
            var graph = BlueprintGraphWindow.Load(usage.File, cancellationToken);
            if (graph == null)
            {
                FLogger.Append(ELog.Warning, () => FLogger.Text($"{usage.File.Name} is not a blueprint", Constants.WHITE, true));
                return;
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                var window = new BlueprintGraphWindow(graph);
                window.Show();
                window.ShowUsage(usage.FunctionName, usage.Offset, name);
            });
        });
    }

    private void OpenAssets()
    {
        var files = SelectedRows.Select(r => r.Usage.File).DistinctBy(f => f.Path).ToList();
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
        var paths = SelectedRows.Select(r => r.Path).Distinct().ToList();
        if (paths.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, paths));
    }

    private void OnCopyRowsClick(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows.Select(r => string.Join('\t', r.KindText, r.Name, r.UsedIn, r.Member, r.Count, r.Path)).ToList();
        if (rows.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, rows));
    }

    private string Text(string key, string fallback) => TryFindResource(key) as string ?? fallback;
}
