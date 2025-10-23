using AdonisUI.Controls;
using FModel.ViewModels;

namespace FModel.Views.Resources.Controls
{
    public partial class PacProgressWindow : AdonisWindow
    {
        public PacProgressWindow(PacProgressWindowViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}