using System.Windows;
using FModel.ViewModels;
using Ookii.Dialogs.Wpf;

namespace FModel.Views;

public partial class ExportSessionWindow
{
    public ExportSessionWindow()
    {
        DataContext = ExportSessionViewModel.Instance;
        InitializeComponent();
    }

    private void OnBrowseOverrideDir(object sender, RoutedEventArgs e)
    {
        var dialog = new VistaFolderBrowserDialog { ShowNewFolderButton = true };
        if (dialog.ShowDialog() == true)
            ExportSessionViewModel.Instance.OverrideOutputDirectory = dialog.SelectedPath;
    }

    private void OnClearLog(object sender, RoutedEventArgs e) => ExportSessionViewModel.Instance.ClearLog();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
