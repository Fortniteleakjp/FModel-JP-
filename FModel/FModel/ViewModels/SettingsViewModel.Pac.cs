using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Reflection;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.VirtualFileCache.Manifest;
using System.Windows.Input; // For ICommand
using System.Windows.Media; // For Brush
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.UEFormat.Enums;
using CUE4Parse.UE4.Assets.Exports.Nanite;
using EpicManifestParser;
using EpicManifestParser.Api;
using EpicManifestParser.UE;
using EpicManifestParser.ZlibngDotNetDecompressor;
using CUE4Parse.Compression;
using FModel.Framework;
using FModel.ViewModels.ApiEndpoints.Models;
using FModel.Services;
using FModel.Settings;
using FModel.Views;
using FModel.Views.Resources.Controls;
using Serilog;

namespace FModel.ViewModels;

public partial class SettingsViewModel
{
    #region Pac化
    private string _installedBundlesPath;
    public string InstalledBundlesPath
    {
        get => _installedBundlesPath;
        set
        {
            if (SetProperty(ref _installedBundlesPath, value))
            {
                UpdateIslandProjects();
                ((RelayCommand)ExecutePacCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public ObservableCollection<IslandProject> IslandProjects { get; } = new();

    private IslandProject _selectedIslandProject;
    public IslandProject SelectedIslandProject
    {
        get => _selectedIslandProject;
        set
        {
            if (SetProperty(ref _selectedIslandProject, value))
            {
                ((RelayCommand)ExecutePacCommand).RaiseCanExecuteChanged();
            }
        }
    }

    private bool _isPieMode;
    public bool IsPieMode { get => _isPieMode; set => SetProperty(ref _isPieMode, value); }

    private void BrowseInstalledBundles(object parameter)
    {
        var dialog = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog();
        if (dialog.ShowDialog() == true)
        {
            InstalledBundlesPath = dialog.SelectedPath;
        }
    }

    private async void ExecutePac(object parameter)
    {
        var progressVM = new PacProgressWindowViewModel("pac化を実行中...", "準備しています...");
        var progressWindow = new PacProgressWindow(progressVM)
        {
            Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w is SettingsView)
        };

        var cancellationTokenSource = new CancellationTokenSource();
        progressVM.CancelCommand = new RelayCommand(_ => cancellationTokenSource.Cancel());

        progressWindow.Show();

        try
        {
            await Task.Run(() =>
            {
                var islandFolder = SelectedIslandProject.FullPath;
                ExecutePacCore(islandFolder, cancellationTokenSource.Token, progressVM, true);
                progressVM.Update(100, "完了しました！");
                Thread.Sleep(1000); // Show "Completed" for a moment
            }, cancellationTokenSource.Token);

            await RefreshArchivesAfterPacAsync();
        }
        catch (OperationCanceledException)
        {
            progressVM.Update(0, "キャンセルされました。");
            await Task.Delay(1500);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラーが発生しました: {ex.Message}", "pak化エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            progressWindow.Close();
        }
    }

    private void ExecutePacCore(string islandFolder, CancellationToken cancellationToken, PacProgressWindowViewModel progressVM, bool launchUefn)
    {
        var gamePath = UserSettings.Default.GameDirectory;

        int fortniteGameIndex = gamePath.IndexOf("FortniteGame", StringComparison.OrdinalIgnoreCase);
        if (fortniteGameIndex == -1)
        {
            throw new DirectoryNotFoundException("ゲームフォルダ内に'FortniteGame'ディレクトリが見つかりません。設定を確認してください。");
        }

        string fortniteRootPath = gamePath.Substring(0, fortniteGameIndex);
        var contentPaksPath = Path.Combine(fortniteRootPath, "FortniteGame", "Content", "Paks");
        var uefnIslandsExe = Path.Combine(fortniteRootPath, "FortniteGame", "Binaries", "Win64", "UEFN-Islands.exe");
        string[] pluginExtensions = { ".pak", ".sig", ".utoc", ".ucas" };

        foreach (var ext in pluginExtensions)
        {
            var legacyFile = Path.Combine(contentPaksPath, "pakchunk99Island-WindowsClient" + ext);
            if (File.Exists(legacyFile))
            {
                File.Delete(legacyFile);
            }
        }

        progressVM?.Update(10, "プラグインファイルを確認しています...");
        cancellationToken.ThrowIfCancellationRequested();
        var hasAnyPluginFile = pluginExtensions.Any(ext => File.Exists(Path.Combine(islandFolder, "plugin" + ext)));
        if (!hasAnyPluginFile)
        {
            throw new FileNotFoundException("plugin.* ファイルが見つかりません。InstalledBundlesフォルダが正しいか確認してください。", islandFolder);
        }

        progressVM?.Update(30, ".utocから島名を抽出しています...");
        cancellationToken.ThrowIfCancellationRequested();
        var utocPath = Path.Combine(islandFolder, "plugin.utoc");
        if (!File.Exists(utocPath))
        {
            throw new FileNotFoundException("plugin.utoc ファイルが見つかりません。InstalledBundlesフォルダが正しいか確認してください。", utocPath);
        }

        var extractedPluginName = ExtractIslandNameFromUtoc(utocPath, progressVM);

        progressVM?.Update(50, "Paksフォルダにファイルをコピーしています...");
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var ext in pluginExtensions)
        {
            var sourceFile = Path.Combine(islandFolder, "plugin" + ext);
            var destFile = Path.Combine(contentPaksPath, "plugin" + ext);
            if (File.Exists(sourceFile))
            {
                if (File.Exists(destFile)) File.Delete(destFile);
                File.Copy(sourceFile, destFile, true);
            }
        }

        if (!launchUefn)
        {
            return;
        }

        if (File.Exists(uefnIslandsExe) && !string.IsNullOrEmpty(extractedPluginName))
        {
            progressVM?.Update(80, "UEFN-Islandsを起動しています...");
            cancellationToken.ThrowIfCancellationRequested();
            var pieArg = IsPieMode ? ",ValkyriePIE" : "";
            var arguments = $"-disableplugins=\"ValkyrieFortnite,AtomVK\" -enableplugins=\"{extractedPluginName}{pieArg}\"";
            Process.Start(uefnIslandsExe, arguments);
        }
        else
        {
            progressVM?.Update(80, "UEFN-Islands.exeが見つかりません。起動をスキップします。");
            if (progressVM != null)
            {
                Thread.Sleep(2000);
            }
        }
    }

    private bool ValidateExtractedPluginFiles(string folderPath, out string error)
    {
        error = string.Empty;

        var pakPath = Path.Combine(folderPath, "plugin.pak");
        var utocPath = Path.Combine(folderPath, "plugin.utoc");
        var ucasPath = Path.Combine(folderPath, "plugin.ucas");

        if (!File.Exists(pakPath))
        {
            error = "plugin.pak が存在しません";
            return false;
        }

        if (!File.Exists(utocPath))
        {
            error = "plugin.utoc が存在しません";
            return false;
        }

        if (!File.Exists(ucasPath))
        {
            error = "plugin.ucas が存在しません";
            return false;
        }

        if (new FileInfo(pakPath).Length <= 0 || new FileInfo(utocPath).Length <= 0 || new FileInfo(ucasPath).Length <= 0)
        {
            error = "pluginコンテナファイルのサイズが0です";
            return false;
        }

        if (!HasValidUtocMagic(utocPath))
        {
            error = "plugin.utoc のヘッダーが不正です";
            return false;
        }

        return true;
    }

    private static bool HasValidUtocMagic(string utocPath)
    {
        try
        {
            var expectedMagic = new byte[] { 0x2D, 0x3D, 0x3D, 0x2D, 0x2D, 0x3D, 0x3D, 0x2D, 0x2D, 0x3D, 0x3D, 0x2D, 0x2D, 0x3D, 0x3D, 0x2D };
            var buffer = new byte[16];
            using var fs = new FileStream(utocPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Read(buffer, 0, buffer.Length) != buffer.Length)
            {
                return false;
            }

            return buffer.SequenceEqual(expectedMagic);
        }
        catch
        {
            return false;
        }
    }

    private async Task RefreshArchivesAfterPacAsync()
    {
        try
        {
            FLogger.Append(ELog.Information, () => FLogger.Text("アーカイブ一覧を再読み込みしています...", Constants.WHITE, true));

            var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
            if (provider != null)
            {
                await Task.Run(() => provider.Initialize());
            }

            await ApplicationService.ApplicationView.UpdateProvider(true);
            FLogger.Append(ELog.Information, () => FLogger.Text("アーカイブ一覧を更新しました。", Constants.GREEN, true));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "アーカイブ一覧の再読み込みに失敗しました");
            FLogger.Append(ELog.Warning, () => FLogger.Text($"アーカイブ一覧の再読み込みに失敗しました: {ex.Message}", Constants.YELLOW, true));
        }
    }

    private bool CanExecutePac(object parameter)
    {
        return SelectedIslandProject != null && !string.IsNullOrEmpty(UserSettings.Default.GameDirectory);
    }

    private void UpdateIslandProjects()
    {
        Application.Current.Dispatcher.Invoke(() => IslandProjects.Clear());
        if (string.IsNullOrEmpty(InstalledBundlesPath) || !Directory.Exists(InstalledBundlesPath)) return;

        try
        {
            var directories = Directory.GetDirectories(InstalledBundlesPath);
            var projects = new List<IslandProject>();
            foreach (var dir in directories)
            {
                var dirInfo = new DirectoryInfo(dir);
                projects.Add(new IslandProject(dirInfo.Name, dirInfo.LastWriteTime, dir));
            }
            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var p in projects.OrderByDescending(p => p.LastModified))
                {
                    IslandProjects.Add(p);
                }
                SelectedIslandProject = IslandProjects.FirstOrDefault();
            });
        }
        catch (Exception e)
        {
            FLogger.Append(ELog.Error, () => FLogger.Text($"プロジェクトの読み込みに失敗しました: {e.Message}", Constants.RED, true));
        }
    }

    private string ExtractIslandNameFromUtoc(string utocPath, PacProgressWindowViewModel progressVM)
    {
        // This is a direct port of the logic from MIddleMan's ExtractIslandNameFromPak.cs
        // It's fragile but should work for its intended purpose.
        var fileBytes = File.ReadAllBytes(utocPath);
        var pattern = Encoding.UTF8.GetBytes("/FortniteGame/Plugins/GameFeatures/");
        int index = fileBytes.AsSpan().IndexOf(pattern);
        if (index == -1)
        {
            progressVM?.Update(40, "警告: .utocからプラグイン名を抽出できませんでした。");
            if (progressVM != null)
            {
                Thread.Sleep(2000); // ユーザーがメッセージを読めるように少し待機
            }
            return null;
        }

        int startIndex = index + pattern.Length;
        int endIndex = fileBytes.AsSpan(startIndex).IndexOf((byte)'/');
        if (endIndex == -1)
        {
            throw new Exception(".utocファイルからプラグイン名を抽出できませんでした。");
        }

        return Encoding.UTF8.GetString(fileBytes, startIndex, endIndex);
    }
    #endregion
}

public class IslandProject
{
    public string Name { get; }
    public DateTime LastModified { get; }
    public string FullPath { get; }
    public string DisplayName => $"{Name} - {LastModified:yyyy/MM/dd HH:mm}";

    public IslandProject(string name, DateTime lastModified, string fullPath)
    {
        Name = name;
        LastModified = lastModified;
        FullPath = fullPath;
    }
}
