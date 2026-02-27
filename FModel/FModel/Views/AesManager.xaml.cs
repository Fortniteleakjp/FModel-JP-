using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using FModel.Framework;
using FModel.Services;
using FModel.ViewModels;
using FModel.Settings;
using FModel;
using FModel.Views;
using FModel.Views.Resources.Controls;

namespace FModel.Views;

public partial class AesManager
{
    private ApplicationViewModel _applicationView => ApplicationService.ApplicationView;

    public AesManager()
    {
        DataContext = _applicationView;
        InitializeComponent();
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        _applicationView.AesManager.HasChange = true;
        Close();
    }

    private async void OnRefreshAes(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        if (button != null) button.IsEnabled = false;
        try
        {
            await _applicationView.CUE4Parse.RefreshAesForAllAsync();
            await _applicationView.AesManager.InitAes();
            _applicationView.AesManager.HasChange = true; // yes even if nothing actually changed
        }
        catch (System.Exception ex)
        {
            FLogger.Append(ELog.Error, () => FLogger.Text($"AES Refresh Error: {ex.Message}", Constants.RED));
        }
        finally
        {
            if (button != null) button.IsEnabled = true;
        }
    }

    private bool _canClose;
    private async void OnClosing(object sender, CancelEventArgs e)
    {
        if (!_canClose)
        {
            e.Cancel = true;
            try
            {
                if (_applicationView.AesManager.HasChange)
                {
                    UserSettings.Save();
                    await _applicationView.UpdateProvider(true);
                }
            }
            finally
            {
                _canClose = true;
                Dispatcher.BeginInvoke(new Action(Close));
            }
        }
    }
}
