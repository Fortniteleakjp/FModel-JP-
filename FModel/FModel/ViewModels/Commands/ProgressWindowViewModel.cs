// ProgressWindowViewModel.cs
using System;
using System.Threading;
using System.Windows.Input;
using FModel.Framework;

public class ProgressWindowViewModel : ViewModel
{
    private DateTime _startTime;
    private double _progress;
    private int _totalSteps;
    private int _currentStep;
    public string Message { get; set; } = "バックアップを作成中...";
    public string ETA { get; private set; } = "残り時間を計測中...";
    public double Progress
    {
        get => _progress;
        set
        {
            SetProperty(ref _progress, value);
            UpdateETA();
        }
    }
    public ICommand CancelCommand { get; }
    private readonly CancellationTokenSource _cts = new();
    public CancellationToken Token => _cts.Token;
    public ProgressWindowViewModel(int totalSteps)
    {
        _totalSteps = totalSteps;
        _startTime = DateTime.Now;
        CancelCommand = new RelayCommand(() => _cts.Cancel());
    }
    public void StepForward()
    {
        _currentStep++;
        Progress = (_currentStep / (double)_totalSteps) * 100;
    }
    private void UpdateETA()
    {
        if (_currentStep == 0) return;
        var elapsed = DateTime.Now - _startTime;
        var timePerStep = elapsed.TotalSeconds / _currentStep;
        var remainingSteps = _totalSteps - _currentStep;
        var remainingTime = TimeSpan.FromSeconds(timePerStep * remainingSteps);
        ETA = $"残り時間: {remainingTime:hh\\:mm\\:ss}";
        OnPropertyChanged(nameof(ETA));
    }
}