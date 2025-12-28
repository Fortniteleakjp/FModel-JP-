using System.Windows;
using System.Windows.Controls;
using FModel.ViewModels;

namespace FModel.Views
{
    public partial class ReferenceViewerWindow : Window
    {
        public ReferenceViewerWindow(ReferenceViewerViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
        }

        private void TreeViewItem_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is TreeViewItem item && item.DataContext is ReferenceNodeViewModel node)
            {
                if (!string.IsNullOrEmpty(node.AssetPathName))
                {
                    // ファイルジャンプ処理（例: MainWindowのメソッド呼び出し等）
                    MessageBox.Show($"ジャンプ: {node.AssetPathName}");
                }
            }
        }
    }
}
