using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using FModel.Framework;

namespace FModel.ViewModels
{
    public class PacProgressWindowViewModel : INotifyPropertyChanged
    {
        private string _title;
        public string Title { get => _title; set => SetField(ref _title, value); }

        private string _message;
        public string Message
        {
            get => _message;
            set => SetField(ref _message, value);
        }

        private int _progress;
        public int Progress
        {
            get => _progress;
            set => SetField(ref _progress, value);
        }

        private string _eta = "計算中...";
        public string ETA
        {
            get => _eta;
            set => SetField(ref _eta, value);
        }

        public ICommand CancelCommand { get; set; }

        public PacProgressWindowViewModel(string title, string initialMessage)
        {
            Title = title;
            Message = initialMessage;
            CancelCommand = null;
        }

        public void Update(int progress, string message)
        {
            Progress = progress;
            Message = message;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }
}