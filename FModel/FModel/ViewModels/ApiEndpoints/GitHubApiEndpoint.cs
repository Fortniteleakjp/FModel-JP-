using System.Linq;
using System.Threading.Tasks;
using FModel.Framework;
using FModel.ViewModels.ApiEndpoints.Models;
using RestSharp;
using Serilog;

namespace FModel.ViewModels.ApiEndpoints;

public class GitHubApiEndpoint : AbstractApiProvider
{
    // リリース情報は資産が数百件になり 5 秒では読み切れないことがあるため長めに取る
    private const int ReleaseTimeoutSeconds = 30;

    public GitHubApiEndpoint(RestClient client) : base(client) { }

    public async Task<GitHubCommit[]> GetCommitHistoryAsync(string branch = "main", int page = 1, int limit = 30)
    {
        var request = new FRestRequest(Constants.GH_COMMITS_HISTORY);
        request.AddParameter("sha", branch);
        request.AddParameter("page", page);
        request.AddParameter("per_page", limit);
        var response = await _client.ExecuteAsync<GitHubCommit[]>(request).ConfigureAwait(false);
        Log.Information("[{Method}] [{Status}({StatusCode})] '{Resource}' -> {Count} commit(s)",
            request.Method, response.StatusDescription, (int) response.StatusCode, response.ResponseUri?.OriginalString, response.Data?.Length ?? 0);
        return response.IsSuccessful ? response.Data : null;
    }

    public async Task<GitHubRelease> GetReleaseAsync(string tag)
    {
        var request = new FRestRequest($"{Constants.GH_RELEASES}/tags/{tag}", timeoutSeconds: ReleaseTimeoutSeconds);
        var response = await _client.ExecuteAsync<GitHubRelease>(request).ConfigureAwait(false);
        Log.Information("[{Method}] [{Status}({StatusCode})] '{Resource}' -> {Count} asset(s)",
            request.Method, response.StatusDescription, (int) response.StatusCode, response.ResponseUri?.OriginalString, response.Data?.Assets?.Length ?? 0);

        if (!response.IsSuccessful)
        {
            // 429/403 (レート制限) や 404 は本文が {"message": ...} なので Assets が null になる
            Log.Warning("GitHub のリリース '{Tag}' を取得できませんでした: {Error}", tag, response.ErrorMessage ?? response.Content);
            return null;
        }

        return response.Data;
    }

    /// <summary>
    /// 'qa' リリースに積まれている最新のビルド資産を返す。更新 API が別ラインのビルドに
    /// 上書きされていても、main のビルドを直接引き当てるために使う。
    /// </summary>
    public async Task<GitHubAsset> GetLatestBuildAssetAsync(string tag = "qa")
    {
        var release = await GetReleaseAsync(tag).ConfigureAwait(false);
        return release?.Assets?
            .Where(x => x != null && GitHubAsset.GetCommitSha(x.Name) != null)
            .MaxBy(x => x.CreatedAt);
    }

    public GitHubAsset GetLatestBuildAsset(string tag = "qa")
    {
        return GetLatestBuildAssetAsync(tag).GetAwaiter().GetResult();
    }
}
