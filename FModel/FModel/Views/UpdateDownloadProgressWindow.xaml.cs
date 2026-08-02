using System.ComponentModel;
using AdonisUI.Controls;
using FModel.ViewModels;

namespace FModel.Views;

public partial class UpdateDownloadProgressWindow : AdonisWindow
{
    private readonly UpdateDownloadProgressViewModel _viewModel;

    public UpdateDownloadProgressWindow(UpdateDownloadProgressViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.RequestClose += OnRequestClose;
    }

    private void OnRequestClose(object sender, System.EventArgs e)
    {
        Close();
    }

    private void OnClosing(object sender, CancelEventArgs e)
    {
        if (_viewModel.IsCompleted)
            return;

        _viewModel.Cancel();
        e.Cancel = true;
    }
}
