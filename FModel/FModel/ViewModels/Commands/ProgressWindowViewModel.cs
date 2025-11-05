using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Input;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using FModel.Framework;

namespace FModel.ViewModels
{
    public class ProgressWindowViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler RequestClose;
        private string _message = string.Empty;
        public string Message
        {
            get => _message;
            set => SetField(ref _message, value);
        }
        private double _progress;
        public double Progress
        {
            get => _progress;
            set => SetField(ref _progress, value);
        }
        private string _eta = "推定残り時間 : 計測中...";
        public string ETA
        {
            get => _eta;
            set => SetField(ref _eta, value);
        }
        private readonly CancellationTokenSource _cts = new();
        public CancellationToken Token => _cts.Token;
        public ICommand CancelCommand { get; }

        public ProgressWindowViewModel()
        {
            CancelCommand = new RelayCommand(Cancel, CanCancel);
        }

        public void UpdateProgress(int current, int total, string message)
        {
            Progress = (double)current / total * 100;
            Message = message;

            // ETA calculation can be added here if needed
        }

        public void Complete()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                RequestClose?.Invoke(this, EventArgs.Empty);
            });
        }

        private void Cancel(object obj)
        {
            _cts.Cancel();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
            Complete();
        }

        private bool CanCancel(object obj)
        {
            return !_cts.IsCancellationRequested;
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }
}