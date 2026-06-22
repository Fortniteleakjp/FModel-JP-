using System;
using System.Collections.Generic; // List<> を使用するために追加
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AdonisUI.Controls;
using UAssetAPI;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using CUE4Parse.UE4.Assets;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Exceptions;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels;
using FModel.Views;
using FModel.Views.Resources.Controls;
using ICSharpCode.AvalonEdit.Editing;
using FModel.Framework; // RelayCommand を使用するためのやつ
using System.Collections.Specialized; // NotifyCollectionChangedEventArgs を使用するためのやつ
using Microsoft.Win32;
using FModel.Features.Athena;
using Serilog;
using Ookii.Dialogs.Wpf;
using UAssetAPI.PropertyTypes.Structs;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxButton = AdonisUI.Controls.MessageBoxButton;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;
using Newtonsoft.Json;

namespace FModel;

public partial class MainWindow
{
    //「テスト」内のボタンを押した際に出てくる注意ウインドウ
    private bool ShowBetaFeatureWarning()
    {
        var message = "これらの機能はβ、α版です。意" +
                      "図しない処理が行われる可能性があります。" +
                      "これらの不具合に開発者は一切の責任を負いません。";
        var caption = "注意";
        var result = AdonisUI.Controls.MessageBox.Show(this, message, caption, AdonisUI.Controls.MessageBoxButton.OKCancel, AdonisUI.Controls.MessageBoxImage.Warning);
        return result == AdonisUI.Controls.MessageBoxResult.OK;
    }

    private async void OnAthenaAllCosmeticsClick(object sender, RoutedEventArgs e)
    {
        if (ShowBetaFeatureWarning())
            await GenerateAllCosmeticsFeature.ExecuteAsync();
    }

    private async void OnAthenaNewCosmeticsClick(object sender, RoutedEventArgs e)
    {
        if (ShowBetaFeatureWarning())
            await GenerateNewCosmeticsFeature.ExecuteAsync();
    }

    private async void OnAthenaNewCosmeticsWithPaksClick(object sender, RoutedEventArgs e)
    {
        if (ShowBetaFeatureWarning())
            await GenerateNewCosmeticsWithPaksFeature.ExecuteAsync();
    }

    private async void OnAthenaCustomByIdClick(object sender, RoutedEventArgs e)
    {
        if (ShowBetaFeatureWarning())
            await GenerateCustomCosmeticsByIdFeature.ExecuteAsync();
    }

    private async void OnBruteForceAesClick(object sender, RoutedEventArgs e)
    {
        if (ShowBetaFeatureWarning())
        {
            if (UserSettings.Default.BruteForceAesMode == EBruteForceAesMode.Gpu)
                await BruteForceAesGpuFeature.ExecuteAsync();
            else
                await BruteForceAesFeature.ExecuteAsync();
        }
    }

    private void OnAthenaPakCosmeticsClick(object sender, RoutedEventArgs e)
    {
        if (ShowBetaFeatureWarning())
            AthenaFeatureBase.LogNotImplemented("Pak Cosmetics");
    }
    private void OnAthenaPaksBulkClick(object sender, RoutedEventArgs e)
    {
        if (ShowBetaFeatureWarning())
            AthenaFeatureBase.LogNotImplemented("Paks Bulk");
    }

    private void OnAthenaBackClick(object sender, RoutedEventArgs e)
    {
        if (ShowBetaFeatureWarning())
            AthenaFeatureBase.LogNotImplemented("Back");
    }
}
