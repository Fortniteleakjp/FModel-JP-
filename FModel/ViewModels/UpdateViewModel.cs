using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
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
        Commits = [];
        CommitsView = new ListCollectionView(Commits)
        {
            GroupDescriptions = { new PropertyGroupDescription("Commit.Author.Date", new DateTimeToDateConverter()) }
        };

        if (UserSettings.Default.NextUpdateCheck < DateTime.Now)
            RemindMeCommand.Execute(this, null);
    }

    public async Task LoadAsync()
    {
        // 更新サーバーの情報を優先する。main と同じく Version / DownloadUrl が更新元の基準。
        var assets = new List<GitHubAsset>();
        var info = _apiEndpointView.FModelApi.CurrentUpdateInfo;
        var apiCommitSha = ExtractCommitSha(info?.Version);
        if (!string.IsNullOrWhiteSpace(info?.DownloadUrl) && apiCommitSha != null)
        {
            assets.Add(GitHubAsset.CreateDirect(
                $"{apiCommitSha}.zip",
                info.DownloadUrl,
                DateTime.UtcNow));
        }

        // 更新 API の情報が無い場合だけ GitHub Releases をフォールバックとして使う。
        if (assets.Count == 0)
        {
            var release = await _apiEndpointView.GitHubApi.GetJpLatestReleaseAsync();
            var releaseAssets = release?.Assets?
                .Where(x => x.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.CreatedAt)
                .ToArray() ?? [];
            assets.AddRange(releaseAssets);
        }

        // 一番新しいビルドの sha を起点に履歴を引くことで、ビルド元のブランチ名に依存しない
        var commits = await _apiEndpointView.GitHubApi.GetJpCommitHistoryAsync(assets.Count > 0 ? GetAssetSha(assets[0].Name) : null);
        if (commits is { Length: > 0 })
            Commits.AddRange(commits);

        try
        {
            _ = LoadCoAuthors();
            LinkAssets(assets.ToArray());
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
                if (!commit.Commit.Message.Contains("Co-authored-by", StringComparison.OrdinalIgnoreCase))
                    continue;

                var regex = GetCoAuthorRegex();
                var matches = regex.Matches(commit.Commit.Message);
                if (matches.Count == 0) continue;

                commit.Commit.Message = regex.Replace(commit.Commit.Message, string.Empty).Trim();

                coAuthorMap[commit] = [];
                foreach (Match match in matches)
                {
                    if (match.Groups.Count < 3) continue;

                    var username = match.Groups[1].Value;
                    if (username.Equals("Asval", StringComparison.OrdinalIgnoreCase))
                    {
                        username = "4sval"; // found out the hard way co-authored usernames can't be trusted
                    } else if (username.Equals("Krowe Moh", StringComparison.OrdinalIgnoreCase))
                    {
                        username = "Krowe-moh";
                    }

                    coAuthorMap[commit].Add(username);
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
                    // Ignore
                }
            }

            foreach (var (commit, usernames) in coAuthorMap)
            {
                var coAuthors = usernames
                    .Where(authorCache.ContainsKey)
                    .Select(username => authorCache[username])
                    .ToArray();

                if (coAuthors.Length > 0)
                    commit.CoAuthors = coAuthors;
            }
        });
    }

    /// <summary>
    /// リリース資産を対応するコミットに紐付ける。履歴に無いビルドは資産だけの項目として足す。
    /// </summary>
    private void LinkAssets(GitHubAsset[] assets)
    {
        for (var i = 0; i < assets.Length; i++)
        {
            var asset = assets[i];
            asset.IsLatest = i == 0;

            var sha = GetAssetSha(asset.Name);
            if (string.IsNullOrEmpty(sha)) continue;

            var commit = Commits.FirstOrDefault(x => x.Sha == sha);
            if (commit != null)
            {
                commit.Asset = asset;
                continue;
            }

            Commits.Add(new GitHubCommit
            {
                Sha = sha,
                Commit = new Commit
                {
                    Message = $"FModel-JP ({sha[..Math.Min(7, sha.Length)]})",
                    Author = new Author { Name = asset.Uploader?.Login, Date = asset.CreatedAt }
                },
                Author = asset.Uploader,
                Asset = asset
            });
        }
    }

    /// <summary>
    /// CI が積む資産名 "&lt;version&gt;-&lt;sha&gt;.zip" から sha を取り出す。
    /// </summary>
    private static string GetAssetSha(string assetName)
    {
        var name = assetName.SubstringBeforeLast(".zip");
        return name.Contains('-') ? name.SubstringAfterLast('-') : name;
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

    [GeneratedRegex(@"Co-authored-by:\s*(.+?)\s*<(.+?)>", RegexOptions.IgnoreCase | RegexOptions.Multiline, "en-US")]
    private static partial Regex GetCoAuthorRegex();
}
