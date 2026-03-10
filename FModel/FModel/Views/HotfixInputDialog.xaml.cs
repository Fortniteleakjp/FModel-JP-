using System.Windows;

namespace FModel.Views
{
    /// <summary>
    /// HotfixInputDialog.xaml の相互作用ロジック
    /// </summary>
    public partial class HotfixInputDialog : AdonisUI.Controls.AdonisWindow
    {
        /// <summary>
        /// ホットフィックステキスト
        /// </summary>
        public string HotfixText => HotfixTextBox.Text;

        public HotfixInputDialog()
        {
            InitializeComponent();
            Owner = Application.Current.MainWindow;
        }

        private void OnApplyClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(HotfixTextBox.Text))
            {
                MessageBox.Show(this, "ホットフィックスを入力してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}