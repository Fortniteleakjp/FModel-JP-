using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AdonisUI.Controls;
using FModel.Framework;
using FModel.Settings;
using FModel.ViewModels;
using FModel.Views;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxButton = AdonisUI.Controls.MessageBoxButton;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;
using MessageBoxResult = AdonisUI.Controls.MessageBoxResult;
using J = Newtonsoft.Json.JsonPropertyAttribute;

namespace FModel.ViewModels.ApiEndpoints.Models;

public class GitHubRelease
{
    [J("assets")] public GitHubAsset[] Assets { get; private set; }
}

public class GitHubAsset : ViewModel
{
    [J("name")] public string Name { get; private set; }
    [J("size")] public int Size { get; private set; }
    [J("download_count")] public int DownloadCount { get; private set; }
    [J("browser_download_url")] public string BrowserDownloadUrl { get; private set; }
    [J("created_at")] public DateTime CreatedAt { get; private set; }
    [J("uploader")] public Author Uploader { get; private set; }

    private bool _isLatest;
    public bool IsLatest
    {
        get => _isLatest;
        set => SetProperty(ref _isLatest, value);
    }
}

public class GitHubCommit : ViewModel
{
    private string _sha;
    [J("sha")]
    public string Sha
    {
        get => _sha;
        set
        {
            SetProperty(ref _sha, value);
            RaisePropertyChanged(nameof(IsCurrent));
            RaisePropertyChanged(nameof(ShortSha));
        }
    }

    [J("commit")] public Commit Commit { get; set; }
    [J("author")] public Author Author { get; set; }

    private GitHubAsset _asset;
    public GitHubAsset Asset
    {
        get => _asset;
        set
        {
            SetProperty(ref _asset, value);
            RaisePropertyChanged(nameof(IsDownloadable));
        }
    }

    public bool IsCurrent => Sha == Constants.APP_COMMIT_ID;
    public string ShortSha => Sha[..7];
    public bool IsDownloadable => Asset != null;

    public async void Download()
    {
        if (IsCurrent)
        {
            MessageBox.Show(new MessageBoxModel
            {
                Text = "あなたは最新バージョンをインストール済みです/You have the latest version installed.",
                Caption = "Update FModel",
                Icon = MessageBoxImage.Information,
                Buttons = [MessageBoxButtons.Ok()],
                IsSoundEnabled = false
            });
            return;
        }

        var messageBox = new MessageBoxModel
        {
            Text = $"FModelを '{ShortSha}' にアップデートしますか？?{(!Asset.IsLatest ? "\n※ 最新バージョンではありません" : "")}",
            Caption = "Update FModel",
            Icon = MessageBoxImage.Question,
            Buttons = MessageBoxButtons.YesNo(),
            IsSoundEnabled = false
        };

        MessageBox.Show(messageBox);
        if (messageBox.Result != MessageBoxResult.Yes) return;

        var progressViewModel = new UpdateDownloadProgressViewModel(
            "アップデートをダウンロード中",
            $"FModel {ShortSha} の更新パッケージをダウンロードしています...");
        var progressWindow = new UpdateDownloadProgressWindow(progressViewModel);
        progressWindow.Show();

        try
        {
            await DownloadAndReplaceExecutableAsync(progressViewModel);
            progressViewModel.Complete();

            MessageBox.Show(
                "アップデートを適用するため、FModelを終了して再起動します。",
                "Update FModel",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Application.Current.Shutdown();
        }
        catch (OperationCanceledException) when (progressViewModel.Token.IsCancellationRequested)
        {
            // ユーザーによるキャンセル時はエラーダイアログを表示しない。
        }
        catch (Exception exception)
        {
            UserSettings.Default.ShowChangelog = false;
            MessageBox.Show(exception.Message, exception.GetType().ToString(), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (!progressViewModel.IsCompleted)
                progressViewModel.Complete();
        }
    }

    private async Task DownloadAndReplaceExecutableAsync(UpdateDownloadProgressViewModel progressViewModel)
    {
        var appPath = Constants.APP_PATH;
        var baseName = Path.GetFileNameWithoutExtension(appPath);
        var isDllHost = appPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

        var preferredTargetExePath = appPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? appPath
            : Path.ChangeExtension(appPath, ".exe");

        var updateRoot = Path.Combine(Path.GetTempPath(), "FModel-Update", Guid.NewGuid().ToString("N"));
        var zipPath = Path.Combine(updateRoot, "update.zip");
        var extractDir = Path.Combine(updateRoot, "extracted");
        var scriptPath = Path.Combine(updateRoot, "apply_update.cmd");

        Directory.CreateDirectory(updateRoot);
        Directory.CreateDirectory(extractDir);

        using (var httpClient = new HttpClient())
        using (var response = await httpClient.GetAsync(
                   Asset.BrowserDownloadUrl,
                   HttpCompletionOption.ResponseHeadersRead,
                   progressViewModel.Token))
        {
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength;

            await using var source = await response.Content.ReadAsStreamAsync(progressViewModel.Token).ConfigureAwait(false);
            await using var destination = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long downloadedBytes = 0;
            progressViewModel.Report(downloadedBytes, totalBytes);

            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), progressViewModel.Token).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), progressViewModel.Token).ConfigureAwait(false);
                downloadedBytes += bytesRead;
                progressViewModel.Report(downloadedBytes, totalBytes);
            }
        }

        ZipFile.ExtractToDirectory(zipPath, extractDir, true);

        var candidateFileNames = new[]
        {
            Path.GetFileName(preferredTargetExePath),
            baseName + ".exe",
            "FModel.exe"
        }
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

        var downloadedExe = candidateFileNames
            .SelectMany(name => Directory.EnumerateFiles(extractDir, name, SearchOption.AllDirectories))
            .FirstOrDefault();

        if (string.IsNullOrEmpty(downloadedExe))
            throw new FileNotFoundException($"更新パッケージ内に実行ファイル ({string.Join(", ", candidateFileNames)}) が見つかりませんでした。", zipPath);

        var replacementTargetPath = downloadedExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? preferredTargetExePath
            : appPath;

        var restartTargetPath = replacementTargetPath;
        var script = BuildApplyUpdateScript(Environment.ProcessId, downloadedExe, replacementTargetPath, updateRoot, restartTargetPath, isDllHost);
        File.WriteAllText(scriptPath, script, Encoding.ASCII);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/C \"{scriptPath}\"",
            WorkingDirectory = updateRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static string BuildApplyUpdateScript(int currentProcessId, string sourceExePath, string targetExePath, string cleanupDir, string restartTargetPath, bool wasDllHost)
    {
        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine("setlocal");
        script.AppendLine($"set \"PID={currentProcessId}\"");
        script.AppendLine($"set \"SRC={sourceExePath}\"");
        script.AppendLine($"set \"DST={targetExePath}\"");
        script.AppendLine($"set \"RUN={restartTargetPath}\"");
        script.AppendLine($"set \"CLEANUP={cleanupDir}\"");
        script.AppendLine();
        script.AppendLine("for /L %%i in (1,1,90) do (");
        script.AppendLine("  tasklist /FI \"PID eq %PID%\" | findstr /R /C:\" %PID% \" >nul");
        script.AppendLine("  if errorlevel 1 goto apply");
        script.AppendLine("  timeout /t 1 /nobreak >nul");
        script.AppendLine(")");
        script.AppendLine();
        script.AppendLine(":apply");
        script.AppendLine("copy /Y \"%SRC%\" \"%DST%\" >nul");
        if (wasDllHost && restartTargetPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            script.AppendLine("start \"\" dotnet \"%RUN%\"");
        else
            script.AppendLine("start \"\" \"%RUN%\"");
        script.AppendLine("rmdir /S /Q \"%CLEANUP%\" >nul 2>nul");
        script.AppendLine("endlocal");
        return script.ToString();
    }
}

public class Commit
{
    [J("author")] public Author Author { get; set; }
    [J("message")] public string Message { get; set; }
}

public class Author
{
    [J("name")] public string Name { get; set; }
    [J("login")] public string Login { get; set; }
    [J("date")] public DateTime Date { get; set; }
    [J("avatar_url")] public string AvatarUrl { get; set; }
    [J("html_url")] public string HtmlUrl { get; set; }
}
