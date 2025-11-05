using System.Windows;
using FModel.ViewModels;

namespace FModel.Views
{
    public partial class ProgressWindow : Window
    {
        private readonly ProgressWindowViewModel _viewModel;

        public ProgressWindow(ProgressWindowViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;

            _viewModel.RequestClose += (s, e) => Close();
        }
    }
}