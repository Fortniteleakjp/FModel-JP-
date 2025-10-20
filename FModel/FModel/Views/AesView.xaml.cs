using System.Windows;

namespace FModel.Views
{
    public partial class AesView
    {
        public AesView(string aesKey, string guid)
        {
            InitializeComponent();
            AesKeyTextBox.Text = aesKey;
            GuidTextBox.Text = guid;
        }

        private void OnCopyAes(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(AesKeyTextBox.Text);
        }

        private void OnCopyGuid(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(GuidTextBox.Text);
        }
    }
}
