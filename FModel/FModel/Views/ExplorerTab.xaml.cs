using System.Windows.Controls;

namespace FModel.Views
{
    public partial class ExplorerTab : UserControl
    {
        public ExplorerTab(string rootPath = null)
        {
            InitializeComponent();
            // rootPath が null の場合はデフォルトでカレントディレクトリ
            if (string.IsNullOrEmpty(rootPath))
                rootPath = System.IO.Directory.GetCurrentDirectory();
            this.DataContext = new FModel.ViewModels.ExplorerTabViewModel(rootPath);
        }
    }
}
