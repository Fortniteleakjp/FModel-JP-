using FModel.Framework;

namespace FModel.ViewModels
{
    // ローディング情報を管理する ViewModel
    public class LoadingInfoWindowViewModel : ViewModel
    {
        // 現在のステータス
        private string _statusText = "初期化中...";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        // 進行状況の値
        private double _progressValue;
        public double ProgressValue
        {
            get => _progressValue;
            set => SetProperty(ref _progressValue, value);
        }

        // 進行状況の文字列表示
        private string _progressText = "0%";
        public string ProgressText
        {
            get => _progressText;
            set => SetProperty(ref _progressText, value);
        }

        // 経過時間
        private string _elapsedTime = "00:00";
        public string ElapsedTime
        {
            get => _elapsedTime;
            set => SetProperty(ref _elapsedTime, value);
        }

        // 推定残り時間
        private string _estimatedTime = "計算中...";
        public string EstimatedTime
        {
            get => _estimatedTime;
            set => SetProperty(ref _estimatedTime, value);
        }

        // ダウンロードサイズ
        private string _downloadSize = "計算中...";
        public string DownloadSize
        {
            get => _downloadSize;
            set => SetProperty(ref _downloadSize, value);
        }

        // 進捗が不明な場合
        private bool _isIndeterminate = true;
        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            set => SetProperty(ref _isIndeterminate, value);
        }
    }
}