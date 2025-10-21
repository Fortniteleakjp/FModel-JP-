using AdonisUI.Controls;
using FModel.ViewModels;

namespace FModel.Views
{
    public partial class LoadingInfoWindow : AdonisWindow
    {
        public LoadingInfoWindow(LoadingInfoWindowViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
