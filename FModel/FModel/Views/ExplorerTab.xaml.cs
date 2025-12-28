using System.Windows.Controls;

namespace FModel.Views
{
    public partial class ExplorerTab : UserControl
    {
        public ExplorerTab(string rootPath = null)
        {
            InitializeComponent();
            this.DataContext = new FModel.ViewModels.ExplorerTabViewModel(rootPath);
        }
    }
}
