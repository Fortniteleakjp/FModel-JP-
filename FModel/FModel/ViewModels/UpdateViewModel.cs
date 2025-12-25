using System;
using System.ComponentModel;
using System.Linq;
using System.Collections.Generic; // Dictionary, HashSet
using System.Text.RegularExpressions; // Match
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

public partial class UpdateViewModel : ViewModel
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

    public async Task LoadAsync()
    {
        var commits = await _apiEndpointView.GitHubApi.GetCommitHistoryAsync("main");
        if (commits == null || commits.Length == 0)
            return;

        Commits.AddRange(commits);

        try
        {
            _ = LoadCoAuthors();
            _ = LoadAssets();
        }
        catch
        {
            //
        }
    }

    private Task LoadCoAuthors()
    {
        return Task.Run(async () =>
        {
            var coAuthorMap = new Dictionary<GitHubCommit, HashSet<string>>();
            foreach (var commit in Commits)
            {
                if (!commit.Commit.Message.Contains("Co-authored-by"))
                    continue;

                var regex = GetCoAuthorRegex();
                var matches = regex.Matches(commit.Commit.Message);
                if (matches.Count == 0) continue;

                commit.Commit.Message = regex.Replace(commit.Commit.Message, string.Empty).Trim();

                coAuthorMap[commit] = new HashSet<string>();
                foreach (Match match in matches)
                {
                    if (match.Groups.Count < 3) continue;
                    coAuthorMap[commit].Add(match.Groups[1].Value);
                }
            }

            if (coAuthorMap.Count == 0) return;

            var uniqueUsernames = coAuthorMap.Values.SelectMany(x => x).Distinct().ToArray();
            var authorCache = new Dictionary<string, Author>();
            foreach (var username in uniqueUsernames)
            {
                try
                {
                    var author = await _apiEndpointView.GitHubApi.GetUserAsync(username);
                    if (author != null)
                        authorCache[username] = author;
                }
                catch
                {
                    //
                }
            }

            foreach (KeyValuePair<GitHubCommit, HashSet<string>> pair in coAuthorMap)
            {
                var commit = pair.Key;
                var usernames = pair.Value;
                var coAuthors = usernames
                    .Where(username => authorCache.ContainsKey(username))
                    .Select(username => authorCache[username])
                    .ToArray();

                if (coAuthors.Length > 0)
                    commit.CoAuthors = coAuthors;
            }
        });
    }

    private Task LoadAssets()
    {
        return Task.Run(async () =>
        {
            var qa = await _apiEndpointView.GitHubApi.GetReleaseAsync("qa");
            var assets = qa.Assets.OrderByDescending(x => x.CreatedAt).ToList();

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
        });
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"Co-authored-by:\s*(.+?)\s*<(.+?)>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline, "en-US")]
    private static partial System.Text.RegularExpressions.Regex GetCoAuthorRegex();

    public void DownloadLatest()
    {
        Commits.FirstOrDefault(x => x.IsDownloadable && x.Asset.IsLatest)?.Download();
    }
}
