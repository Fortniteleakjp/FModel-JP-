using System.Windows;
using System.Windows.Controls;

namespace FModel.Views.Resources.Controls
{
    public partial class CommitDownloaderControl : UserControl
    {
        public CommitDownloaderControl()
        {
            InitializeComponent();
        }

        // Commit プロパティをバインド可能にする例
        public object Commit
        {
            get { return GetValue(CommitProperty); }
            set { SetValue(CommitProperty, value); }
        }

        public static readonly DependencyProperty CommitProperty =
            DependencyProperty.Register("Commit", typeof(object), typeof(CommitDownloaderControl), new PropertyMetadata(null));

        private void OnDownload(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("ダウンロード開始: " + Commit);
        }
    }
}
