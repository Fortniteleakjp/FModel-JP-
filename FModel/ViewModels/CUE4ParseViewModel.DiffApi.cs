using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FModel.Services;
using FModel.Views.Resources.Controls;
using FModel.Views.Resources.Controls.Diff;

namespace FModel.ViewModels;

public partial class CUE4ParseViewModel
{
    /// <summary>
    /// Extensions api.fortniteapi.com is able to export, everything else has no comparable json representation.
    /// </summary>
    private static readonly HashSet<string> _apiDiffableExtensions = new(StringComparer.OrdinalIgnoreCase) { "uasset", "umap" };

    /// <summary>
    /// Compares the local (current) state of <paramref name="assetPath"/> against the same asset in the previously
    /// shipped build, fetched from api.fortniteapi.com. Local content is shown on the right, the API one on the left.
    /// </summary>
    public async Task ShowApiAssetDiff(string assetPath, CancellationToken token = default)
    {
        if (Provider is null || !string.Equals(Provider.ProjectName, "FortniteGame", StringComparison.OrdinalIgnoreCase))
        {
            FLogger.Append(ELog.Warning, () =>
                FLogger.Text("Comparing against a previous version is only available for Fortnite.", Constants.WHITE, true));
            return;
        }

        var apiPath = assetPath.Replace('\\', '/');
        var extension = Path.GetExtension(apiPath).TrimStart('.');
        if (!_apiDiffableExtensions.Contains(extension))
        {
            FLogger.Append(ELog.Warning, () =>
                FLogger.Text($"The API can only export packages, '{extension}' is not supported.", Constants.WHITE, true));
            return;
        }

        var endpoint = ApplicationService.ApiEndpointView.FortniteApiCom;
        var version = await endpoint.GetPreviousVersionAsync(token).ConfigureAwait(false);
        if (version?.Id is null)
        {
            FLogger.Append(ELog.Error, () =>
                FLogger.Text("Could not resolve the previous Fortnite version from the API.", Constants.WHITE, true));
            return;
        }

        var result = await endpoint.GetExportAsync(apiPath, version.Id, token).ConfigureAwait(false);
        if (!result.IsSuccess && !result.IsNotFound)
        {
            FLogger.Append(ELog.Error, () =>
                FLogger.Text($"Version {version.Id} export failed for {Path.GetFileName(apiPath)}: {result.Error}", Constants.WHITE, true));
            return;
        }

        // a 404 means the asset did not exist yet, an empty left side then reads as "everything was added"
        var left = result.IsSuccess ? SplitIntoChunks(result.Json) : [];

        var localFile = TryGetFileByPathOrName(Provider.Files, assetPath);
        if (localFile is null && left.Count == 0)
        {
            FLogger.Append(ELog.Warning, () =>
                FLogger.Text($"{Path.GetFileName(apiPath)} exists neither locally nor in version {version.Id}.", Constants.WHITE, true));
            return;
        }

        List<string> right = localFile != null ? SplitIntoChunks(ExtractTextForDiff(Provider, localFile)) : [];

        var titleExtra = $"{Path.GetFileName(apiPath)} ({version.Id} → local)";

        await Application.Current.Dispatcher.Invoke(async () =>
        {
            object diffContent;
            if (AreTextsEqual(left, right))
            {
                diffContent = new SameDataMessage();
            }
            else
            {
                var viewer = new DataDiffViewer(left, right, extension);
                await viewer.Initialize();
                diffContent = viewer;
            }

            SetDiffTabContent(titleExtra, diffContent);
        });
    }
}
