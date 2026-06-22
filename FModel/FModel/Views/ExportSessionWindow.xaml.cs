using System.Windows;
using FModel.ViewModels;

namespace FModel.Views;

public partial class ExportSessionWindow
{
    public ExportSessionWindow()
    {
        DataContext = ExportSessionViewModel.Instance;
        InitializeComponent();
    }

    private void OnClearLog(object sender, RoutedEventArgs e) => ExportSessionViewModel.Instance.ClearLog();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
