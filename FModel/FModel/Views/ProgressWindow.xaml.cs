using System.Windows;
using FModel.ViewModels;

using AdonisUI.Controls; // AdonisWindow を解決するために追加

namespace FModel.Views
{
    public partial class ProgressWindow : AdonisWindow
    {
        private readonly ProgressWindowViewModel _viewModel;

        public ProgressWindow()
        {
            InitializeComponent();
        }

        public ProgressWindow(ProgressWindowViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;
            if (_viewModel != null)
                _viewModel.RequestClose += (s, e) => Close();
        }
    }
}
