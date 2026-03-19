using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.MappingsProvider;
using FModel.Settings;
using FModel.Views.Resources.Controls;
using Serilog;

namespace FModel.ViewModels.CUE4Parse;

public partial class CUE4ParseViewModel
{
    public async Task InitAllMappings(bool force = false)
    {
        foreach ((AbstractVfsFileProvider provider, EndpointSettings endpoint) in ProvidersWithEndpoints(EEndpointType.Mapping))
        {
            await InitMappingsForProvider(provider, endpoint, force);
        }
    }

    /// <summary>
    /// Loads mappings for a given provider and endpoint.
    /// </summary>
    private Task InitMappingsForProvider(AbstractVfsFileProvider provider, EndpointSettings endpoint,
        bool force = false)
    {
        if (provider == null || !(endpoint.Overwrite || endpoint.IsValid))
        {
            if (provider != null)
                provider.MappingsContainer = null;
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            var l = ELog.Information;
            if (endpoint.Overwrite && File.Exists(endpoint.FilePath))
            {
                provider.MappingsContainer = new FileUsmapTypeMappingsProvider(endpoint.FilePath);
            }
            else if (endpoint.IsValid)
            {
                var mappingsFolder = Path.Combine(UserSettings.Default.OutputDirectory, ".data");
                NormalizeMappingsEndpoint(endpoint);
                var mappings = _apiEndpointView.DynamicApi.GetMappings(CancellationToken.None, endpoint.Url, endpoint.Path);
                if (mappings is { Length: > 0 })
                {
                    foreach (var mapping in mappings)
                    {
                        if (!mapping.IsValid)
                            continue;

                        var mappingPath = Path.Combine(mappingsFolder, mapping.FileName);
                        if (force || !File.Exists(mappingPath))
                        {
                            _apiEndpointView.DownloadFile(mapping.Url, mappingPath);
                        }

                        var candidateMappings = new FileUsmapTypeMappingsProvider(mappingPath);
                        if (IsBrokenFortniteSkeletonMappings(provider, candidateMappings))
                        {
                            Log.Warning("Skipping invalid Fortnite mappings '{FileName}' (Skeleton has no properties)", mapping.FileName);
                            continue;
                        }

                        provider.MappingsContainer = candidateMappings;
                        break;
                    }
                }

                if (provider.MappingsContainer == null)
                {
                    var usmaps = new DirectoryInfo(mappingsFolder).GetFiles("*.usmap")
                        .OrderByDescending(f => f.LastWriteTime);

                    foreach (var usmapInfo in usmaps)
                    {
                        var candidateMappings = new FileUsmapTypeMappingsProvider(usmapInfo.FullName);
                        if (IsBrokenFortniteSkeletonMappings(provider, candidateMappings))
                        {
                            Log.Warning("Skipping local invalid Fortnite mappings '{FileName}' (Skeleton has no properties)", usmapInfo.Name);
                            continue;
                        }

                        provider.MappingsContainer = candidateMappings;
                        l = ELog.Warning;
                        break;
                    }

                    if (provider.MappingsContainer == null)
                        return;
                }
            }

            if (provider.MappingsContainer is not FileUsmapTypeMappingsProvider m) return;

            Log.Information($"Mappings pulled from '{m.FileName}'");
            FLogger.Append(l,
                () => FLogger.Text($"Mappings pulled from '{m.FileName}'", Constants.WHITE, true));
        });
    }
}
