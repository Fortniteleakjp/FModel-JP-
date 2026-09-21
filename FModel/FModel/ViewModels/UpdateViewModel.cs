using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using CUE4Parse.Utils;
using FModel.Framework;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels.ApiEndpoints.Models;
using FModel.ViewModels.Commands;
using FModel.Views.Resources.Converters;

namespace FModel.ViewModels;

public class UpdateViewModel : ViewModel
{
    private ApiEndpointViewModel _apiEndpointView => ApplicationService.ApiEndpointView;

    private RemindMeCommand _remindMeCommand;
    public RemindMeCommand RemindMeCommand => _remindMeCommand ??= new RemindMeCommand(this);

    public RangeObservableCollection<GitHubCommit> Commits { get; }
    public ICollectionView CommitsView { get; }

    public UpdateViewModel()
    {
        Commits = new RangeObservableCollection<GitHubCommit>();
        CommitsView = new ListCollectionView(Commits)
        {
            GroupDescriptions = { new PropertyGroupDescription("Commit.Author.Date", new DateTimeToDateConverter()) }
        };

        if (UserSettings.Default.NextUpdateCheck < DateTime.Now)
            RemindMeCommand.Execute(this, null);
    }

    public async Task Load()
    {
        var commits = await _apiEndpointView.GitHubApi.GetCommitHistoryAsync();
        if (commits != null)
            Commits.AddRange(commits);

        var qa = await _apiEndpointView.GitHubApi.GetReleaseAsync("qa");
        var assets = qa?.Assets?
            .Where(x => x != null)
            .OrderByDescending(x => x.CreatedAt)
            .ToList() ?? [];

        if (assets.Count == 0)
        {
            var info = _apiEndpointView.FModelApi.CurrentUpdateInfo;
            var commitSha = ExtractCommitSha(info?.Version);
            if (!string.IsNullOrWhiteSpace(info?.DownloadUrl) && commitSha != null)
            {
                assets.Add(GitHubAsset.CreateDirect(
                    $"{commitSha}.zip",
                    info.DownloadUrl,
                    DateTime.UtcNow));
            }
        }

        if (assets.Count == 0) return;

        for (var i = 0; i < assets.Count; i++)
        {
            var asset = assets[i];
            asset.IsLatest = i == 0;

            var commitSha = asset.Name.SubstringBeforeLast(".zip");
            var commit = Commits.FirstOrDefault(x => x.Sha == commitSha);
            if (commit != null)
            {
                commit.Asset = asset;
            }
            else
            {
                Commits.Add(new GitHubCommit
                {
                    Sha = commitSha,
                    Commit = new Commit
                    {
                        Message = $"FModel ({commitSha[..7]})",
                        Author = new Author { Name = asset.Uploader.Login, Date = asset.CreatedAt }
                    },
                    Author = asset.Uploader,
                    Asset = asset
                });
            }
        }
    }

    private static string ExtractCommitSha(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var separator = version.LastIndexOf('-');
        if (separator < 0 || separator >= version.Length - 1)
            return null;

        var commitSha = version[(separator + 1)..].Trim();
        return commitSha.Length >= 7 ? commitSha : null;
    }

    public void DownloadLatest()
    {
        Commits.FirstOrDefault(x => x.IsDownloadable && x.Asset.IsLatest)?.Download();
    }
}
