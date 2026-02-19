using System.Threading;
using System.Windows;
using AdonisUI.Controls;

namespace FModel.Views
{
    public partial class BruteForceProgressWindow : AdonisWindow
    {
        public CancellationTokenSource CancellationTokenSource { get; }

        public BruteForceProgressWindow()
        {
            InitializeComponent();
            CancellationTokenSource = new CancellationTokenSource();
        }

        public void SetTargetFile(string fileName)
        {
            TargetFileTextBlock.Text = fileName;
        }

        public void UpdateAttemptCount(long count)
        {
            AttemptCountTextBlock.Text = count.ToString("N0");
        }

        public void UpdateCurrentKey(string key)
        {
            CurrentKeyTextBox.Text = key;
        }

        public void UpdateElapsedTime(System.TimeSpan elapsed)
        {
            ElapsedTimeTextBlock.Text = elapsed.ToString(@"hh\:mm\:ss");
        }

        public void UpdateRate(double rate)
        {
            RateTextBlock.Text = rate.ToString("N0");
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            CancellationTokenSource.Cancel();
            CancelButton.IsEnabled = false;
            CancelButton.Content = "キャンセル中...";
        }

        protected override void OnClosed(System.EventArgs e)
        {
            base.OnClosed(e);
            if (!CancellationTokenSource.IsCancellationRequested)
            {
                CancellationTokenSource.Cancel();
            }
        }
    }
}
