using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using FModel.Framework;

namespace FModel.Services;

/// <summary>
/// 実行中のバックグラウンド処理の一覧。ステータスバーのボタンとポップアップがこれを表示し、
/// 各処理はそこから個別に（または一括で）キャンセルできる。
/// 登録側は <see cref="Start"/> で受け取った <see cref="BackgroundJob"/> を処理の終わりに Dispose するだけでよい。
/// どのスレッドから呼んでもよく、一覧の更新は UI スレッドへ回す。
/// </summary>
public sealed class BackgroundJobs : ViewModel
{
    public static BackgroundJobs Instance { get; } = new();

    public ObservableCollection<BackgroundJob> Jobs { get; } = [];

    public int Count => Jobs.Count;
    public bool HasJobs => Jobs.Count > 0;

    private bool _isVisible;
    /// <summary>
    /// ステータスバーのボタンを出すか。アセットを開くたびに一瞬だけ走るメイン処理でボタンが点滅しないよう、
    /// 1 秒以上続いている処理があるときだけ true にする。
    /// </summary>
    public bool IsVisible
    {
        get => _isVisible;
        private set => SetProperty(ref _isVisible, value);
    }

    private static readonly TimeSpan VisibleAfter = TimeSpan.FromSeconds(1);
    public bool HasCancelableJobs
    {
        get
        {
            foreach (var job in Jobs)
                if (job.CanCancel) return true;
            return false;
        }
    }

    public Command CancelAllCommand { get; }

    private DispatcherTimer _timer;

    private BackgroundJobs()
    {
        CancelAllCommand = new ActionCommand(_ => CancelAll());
        Jobs.CollectionChanged += (_, _) =>
        {
            RaisePropertyChanged(nameof(Count));
            RaisePropertyChanged(nameof(HasJobs));
            RaisePropertyChanged(nameof(HasCancelableJobs));
            UpdateTimer();
        };
    }

    /// <summary>
    /// 処理を一覧に載せる。<paramref name="cancel"/> が null の処理はキャンセルボタンを出さない。
    /// <paramref name="detail"/> を渡すと、表示中は定期的に呼び出して詳細欄を更新する（頻繁に変わる文言向け）。
    /// </summary>
    public static BackgroundJob Start(string title, Action cancel = null, Func<string> detail = null)
    {
        var job = new BackgroundJob(title, cancel, detail);
        Instance.OnUi(() => Instance.Jobs.Add(job));
        return job;
    }

    /// <summary><see cref="CancellationTokenSource"/> を持つ処理の登録用。</summary>
    public static BackgroundJob Start(string title, CancellationTokenSource cancellation, Func<string> detail = null)
        => Start(title, cancellation == null ? null : () =>
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { /* 終わった直後 */ }
        }, detail);

    public void CancelAll()
    {
        foreach (var job in Jobs.ToList())
            job.Cancel();
    }

    internal void Remove(BackgroundJob job) => OnUi(() => Jobs.Remove(job));

    internal void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private void UpdateTimer()
    {
        if (Jobs.Count == 0)
        {
            _timer?.Stop();
            IsVisible = false;
            return;
        }

        if (_timer == null)
        {
            if (Application.Current?.Dispatcher is not { } dispatcher) return;
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
        }

        _timer.Start();
    }

    private void Tick()
    {
        var visible = false;
        foreach (var job in Jobs)
        {
            job.Refresh();
            visible |= job.ElapsedTime >= VisibleAfter;
        }

        IsVisible = visible;
    }
}

public sealed class BackgroundJob : ViewModel, IDisposable
{
    private readonly Action _cancel;
    private readonly Func<string> _detailProvider;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private int _disposed;

    public string Title { get; }
    public DateTime StartedAt { get; } = DateTime.Now;
    public bool IsCancelable => _cancel != null;
    public bool CanCancel => IsCancelable && !IsCancelling;
    public Command CancelCommand { get; }

    private string _detail;
    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    private bool _isIndeterminate = true;
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set => SetProperty(ref _isIndeterminate, value);
    }

    private double _progress;
    /// <summary>0..1</summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    private bool _isCancelling;
    public bool IsCancelling
    {
        get => _isCancelling;
        private set
        {
            if (SetProperty(ref _isCancelling, value))
                RaisePropertyChanged(nameof(CanCancel));
        }
    }

    public TimeSpan ElapsedTime => _stopwatch.Elapsed;

    public string Elapsed
    {
        get
        {
            var elapsed = _stopwatch.Elapsed;
            return elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"m\:ss");
        }
    }

    internal BackgroundJob(string title, Action cancel, Func<string> detail)
    {
        Title = title;
        _cancel = cancel;
        _detailProvider = detail;
        CancelCommand = new ActionCommand(_ => Cancel());
        if (detail != null) _detail = SafeDetail();
    }

    /// <summary>詳細欄の文言だけを更新する（進捗は不定のまま）。</summary>
    public void Report(string detail) => BackgroundJobs.Instance.OnUi(() => Detail = detail);

    /// <summary>進捗を更新する。<paramref name="total"/> が 0 以下なら不定表示。</summary>
    public void Report(double done, double total, string detail = null)
    {
        BackgroundJobs.Instance.OnUi(() =>
        {
            IsIndeterminate = total <= 0;
            Progress = total > 0 ? Math.Clamp(done / total, 0, 1) : 0;
            if (detail != null) Detail = detail;
        });
    }

    public void Cancel()
    {
        if (_cancel == null || IsCancelling || _disposed != 0) return;

        IsCancelling = true;
        _cancel();
    }

    internal void Refresh()
    {
        RaisePropertyChanged(nameof(Elapsed));
        if (_detailProvider != null) Detail = SafeDetail();
    }

    private string SafeDetail()
    {
        try { return _detailProvider(); }
        catch { return _detail; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _stopwatch.Stop();
        BackgroundJobs.Instance.Remove(this);
    }
}

internal sealed class ActionCommand(Action<object> execute) : Command
{
    public override void Execute(object parameter) => execute(parameter);
    public override bool CanExecute(object parameter) => true;
}
