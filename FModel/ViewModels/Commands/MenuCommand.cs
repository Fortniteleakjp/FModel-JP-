using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using AdonisUI.Controls;
using FModel.Extensions;
using FModel.Framework;
using FModel.Services;
using FModel.Settings;
using FModel.Views;
using FModel.Views.Resources.Controls;
using Newtonsoft.Json;

namespace FModel.ViewModels.Commands;

public class MenuCommand : ViewModelCommand<ApplicationViewModel>
{
    public MenuCommand(ApplicationViewModel contextViewModel) : base(contextViewModel)
    {
    }

    public override async void Execute(ApplicationViewModel contextViewModel, object parameter)
    {
        switch (parameter)
        {
            case "Directory_Selector":
                contextViewModel.AvoidEmptyGameDirectory(true);
                break;
            case "Directory_AES":
                Helper.OpenWindow<AdonisWindow>("AES Manager", () => new AesManager().Show());
                break;
            case "Directory_Backup":
                Helper.OpenWindow<AdonisWindow>("Backup Manager", () => new BackupManager(contextViewModel.CUE4Parse.Provider.ProjectName).Show());
                break;
            case "Directory_ArchivesInfo":
                ApplicationService.ApplicationView.IsAssetsExplorerVisible = false;
                contextViewModel.CUE4Parse.TabControl.AddTab("Archives Info");
                contextViewModel.CUE4Parse.TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("json");
                contextViewModel.CUE4Parse.TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(contextViewModel.CUE4Parse.GameDirectory.DirectoryFiles, Formatting.Indented), false, false);
                break;
            case "Views_3dViewer":
                contextViewModel.CUE4Parse.SnooperViewer.Run();
                break;
            case "Views_ExportSession":
                Helper.OpenWindow<AdonisWindow>("Export Session", () => new ExportSessionWindow().Show());
                break;
            case "Views_AudioPlayer":
                Helper.OpenWindow<AdonisWindow>("Audio Player", () => new AudioPlayer().Show());
                break;
            case "Views_GameplayTags":
                Helper.OpenWindow<AdonisWindow>("Gameplay Tags", () => new GameplayTagBrowserWindow().Show());
                break;
            case "Views_ImageMerger":
                Helper.OpenWindow<AdonisWindow>("Image Merger", () => new ImageMerger().Show());
                break;
            case "Settings":
                Helper.OpenWindow<AdonisWindow>("Settings", () => new SettingsView().Show());
                break;
            case "Help_About":
                Helper.OpenWindow<AdonisWindow>("About", () => new About().Show());
                break;
            case "Help_Donate":
                Process.Start(new ProcessStartInfo { FileName = Constants.DONATE_LINK, UseShellExecute = true });
                break;
            case "Help_Releases":
                // JP 版のリリースノート (同梱 + GitHub Releases) を表示する。
                // upstream のコミット履歴を出す UpdateView は更新検知時のダイアログとしてのみ使う。
                Helper.OpenWindow<ReleaseNotesWindow>(() => new ReleaseNotesWindow().Show());
                break;
            case "Help_BugsReport":
                Process.Start(new ProcessStartInfo { FileName = Constants.ISSUE_LINK, UseShellExecute = true });
                break;
            case "Help_Discord":
                Process.Start(new ProcessStartInfo { FileName = Constants.DISCORD_LINK, UseShellExecute = true });
                break;
            case "ToolBox_Clear_Logs":
                FLogger.ClearLogs();
                break;
            case "ToolBox_Open_Output_Directory":
                OpenDirectorySafe(UserSettings.Default.OutputDirectory, "Output");
                break;
            case "ToolBox_Open_Exports_Directory":
                OpenDirectorySafe(Path.Combine(UserSettings.Default.OutputDirectory, "Exports"), "Exports");
                break;
            case "ToolBox_Open_Textures_Directory":
                OpenDirectorySafe(UserSettings.Default.TextureDirectory, "Textures");
                break;
            case "ToolBox_Open_Models_Directory":
                OpenDirectorySafe(UserSettings.Default.ModelDirectory, "Models");
                break;
            case "ToolBox_Open_Audios_Directory":
                OpenDirectorySafe(UserSettings.Default.AudioDirectory, "Audios");
                break;
            case "ToolBox_Open_Properties_Directory":
                OpenDirectorySafe(UserSettings.Default.PropertiesDirectory, "Properties");
                break;
            case "ToolBox_Open_RawData_Directory":
                OpenDirectorySafe(UserSettings.Default.RawDataDirectory, "Raw Data");
                break;
            case "ToolBox_Open_Code_Directory":
                OpenDirectorySafe(UserSettings.Default.CodeDirectory, "Code");
                break;
            case "ToolBox_Open_Backups_Directory":
                OpenDirectorySafe(Path.Combine(UserSettings.Default.OutputDirectory, "Backups"), "Backups");
                break;
            case "ToolBox_Open_Logs_Directory":
                OpenDirectorySafe(Path.Combine(UserSettings.Default.OutputDirectory, "Logs"), "Logs");
                break;
            case "ToolBox_Collapse_All":
                contextViewModel.CUE4Parse.AssetsFolder.CollapseAll();
                break;
            case TreeItem selectedFolder:
                MainWindow.Instance.SelectFolder(selectedFolder);
                break;
        }
    }

    /// <summary>
    /// Opens a folder in the file explorer, creating it when it does not exist yet.
    /// Falls back to the output directory if the setting is empty or unreachable.
    /// </summary>
    private static void OpenDirectorySafe(string directory, string label)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory))
                directory = UserSettings.Default.OutputDirectory;
            if (string.IsNullOrWhiteSpace(directory))
            {
                FLogger.Append(ELog.Warning, () => FLogger.Text($"No directory configured for \"{label}\"", Constants.WHITE, true));
                return;
            }

            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
        }
        catch (Exception e)
        {
            FLogger.Append(ELog.Error, () => FLogger.Text($"Could not open the \"{label}\" directory: {e.Message}", Constants.WHITE, true));
        }
    }
}
