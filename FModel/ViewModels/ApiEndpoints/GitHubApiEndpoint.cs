using System;
using System.Linq;
using System.Threading.Tasks;
using FModel.Framework;
using FModel.ViewModels.ApiEndpoints.Models;
using RestSharp;

namespace FModel.ViewModels.ApiEndpoints;

public class GitHubApiEndpoint(RestClient client) : AbstractApiProvider(client)
{
    public async Task<GitHubCommit[]> GetCommitHistoryAsync(string branch = "dev", int page = 1, int limit = 30)
    {
        var request = new FRestRequest(Constants.GH_COMMITS_HISTORY);
        request.AddParameter("sha", branch);
        request.AddParameter("page", page);
        request.AddParameter("per_page", limit);
        var response = await _client.ExecuteAsync<GitHubCommit[]>(request).ConfigureAwait(false);
        return response.Data;
    }

    public async Task<GitHubRelease> GetReleaseAsync(string tag)
    {
        var request = new FRestRequest($"{Constants.GH_RELEASES}/tags/{tag}");
        var response = await _client.ExecuteAsync<GitHubRelease>(request).ConfigureAwait(false);
        return response.Data;
    }

    /// <summary>
    /// FModel-JP のリリース一覧を取得する。リリースノートの表示に使う。
    /// </summary>
    public async Task<GitHubRelease[]> GetJpReleasesAsync(int limit = 20)
    {
        var request = new FRestRequest(Constants.GH_JP_RELEASES);
        request.AddParameter("per_page", limit);
        var response = await _client.ExecuteAsync<GitHubRelease[]>(request).ConfigureAwait(false);
        return response.Data;
    }

    /// <summary>
    /// FModel-JP の指定タグのリリースを取得する。CI はバージョンごとのタグ (例: "4.5") に
    /// ビルド成果物 (&lt;version&gt;-&lt;sha&gt;.zip) を積んでいく。
    /// </summary>
    public async Task<GitHubRelease> GetJpReleaseAsync(string tag)
    {
        var request = new FRestRequest($"{Constants.GH_JP_RELEASES}/tags/{tag}");
        var response = await _client.ExecuteAsync<GitHubRelease>(request).ConfigureAwait(false);
        return response.Data;
    }

    /// <summary>
    /// FModel-JP の最新リリースを取得する。タグはバージョンごとに変わるため、
    /// まず /releases/latest を引き、取れなければ一覧の先頭 (下書き・プレリリースを除く) を使う。
    /// </summary>
    public async Task<GitHubRelease> GetJpLatestReleaseAsync()
    {
        var request = new FRestRequest($"{Constants.GH_JP_RELEASES}/latest");
        var response = await _client.ExecuteAsync<GitHubRelease>(request).ConfigureAwait(false);
        if (response.IsSuccessful && response.Data is { } latest) return latest;

        var releases = await GetJpReleasesAsync().ConfigureAwait(false);
        return releases?.FirstOrDefault(r => r is { Draft: false, PreRelease: false });
    }

    public async Task<Author> GetUserAsync(string username)
    {
        var request = new FRestRequest($"https://api.github.com/users/{Uri.EscapeDataString(username)}");
        var response = await _client.ExecuteAsync<Author>(request).ConfigureAwait(false);
        return response.Data;
    }
}
