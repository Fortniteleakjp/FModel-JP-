using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CUE4Parse.FileProvider.Objects;
using FModel.Extensions;
using FModel.Framework;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels.ApiEndpoints.Models;
using FModel.Views.Resources.Controls;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;
using Ookii.Dialogs.Wpf;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using Serilog;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxButton = AdonisUI.Controls.MessageBoxButton;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;

namespace FModel.ViewModels;

public class BackupManagerViewModel : ViewModel
{
    public const uint FBKP_MAGIC = 0x504B4246;
    private ThreadWorkerViewModel _threadWorkerView => ApplicationService.ThreadWorkerView;
    private ApiEndpointViewModel _apiEndpointView => ApplicationService.ApiEndpointView;
    private ApplicationViewModel _applicationView => ApplicationService.ApplicationView;
    private readonly string _gameName;
    private Backup _selectedBackup;
    public Backup SelectedBackup
    {
        get => _selectedBackup;
        set => SetProperty(ref _selectedBackup, value);
    }
    private bool _isCreatingBackup;
    public bool IsCreatingBackup
    {
        get => _isCreatingBackup;
        set => SetProperty(ref _isCreatingBackup, value);
    }
    public ObservableCollection<Backup> Backups { get; }
    public ICollectionView BackupsView { get; }
    public BackupManagerViewModel(string gameName)
    {
        _gameName = gameName;
        Backups = new ObservableCollection<Backup>();
        BackupsView = new ListCollectionView(Backups) { SortDescriptions = { new SortDescription("FileName", ListSortDirection.Ascending) } };
    }
    public async Task Initialize()
    {
        await _threadWorkerView.Begin(cancellationToken =>
        {
            var backups = _apiEndpointView.FModelApi.GetBackups(cancellationToken, _gameName);
            if (backups == null)
                return;
            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var backup in backups)
                    Backups.Add(backup);
                SelectedBackup = Backups.LastOrDefault();
            });
        });
    }
    public async Task CreateBackup()
    {
        await _threadWorkerView.Begin(_ =>
        {
            var backupFolder = Path.Combine(UserSettings.Default.OutputDirectory, "Backups");
            var fileName = $"{_gameName}_{DateTime.Now:MM'_'dd'_'yyyy}.fbkp";
            var fullPath = Path.Combine(backupFolder, fileName);
            var func = new Func<GameFile, bool>(x => !x.IsUePackagePayload);
            using var fileStream = new FileStream(fullPath, FileMode.Create);
            using var compressedStream = LZ4Stream.Encode(fileStream, LZ4Level.L00_FAST);
            using var writer = new BinaryWriter(compressedStream);
            writer.Write(FBKP_MAGIC);
            writer.Write((byte)EBackupVersion.Latest);
            writer.Write(_applicationView.CUE4Parse.Provider.Files.Values.Count(func));
            foreach (var asset in _applicationView.CUE4Parse.Provider.Files.Values)
            {
                if (!func(asset))
                    continue;
                writer.Write(asset.Size);
                writer.Write(asset.IsEncrypted);
                writer.Write(asset.Path);
            }
            SaveCheck(fullPath, fileName, "created", "create");
        });
    }
    public async Task Download()
    {
        if (SelectedBackup == null)
            return;
        await _threadWorkerView.Begin(_ =>
        {
            var fullPath = Path.Combine(Path.Combine(UserSettings.Default.OutputDirectory, "Backups"), SelectedBackup.FileName);
            _apiEndpointView.DownloadFile(SelectedBackup.DownloadUrl, fullPath);
            SaveCheck(fullPath, SelectedBackup.FileName, "downloaded", "download");
        });
    }
    private void SaveCheck(string fullPath, string fileName, string type1, string type2)
    {
        if (new FileInfo(fullPath).Length > 0)
        {
            Log.Information("{FileName} successfully {Type}", fileName, type1);
            FLogger.Append(ELog.Information, () =>
            {
                FLogger.Text($"Successfully {type1} ", Constants.WHITE);
                FLogger.Link(fileName, fullPath, true);
            });
        }
        else
        {
            Log.Error("{FileName} could not be {Type}", fileName, type1);
            FLogger.Append(ELog.Error, () => FLogger.Text($"Could not {type2} '{fileName}'", Constants.WHITE, true));
        }
    }
    public async Task CreateBackupHeavy()
    {
        var dialog = new VistaFolderBrowserDialog
        {
            Description = @"バックアップファイル (大) の保存先フォルダを指定してください。",
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() != true)
            return;
        var selectedFolder = dialog.SelectedPath;
        var defaultFolderName = _applicationView.CUE4Parse.Provider.GameDisplayName ?? _gameName;
        var sanitizedDefaultFolderName = StringExtensions.RemoveInvalidFileNameChars(defaultFolderName);
        var targetPath = Path.Combine(selectedFolder, sanitizedDefaultFolderName);
        var gameDirectory = UserSettings.Default.GameDirectory;
        var allFiles = Directory.EnumerateFiles(gameDirectory, "*", SearchOption.AllDirectories).ToList();
        long totalSize = allFiles.Sum(f => new FileInfo(f).Length);
        string sizeInfo = $"このバックアップファイルは、{StringExtensions.GetReadableSize(totalSize)}のディスク容量を使用します。\n";
        bool? dialogResult = false;
        Application.Current.Dispatcher.Invoke(() =>
        {
            var inputDialog = new InputDialog("Backup Folder Name", sanitizedDefaultFolderName, sizeInfo);
            dialogResult = inputDialog.ShowDialog();
            if (dialogResult == true)
            {
                targetPath = Path.Combine(selectedFolder, StringExtensions.RemoveInvalidFileNameChars(inputDialog.InputText));
            }
        });
        if (dialogResult != true)
            return;
        Directory.CreateDirectory(targetPath);
        var drive = new DriveInfo(Path.GetPathRoot(targetPath)!);
        if (drive.AvailableFreeSpace < totalSize)
        {
            MessageBox.Show(
                $"ディスクには、バックアップファイルを作成するだけの空き容量がありません。\n" +
                $"必要な空き容量 : {StringExtensions.GetReadableSize(totalSize)}\n" +
                $"利用可能 : {StringExtensions.GetReadableSize(drive.AvailableFreeSpace)}",
                "容量不足", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var progressViewModel = new ProgressWindowViewModel
        {
            Message = "バックアップファイル（大）を作成しています...",
            Progress = 0
        };
        var progressWindow = new ProgressWindow
        {
            DataContext = progressViewModel
        };
        progressWindow.Show();
        await Task.Run(async () =>
        {
            var cancellationToken = progressViewModel.Token;
            var totalFiles = allFiles.Count;
            int completedFiles = 0;
            var stopwatch = Stopwatch.StartNew();
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = cancellationToken
            };
            int lastReported = 0;
            try
            {
                await Parallel.ForEachAsync(allFiles, options, async (file, innerCancellationToken) =>
                {
                    innerCancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var relative = Path.GetRelativePath(gameDirectory, file);
                        var dest = Path.Combine(targetPath, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            FLogger.Append(ELog.Information, () => FLogger.Text($"コピー中: {Path.GetFileName(file)}", Constants.WHITE, true));
                        });
                        await using (var sourceStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
                        await using (var destinationStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
                        {
                            await sourceStream.CopyToAsync(destinationStream, innerCancellationToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error copying file: {File}", file);
                    }
                    int done = Interlocked.Increment(ref completedFiles);
                    if (done - lastReported >= 2 || done == totalFiles)
                    {
                        Interlocked.Exchange(ref lastReported, done);
                        var percent = (double)done / totalFiles * 100;
                        var elapsed = stopwatch.Elapsed;
                        var etaSeconds = (elapsed.TotalSeconds / done) * (totalFiles - done);
                        var eta = TimeSpan.FromSeconds(etaSeconds);
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            progressViewModel.Progress = percent;
                            progressViewModel.ETA = $"推定残り時間 : {eta:hh\\:mm\\:ss}";
                        });
                    }
                });
                FLogger.Append(ELog.Information, () =>
                {
                    FLogger.Text("バックアップファイル(大)の生成が完了しました ", Constants.WHITE);
                    FLogger.Link(targetPath, targetPath, true);
                });
            }
            catch (OperationCanceledException)
            {
                FLogger.Append(ELog.Warning, () =>
                {
                    FLogger.Text("バックアップファイル(大)の作成はユーザーによってキャンセルされました。", Constants.YELLOW, true);
                });
            }
            finally
            {
                stopwatch.Stop();
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    progressWindow.Close();
                });
            }
        });
    }
}
    public enum EBackupVersion : byte
    {
        BeforeVersionWasAdded = 0,
        Initial,
        PerfectPath, // no more leading slash and ToLower
        LatestPlusOne,
        Latest = LatestPlusOne - 1
    }