// ProgressWindowViewModel.cs
using System;
using System.Threading;
using System.Windows.Input;
using FModel.Framework;

public class ProgressWindowViewModel : ViewModel
{
    private string _message = "バックアップを作成中...";
    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    private string _eta = "残り時間計測中...";
    public string ETA
    {
        get => _eta;
        set => SetProperty(ref _eta, value);
    }

    public ICommand CancelCommand { get; }

    private readonly CancellationTokenSource _cts = new();
    public CancellationToken Token => _cts.Token;

    public ProgressWindowViewModel()
    {
        CancelCommand = new RelayCommand(() => _cts.Cancel());
    }
}
