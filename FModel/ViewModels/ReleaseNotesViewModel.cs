using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AdonisUI.Controls;
using AutoUpdaterDotNET;
using FModel.Framework;
using FModel.Services;
using FModel.Services.ReleaseNotes;
using FModel.ViewModels.ApiEndpoints.Models;
using Serilog;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxButton = AdonisUI.Controls.MessageBoxButton;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;
using MessageBoxResult = AdonisUI.Controls.MessageBoxResult;

namespace FModel.ViewModels;

public class ReleaseNotesViewModel : ViewModel
{
    public RangeObservableCollection<ReleaseNote> Releases { get; } = [];

    private ReleaseNote _selectedRelease;
    public ReleaseNote SelectedRelease
    {
        get => _selectedRelease;
        set => SetProperty(ref _selectedRelease, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    private bool _isEmpty;
    public bool IsEmpty
    {
        get => _isEmpty;
        set => SetProperty(ref _isEmpty, value);
    }

    private GitHubAsset _latestBuild;
    /// <summary>FModel-JP の "qa" リリースにある一番新しいビルド。</summary>
    public GitHubAsset LatestBuild
    {
        get => _latestBuild;
        set
        {
            SetProperty(ref _latestBuild, value);
            RaisePropertyChanged(nameof(CanDownload));
            RaisePropertyChanged(nameof(LatestBuildInfo));
        }
    }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            SetProperty(ref _isDownloading, value);
            RaisePropertyChanged(nameof(CanDownload));
        }
    }

    /// <summary>実行中のビルドが既に最新かどうか。</summary>
    public bool IsUpToDate => LatestBuild is not null && string.Equals(LatestBuildSha, Constants.APP_COMMIT_ID, StringComparison.OrdinalIgnoreCase);

    public bool CanDownload => LatestBuild is not null && !IsDownloading && !IsUpToDate;

    private string LatestBuildSha => LatestBuild is null ? string.Empty : Path.GetFileNameWithoutExtension(LatestBuild.Name);

    public string LatestBuildInfo
    {
        get
        {
            if (LatestBuild is null) return string.Empty;

            var sha = LatestBuildSha;
            if (sha.Length > 7) sha = sha[..7];

            var size = $"{LatestBuild.Size / 1024d / 1024d:0.0} MB";
            return $"{sha} · {LatestBuild.CreatedAt.ToLocalTime():yyyy/MM/dd} · {size}";
        }
    }

    /// <summary>
    /// まず同梱分を即座に表示し、そのあと GitHub Releases で補完する。
    /// </summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            Releases.AddRange(ReleaseNotesService.GetBundled());
            SelectedRelease = Releases.FirstOrDefault();

            var all = await ReleaseNotesService.LoadAsync();
            if (all.Length != Releases.Count)
            {
                var selectedKey = SelectedRelease?.Key;
                Releases.Clear();
                Releases.AddRange(all);
                SelectedRelease = Releases.FirstOrDefault(r => r.Key == selectedKey) ?? Releases.FirstOrDefault();
            }

            IsEmpty = Releases.Count == 0;

            await LoadLatestBuildAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadLatestBuildAsync()
    {
        try
        {
            var release = await ApplicationService.ApiEndpointView.GitHubApi.GetJpReleaseAsync();
            if (release?.Assets is not { Length: > 0 }) return;

            LatestBuild = release.Assets.MaxBy(a => a.CreatedAt);
            RaisePropertyChanged(nameof(IsUpToDate));
        }
        catch (Exception e)
        {
            Log.Warning("Failed to fetch the latest FModel-JP build: {msg}", e.Message);
        }
    }

    /// <summary>
    /// 最新ビルドをダウンロードして更新する。AutoUpdater が展開と差し替えを行うため、成功したらアプリを終了する。
    /// </summary>
    public void DownloadLatestBuild()
    {
        if (LatestBuild is null) return;

        var sha = LatestBuildSha;
        if (sha.Length > 7) sha = sha[..7];

        if (IsUpToDate)
        {
            MessageBox.Show(new MessageBoxModel
            {
                Text = "You are already on the latest version.",
                Caption = "Update FModel-JP",
                Icon = MessageBoxImage.Information,
                Buttons = [MessageBoxButtons.Ok()],
                IsSoundEnabled = false
            });
            return;
        }

        var messageBox = new MessageBoxModel
        {
            Text = $"Are you sure you want to update to build '{sha}' ({LatestBuild.CreatedAt.ToLocalTime():yyyy/MM/dd})?",
            Caption = "Update FModel-JP",
            Icon = MessageBoxImage.Question,
            Buttons = MessageBoxButtons.YesNo(),
            IsSoundEnabled = false
        };

        MessageBox.Show(messageBox);
        if (messageBox.Result != MessageBoxResult.Yes) return;

        IsDownloading = true;
        try
        {
            if (AutoUpdater.DownloadUpdate(new UpdateInfoEventArgs { DownloadURL = LatestBuild.BrowserDownloadUrl }))
            {
                Application.Current.Shutdown();
            }
        }
        catch (Exception e)
        {
            Log.Error("Failed to download the latest FModel-JP build: {msg}", e.Message);
            MessageBox.Show(e.Message, e.GetType().ToString(), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsDownloading = false;
        }
    }
}
