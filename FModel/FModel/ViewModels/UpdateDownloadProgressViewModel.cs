using System;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using FModel.Framework;

namespace FModel.ViewModels;

public sealed class UpdateDownloadProgressViewModel : ViewModel
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private string _title;
    private string _message;
    private string _downloadedText = "0 B";
    private double _progress;
    private bool _isIndeterminate = true;
    private bool _isCancelling;
    private bool _isCompleted;

    public event EventHandler RequestClose;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public string DownloadedText
    {
        get => _downloadedText;
        private set => SetProperty(ref _downloadedText, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set => SetProperty(ref _isIndeterminate, value);
    }

    public bool IsCancelling
    {
        get => _isCancelling;
        private set => SetProperty(ref _isCancelling, value);
    }

    public bool IsCompleted
    {
        get => _isCompleted;
        private set => SetProperty(ref _isCompleted, value);
    }

    public CancellationToken Token => _cancellationTokenSource.Token;
    public ICommand CancelCommand { get; }

    public UpdateDownloadProgressViewModel(string title, string message)
    {
        Title = title;
        Message = message;
        CancelCommand = new RelayCommand(_ => Cancel(), _ => !_cancellationTokenSource.IsCancellationRequested);
    }

    public void Report(long downloadedBytes, long? totalBytes)
    {
        void update()
        {
            DownloadedText = totalBytes is > 0
                ? $"{FormatBytes(downloadedBytes)} / {FormatBytes(totalBytes.Value)}"
                : FormatBytes(downloadedBytes);

            if (totalBytes is > 0)
            {
                IsIndeterminate = false;
                Progress = Math.Min(100d, downloadedBytes * 100d / totalBytes.Value);
            }
        }

        InvokeOnUiThread(update);
    }

    public void Cancel()
    {
        if (_cancellationTokenSource.IsCancellationRequested)
            return;

        _cancellationTokenSource.Cancel();
        IsCancelling = true;
        Message = "ダウンロードをキャンセルしています...";
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public void Complete()
    {
        InvokeOnUiThread(() =>
        {
            IsCompleted = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
        });
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(0, bytes);
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{value:0} {units[unitIndex]}" : $"{value:0.0} {units[unitIndex]}";
    }

    private static void InvokeOnUiThread(Action action)
    {
        if (Application.Current?.Dispatcher.CheckAccess() == true)
            action();
        else
            Application.Current?.Dispatcher.BeginInvoke(action);
    }
}
