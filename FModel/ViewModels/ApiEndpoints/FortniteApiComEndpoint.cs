using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FModel.Framework;
using FModel.ViewModels.ApiEndpoints.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;
using Serilog;

namespace FModel.ViewModels.ApiEndpoints;

/// <summary>
/// api.fortniteapi.com endpoints used to diff a local asset against the same asset in a previously shipped build.
/// </summary>
public class FortniteApiComEndpoint : AbstractApiProvider
{
    private const string _BASE_URL = "https://api.fortniteapi.com/v1";

    /// <summary>Exports can be several MB big, the default 5s request timeout is not enough for them.</summary>
    private static readonly TimeSpan _ExportTimeout = TimeSpan.FromSeconds(120);

    private ApiComVersion[] _versions;

    public FortniteApiComEndpoint(RestClient client) : base(client) { }

    public async Task<ApiComVersion[]> GetVersionsAsync(CancellationToken token)
    {
        var request = new FRestRequest($"{_BASE_URL}/versions")
        {
            Interceptors = [_interceptor]
        };
        var response = await _client.ExecuteAsync<ApiComVersion[]>(request, token).ConfigureAwait(false);
        Log.Information("[{Method}] [{Status}({StatusCode})] '{Resource}'", request.Method, response.StatusDescription, (int) response.StatusCode, response.ResponseUri?.OriginalString);
        return response.Data;
    }

    /// <summary>
    /// Version the current build is compared against: the one tagged <c>previous</c>, or the second most recent one.
    /// </summary>
    public async Task<ApiComVersion> GetPreviousVersionAsync(CancellationToken token)
    {
        _versions ??= await GetVersionsAsync(token).ConfigureAwait(false);
        if (_versions is not { Length: > 0 })
            return null;

        var ready = _versions.Where(v => v.IsReady && !string.IsNullOrEmpty(v.Id)).ToArray();
        if (ready.Length == 0)
            return null;

        return ready.FirstOrDefault(v => v.IsPrevious) ??
               (ready.Length > 1 ? ready[1] : ready[0]);
    }

    /// <summary>
    /// Exports <paramref name="path"/> as it was in <paramref name="version"/> and returns its json the same way
    /// the local provider serializes a package, so both sides of a diff line up.
    /// </summary>
    public async Task<ApiComExportResult> GetExportAsync(string path, string version, CancellationToken token)
    {
        var request = new FRestRequest($"{_BASE_URL}/export")
        {
            Interceptors = [_interceptor],
            Timeout = _ExportTimeout
        };
        request.AddQueryParameter("Path", path);
        request.AddQueryParameter("Version", version);

        var response = await _client.ExecuteAsync(request, token).ConfigureAwait(false);
        Log.Information("[{Method}] [{Status}({StatusCode})] '{Resource}'", request.Method, response.StatusDescription, (int) response.StatusCode, response.ResponseUri?.OriginalString);

        if (string.IsNullOrWhiteSpace(response.Content))
            return new ApiComExportResult { StatusCode = (int) response.StatusCode, Error = response.ErrorMessage ?? $"{(int) response.StatusCode} {response.StatusDescription}" };

        try
        {
            var root = JObject.Parse(response.Content);
            if (!response.IsSuccessful)
                return new ApiComExportResult { StatusCode = (int) response.StatusCode, Error = root.Value<string>("error_description") ?? root.Value<string>("error") ?? response.StatusDescription };

            if (root["jsonOutput"] is not { } output)
                return new ApiComExportResult { StatusCode = (int) response.StatusCode, Error = "The API response did not contain any export." };

            return new ApiComExportResult { StatusCode = (int) response.StatusCode, Json = output.ToString(Formatting.Indented) };
        }
        catch (JsonException e)
        {
            return new ApiComExportResult { StatusCode = (int) response.StatusCode, Error = e.Message };
        }
    }
}
