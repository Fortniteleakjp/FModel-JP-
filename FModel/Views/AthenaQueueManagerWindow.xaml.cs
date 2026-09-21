using System.Windows;
using FModel.ViewModels;

namespace FModel.Views;

public partial class AthenaQueueManagerWindow
{
    private readonly AthenaQueueManagerViewModel _viewModel;

    public AthenaQueueManagerWindow()
    {
        DataContext = _viewModel = new AthenaQueueManagerViewModel();
        InitializeComponent();
    }

    private void OnRemoveSelectedClick(object sender, RoutedEventArgs e)
    {
        _viewModel.RemoveSelected();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _viewModel.Clear();
    }

    private async void OnCreateProfileClick(object sender, RoutedEventArgs e)
    {
        await _viewModel.GenerateProfile();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
