using System;
using System.IO;
using System.Linq;
using System.Threading;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Options;
using FModel.Settings;
using Serilog;

namespace FModel.Services;

/// <summary>FModel内のすべてのアセット書き出しを新ExportSessionへ集約する。</summary>
public static class ExportSessionService
{
    public static bool TryExport(
        UObject export,
        string outputDirectory,
        out string label,
        out string savedFilePath,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool forceUsd = false,
        EMeshFormat? forcedMeshFormat = null)
    {
        label = export.Name;
        savedFilePath = string.Empty;

        try
        {
            var session = new ExportSession();
            session.Add(export);

            var configured = UserSettings.Default.ExportOptions;
            var options = forceUsd || forcedMeshFormat.HasValue
                ? new ExportOptions(
                    meshFormat: forceUsd ? EMeshFormat.USD : forcedMeshFormat!.Value,
                    naniteMeshFormat: configured.NaniteMeshFormat,
                    texturePlatform: configured.TexturePlatform,
                    textureFormat: configured.TextureFormat,
                    exportHdrTexturesAsHdr: configured.ExportHdrTexturesAsHdr,
                    exportMaterials: configured.ExportMaterials,
                    exportMorphTargets: configured.ExportMorphTargets,
                    socketFormat: configured.SocketFormat,
                    compressionFormat: configured.CompressionFormat)
                : configured;

            var results = session.RunAsync(outputDirectory, options, progress, cancellationToken)
                .GetAwaiter()
                .GetResult();
            savedFilePath = results.FirstOrDefault(result => result.Success)?.DiskFilePath ?? string.Empty;
            return !string.IsNullOrEmpty(savedFilePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "New export pipeline failed for {Name}", export.Name);
            return false;
        }
    }
}
