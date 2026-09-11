using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using FModel.ViewModels;

namespace FModel.Views;

public partial class ReleaseNotesWindow
{
    public ReleaseNotesWindow()
    {
        DataContext = new ReleaseNotesViewModel();
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ReleaseNotesViewModel viewModel) return;
        _ = viewModel.LoadAsync();
    }

    private void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Uri?.AbsoluteUri)) return;

        Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
        e.Handled = true;
    }
}
