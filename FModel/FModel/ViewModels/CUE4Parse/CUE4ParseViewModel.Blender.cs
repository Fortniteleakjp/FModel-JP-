using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Options;
using FModel.Settings;
using FModel.Services;
using Serilog;

namespace FModel.ViewModels.CUE4Parse;

public partial class CUE4ParseViewModel
{
    /// <summary>
    /// Exports every supported animation in a package as UEFormat, independently of the
    /// user's normal model export format. Files are written synchronously so Blender can
    /// consume them as soon as this method returns.
    /// </summary>
    public IReadOnlyList<string> ExportAnimationsForBlender(CancellationToken cancellationToken, GameFile entry)
    {
        var exportedFiles = new List<string>();
        if (!entry.IsUePackage)
            return exportedFiles;

        var outputRoot = Path.GetFullPath(UserSettings.Default.ModelDirectory);
        var package = Provider.LoadPackage(entry);

        for (var index = 0; index < package.ExportMapLength; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var export = new FPackageIndex(package, index + 1).ResolvedObject?.Object?.Value;
            if (export is not (UAnimSequence or UAnimMontage or UAnimComposite))
                continue;

            if (!ExportSessionService.TryExport(
                    export,
                    outputRoot,
                    out _,
                    out var outputPath,
                    cancellationToken: cancellationToken,
                    forcedMeshFormat: EMeshFormat.UEFormat))
                continue;

            exportedFiles.Add(outputPath);
            Log.Information("Exported UEFormat animation for Blender: {FilePath}", outputPath);
        }

        return exportedFiles;
    }
}


