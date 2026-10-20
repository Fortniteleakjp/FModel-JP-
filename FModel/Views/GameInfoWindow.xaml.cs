using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AdonisUI.Controls;
using CUE4Parse.MappingsProvider;
using FModel.Framework;
using FModel.Services;
using FModel.Services.GameInfo;
using FModel.Settings;
using Microsoft.Win32;
using Serilog;

namespace FModel.Views;

/// <summary>
/// 読み込み中のゲームの情報 (UE バージョンの検出結果) と、マッピングファイルの中身 (型の一覧) を見るウィンドウ
/// </summary>
public partial class GameInfoWindow : AdonisWindow
{
    private CancellationTokenSource _reportCancellation;
    private CancellationTokenSource _filterCancellation;
    private GameInfoReport _report;

    private TypeMappings _mappings;
    private List<MappingTypeEntry> _entries = [];
    private bool _mappingsRequested;
    private readonly Func<GameInfoContext> _contextFactory;

    /// <param name="contextFactory">調べるゲーム。省略時はメインウィンドウで読み込み中のゲーム</param>
    public GameInfoWindow(Func<GameInfoContext> contextFactory = null)
    {
        _contextFactory = contextFactory ?? CurrentContext;
        InitializeComponent();
        Tabs.SelectionChanged += OnTabChanged;
    }

    /// <summary>開いていれば前面に出してタブを切り替え、無ければ開く</summary>
    public static void Open(bool mappings)
    {
        Helper.OpenWindow<GameInfoWindow>(() =>
        {
            var window = new GameInfoWindow();
            window.SelectTab(mappings);
            window.Show();
        });
        Application.Current.Windows.OfType<GameInfoWindow>().FirstOrDefault()?.SelectTab(mappings);
    }

    /// <summary>メニューからマッピングを直接開いたときなど、表示するタブを選ぶ</summary>
    public void SelectTab(bool mappings) => Tabs.SelectedItem = mappings ? MappingTab : GameTab;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshReportAsync();
        if (Tabs.SelectedItem == MappingTab) await LoadLoadedMappingsAsync();
    }

    private async void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // 型の一覧は大きいので、マッピングタブを開いたときに初めて作る
        if (e.OriginalSource != Tabs || !IsLoaded || Tabs.SelectedItem != MappingTab || _mappingsRequested) return;
        await LoadLoadedMappingsAsync();
    }

    private void OnClosing(object sender, CancelEventArgs e)
    {
        _reportCancellation?.Cancel();
        _filterCancellation?.Cancel();
    }

    private static GameInfoContext CurrentContext()
    {
        var cue4Parse = ApplicationService.ApplicationView?.CUE4Parse;
        return new GameInfoContext(cue4Parse?.Provider, UserSettings.Default.CurrentDir, cue4Parse?.MappingsFilePath, cue4Parse?.LiveBuildVersion);
    }

    #region Game / UE version

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshReportAsync();

    private async Task RefreshReportAsync()
    {
        _reportCancellation?.Cancel();
        _reportCancellation = new CancellationTokenSource();
        var cancellationToken = _reportCancellation.Token;

        RefreshButton.IsEnabled = false;
        CopyReportButton.IsEnabled = false;
        SetStatus("ゲームの情報を調べています…");
        using var job = BackgroundJobs.Start(TryFindResource("UI_GameInfo") as string ?? "Game Info", _reportCancellation);
        try
        {
            var context = _contextFactory();
            _report = await Task.Run(() => GameInfoCollector.Collect(context, cancellationToken), cancellationToken);
            ReportSections.ItemsSource = _report.Sections;
            ReportWarnings.ItemsSource = _report.Warnings;
            ReportWarnings.Visibility = _report.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            CopyReportButton.IsEnabled = true;
            SetStatus(_report.Warnings.Count > 0 ? $"確認が必要な項目が {_report.Warnings.Count} 件あります" : "調査が完了しました");
        }
        catch (OperationCanceledException)
        {
            // 閉じた / 再読み込みした
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to collect the game information");
            SetStatus($"ゲームの情報を取得できませんでした: {exception.Message}");
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void OnCopyReportClick(object sender, RoutedEventArgs e)
    {
        if (_report == null) return;

        var sb = new StringBuilder();
        foreach (var warning in _report.Warnings)
            sb.AppendLine($"⚠ {warning}");
        if (_report.Warnings.Count > 0) sb.AppendLine();

        foreach (var section in _report.Sections)
            AppendSection(sb, section);

        TrySetClipboard(sb.ToString().TrimEnd());
    }

    private static void AppendSection(StringBuilder sb, InfoSection section)
    {
        sb.AppendLine($"[{section.Title}]");
        foreach (var row in section.Rows)
            sb.AppendLine(row.Note is null ? $"{row.Label}: {row.Value}" : $"{row.Label}: {row.Value} ({row.Note})");
        sb.AppendLine();
    }

    #endregion

    #region Mappings

    private async void OnUseLoadedMappingClick(object sender, RoutedEventArgs e) => await LoadLoadedMappingsAsync();

    private async Task LoadLoadedMappingsAsync()
    {
        _mappingsRequested = true;
        var context = _contextFactory();
        var mappings = context.Provider?.MappingsContainer?.MappingsForGame;
        if (mappings == null)
        {
            ShowMappings(null, null, null, "マッピングは読み込まれていません。ファイルを開いて中身を確認できます。");
            return;
        }

        var path = context.MappingsFilePath;
        await LoadMappingsAsync(path, () =>
        {
            var header = !string.IsNullOrEmpty(path) && File.Exists(path) ? MappingsInspector.ReadHeader(path) : null;
            return (header, mappings);
        }, "読み込み中のマッピング");
    }

    private async void OnOpenMappingClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "マッピングファイルを選択",
            InitialDirectory = Directory.Exists(CacheManager.MappingsDirectory) ? CacheManager.MappingsDirectory : null,
            Filter = "Mappings (*.usmap, *.jmap, *.jmap.gz)|*.usmap;*.jmap;*.jmap.gz|All Files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        _mappingsRequested = true;
        var path = dialog.FileName;
        await LoadMappingsAsync(path, () => (MappingsInspector.ReadHeader(path), MappingsInspector.LoadMappings(path)), "ファイルから開いたマッピング");
    }

    private async Task LoadMappingsAsync(string path, Func<(MappingsHeader Header, TypeMappings Mappings)> load, string source)
    {
        OpenMappingButton.IsEnabled = false;
        UseLoadedMappingButton.IsEnabled = false;
        SetStatus("マッピングを読み込んでいます…");
        try
        {
            var (header, mappings, entries) = await Task.Run(() =>
            {
                var (h, m) = load();
                return (h, m, m is null ? new List<MappingTypeEntry>() : MappingsInspector.BuildEntries(m));
            });
            _entries = entries;
            ShowMappings(header, mappings, path, path is null ? source : $"{source}: {path}");
            SetStatus($"{entries.Count:N0} 個の型を読み込みました");
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to read the mappings {Path}", path);
            SetStatus($"マッピングを読み込めませんでした: {exception.Message}");
        }
        finally
        {
            OpenMappingButton.IsEnabled = true;
            UseLoadedMappingButton.IsEnabled = true;
        }
    }

    private void ShowMappings(MappingsHeader header, TypeMappings mappings, string path, string source)
    {
        _mappings = mappings;
        if (mappings == null) _entries = [];
        MappingSourceText.Text = source;
        MappingSourceText.ToolTip = path;

        var section = new InfoSection("ファイル情報");
        if (header != null)
            MappingsInspector.AddHeaderRows(section, header);
        else if (mappings != null)
            section.Add("ファイル", "読み込み済み (ファイルの場所は不明)");
        if (mappings != null)
            MappingsInspector.AddStatisticsRows(section, MappingsInspector.GetStatistics(mappings));
        MappingHeaderSection.ItemsSource = section.Rows.Count > 0 ? new[] { section } : null;

        TypeDetailBox.Text = string.Empty;
        SelectedTypeText.Text = string.Empty;
        CopyTypeButton.IsEnabled = false;
        ApplyTypeFilter();
    }

    private void OnTypeFilterTextChanged(object sender, TextChangedEventArgs e) => ApplyTypeFilter(TimeSpan.FromMilliseconds(200));

    private void OnKindFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ApplyTypeFilter();
    }

    private async void ApplyTypeFilter(TimeSpan delay = default)
    {
        _filterCancellation?.Cancel();
        _filterCancellation = new CancellationTokenSource();
        var cancellationToken = _filterCancellation.Token;

        var entries = _entries;
        var terms = (TypeFilterBox.Text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var kind = KindFilter.SelectedIndex; // 0=すべて 1=構造体 2=列挙型
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            var filtered = terms.Length == 0 && kind == 0
                ? entries
                : await Task.Run(() => entries
                    .Where(entry => kind == 0 || entry.IsEnum == (kind == 2))
                    .Where(entry => terms.All(term => entry.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
                    .ToList(), cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;

            TypeList.ItemsSource = filtered;
            TypeCountText.Text = entries.Count == 0 ? string.Empty : $"{filtered.Count:N0} / {entries.Count:N0} 個";
        }
        catch (OperationCanceledException)
        {
            // 次の入力が来た
        }
    }

    private void OnTypeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (TypeList.SelectedItem is not MappingTypeEntry entry || _mappings == null)
        {
            CopyTypeButton.IsEnabled = false;
            return;
        }

        SelectedTypeText.Text = $"{entry.Kind} {entry.Name}";
        TypeDetailBox.Text = MappingsInspector.Describe(_mappings, entry);
        TypeDetailBox.ScrollToHome();
        CopyTypeButton.IsEnabled = true;
    }

    private void OnCopyTypeClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TypeDetailBox.Text)) TrySetClipboard(TypeDetailBox.Text);
    }

    #endregion

    private void TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            SetStatus("クリップボードにコピーしました");
        }
        catch (Exception exception)
        {
            // 他のアプリがクリップボードを開いていると失敗する
            SetStatus($"コピーできませんでした: {exception.Message}");
        }
    }

    private void SetStatus(string text) => Status.Text = text;
}
