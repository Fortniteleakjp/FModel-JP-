using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Animations;
using CUE4Parse_Conversion.UEFormat.Enums;
using FModel.Settings;
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
        var options = UserSettings.Default.ExportOptions;
        options.AnimFormat = EAnimFormat.UEFormat;

        for (var index = 0; index < package.ExportMapLength; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var export = new FPackageIndex(package, index + 1).ResolvedObject?.Object?.Value;
            var exporter = export switch
            {
                UAnimSequence sequence => new AnimExporter(sequence, options),
                UAnimMontage montage => new AnimExporter(montage, options),
                UAnimComposite composite => new AnimExporter(composite, options),
                _ => null
            };

            if (exporter is null)
                continue;

            foreach (var animation in exporter.AnimSequences)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = animation.FileName
                    .TrimStart('/', '\\')
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                var outputPath = Path.GetFullPath(Path.Combine(outputRoot, relativePath));
                var pathFromRoot = Path.GetRelativePath(outputRoot, outputPath);
                if (pathFromRoot.Equals("..", StringComparison.Ordinal) ||
                    pathFromRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Invalid UEFormat output path: {animation.FileName}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllBytes(outputPath, animation.FileData);
                exportedFiles.Add(outputPath);
                Log.Information("Exported UEFormat animation for Blender: {FilePath}", outputPath);
            }
        }

        return exportedFiles;
    }
}
