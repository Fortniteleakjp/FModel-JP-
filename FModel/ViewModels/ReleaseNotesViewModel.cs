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
    /// <summary>FModel-JP の最新リリースにある一番新しいビルド。</summary>
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

    /// <summary>
    /// 成果物名 "&lt;version&gt;-&lt;sha&gt;.zip" から sha を取り出す。
    /// 旧形式の "&lt;sha&gt;.zip" もそのまま扱えるようにしている。
    /// </summary>
    private string LatestBuildSha
    {
        get
        {
            if (LatestBuild is null) return string.Empty;

            var name = Path.GetFileNameWithoutExtension(LatestBuild.Name);
            var separator = name.LastIndexOf('-');
            return separator < 0 ? name : name[(separator + 1)..];
        }
    }

    /// <summary>成果物名 "&lt;version&gt;-&lt;sha&gt;.zip" から version を取り出す。旧形式なら空。</summary>
    private string LatestBuildVersion
    {
        get
        {
            if (LatestBuild is null) return string.Empty;

            var name = Path.GetFileNameWithoutExtension(LatestBuild.Name);
            var separator = name.LastIndexOf('-');
            return separator < 0 ? string.Empty : name[..separator];
        }
    }

    public string LatestBuildInfo
    {
        get
        {
            if (LatestBuild is null) return string.Empty;

            var sha = LatestBuildSha;
            if (sha.Length > 7) sha = sha[..7];

            var version = LatestBuildVersion;
            var size = $"{LatestBuild.Size / 1024d / 1024d:0.0} MB";
            var info = $"{sha} · {LatestBuild.CreatedAt.ToLocalTime():yyyy/MM/dd} · {size}";
            return string.IsNullOrEmpty(version) ? info : $"v{version} · {info}";
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
            var release = await ApplicationService.ApiEndpointView.GitHubApi.GetJpLatestReleaseAsync();
            if (release?.Assets is not { Length: > 0 }) return;

            // 同じリリースに複数ビルドの zip が積まれるため、一番新しい zip を選ぶ
            var zips = release.Assets.Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (zips.Length == 0) return;

            LatestBuild = zips.MaxBy(a => a.CreatedAt);
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
