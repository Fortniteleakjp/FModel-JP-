using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CUE4Parse_Conversion;
using FModel.Framework;

namespace FModel.ViewModels;

/// <summary>
/// 新エクスポートパイプライン(ExportSession)の進捗を集約し、Export Session ウインドウへ表示する。
/// CUE4Parse_Conversion.ExportSession の IProgress&lt;ExportProgress&gt; として渡される。
/// （4sval/FModel PR #684 の Export Session 相当を日本語で実装）
/// </summary>
public sealed class ExportSessionViewModel : ViewModel, IProgress<ExportProgress>
{
    private static ExportSessionViewModel _instance;
    public static ExportSessionViewModel Instance => _instance ??= new ExportSessionViewModel();

    private int _totalQueued;
    /// <summary>現在実行中のエクスポート件数（0 で待機中）。</summary>
    public int TotalQueued { get => _totalQueued; private set => SetProperty(ref _totalQueued, value); }

    private int _completed;
    /// <summary>累計の成功ファイル数。</summary>
    public int Completed { get => _completed; private set => SetProperty(ref _completed, value); }

    private int _failed;
    /// <summary>累計の失敗数。</summary>
    public int Failed { get => _failed; private set => SetProperty(ref _failed, value); }

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }

    private double _progressPercent;
    public double ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, value); }

    private string _progressText = "待機中";
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }

    private string _elapsedTime = "00:00";
    public string ElapsedTime { get => _elapsedTime; private set => SetProperty(ref _elapsedTime, value); }

    /// <summary>アクティビティログ（新しいものが先頭）。</summary>
    public ObservableCollection<string> Log { get; } = new();

    private readonly Stopwatch _stopwatch = new();
    private int _activeExports;

    /// <summary>エクスポート開始時に呼ぶ。</summary>
    public void BeginExport(string name) => Dispatch(() =>
    {
        if (_activeExports == 0)
        {
            _stopwatch.Restart();
            IsRunning = true;
        }
        _activeExports++;
        TotalQueued = _activeExports;
        AddLog($"開始: {name}");
    });

    /// <summary>ExportSession からの進捗報告（ワーカースレッドから呼ばれ得る）。</summary>
    public void Report(ExportProgress value) => Dispatch(() =>
    {
        ProgressPercent = value.Percentage >= 0 ? value.Percentage * 100 : 0;
        ProgressText = value.DisplayText;
        ElapsedTime = _stopwatch.Elapsed.ToString(@"mm\:ss");

        if (value.LastResult is { } r)
        {
            if (r.Success)
            {
                Completed++;
                AddLog($"成功: {ShortName(r.DiskFilePath ?? r.ObjectPath)}");
            }
            else
            {
                Failed++;
                AddLog($"失敗: {ShortName(r.ObjectPath)} — {r.Error?.Message}");
            }
        }
    });

    /// <summary>エクスポート終了時に呼ぶ。</summary>
    public void EndExport(string name) => Dispatch(() =>
    {
        _activeExports = Math.Max(0, _activeExports - 1);
        TotalQueued = _activeExports;
        AddLog($"完了: {name}");
        if (_activeExports == 0)
        {
            _stopwatch.Stop();
            IsRunning = false;
            ProgressText = "待機中";
            ElapsedTime = _stopwatch.Elapsed.ToString(@"mm\:ss");
        }
    });

    public void ClearLog() => Dispatch(() => Log.Clear());

    private void AddLog(string line)
    {
        Log.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {line}");
        while (Log.Count > 500) Log.RemoveAt(Log.Count - 1);
    }

    private static string ShortName(string path)
        => string.IsNullOrEmpty(path) ? path : Path.GetFileName(path.Replace('\\', '/'));

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}
