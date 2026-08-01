using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Nanite;
using CUE4Parse.UE4.Assets.Exports.Texture;

namespace CUE4Parse_Conversion.Options
{

/// <summary>
/// FModel-JP の設定画面と、指定された CUE4Parse の旧 Exporter API をつなぐ最小互換層。
/// 実際の変換処理は CUE4Parse_Conversion.Exporter に委譲する。
/// </summary>
public enum EMeshFormat
{
    ActorX,
    Gltf2,
    Fbx,
    OBJ,
    UEFormat,
    USD
}

public sealed class ExportOptions(
    EMeshFormat meshFormat = EMeshFormat.USD,
    ENaniteMeshFormat naniteMeshFormat = ENaniteMeshFormat.OnlyNormalLODs,
    ETexturePlatform texturePlatform = ETexturePlatform.DesktopMobile,
    bool exportHdrTexturesAsHdr = true,
    bool exportMaterials = true,
    bool exportMorphTargets = true)
{
    public EMeshFormat MeshFormat { get; } = meshFormat;
    public ENaniteMeshFormat NaniteMeshFormat { get; } = naniteMeshFormat;
    public ETexturePlatform Platform { get; } = texturePlatform;
    public bool ExportHdrTexturesAsHdr { get; } = exportHdrTexturesAsHdr;
    public bool ExportMaterials { get; } = exportMaterials;
    public bool ExportMorphTargets { get; } = exportMorphTargets;
}

public enum ELandscapeFlags
{
    None = 0,
    Mesh = 1 << 0,
    Heightmap = 1 << 1,
    Weightmap = 1 << 2,
    All = Mesh | Heightmap | Weightmap
}
}

namespace CUE4Parse_Conversion
{

public sealed record ExportResult(bool Success, string ObjectPath, string? DiskFilePath = null, Exception? Error = null)
{
    public static ExportResult Failure(string objectPath, Exception error) => new(false, objectPath, null, error);
}

public readonly record struct ExportProgress(int Completed, int Total, ExportResult? LastResult = null)
{
    public double Percentage => Total <= 0 ? 0 : (double)Completed / Total;
    public string DisplayText => LastResult is { Success: false } ? "失敗" : $"{Completed}/{Total}";
}

/// <summary>
/// PR #689 のセッション UI を維持しつつ、変換は指定コミットの Exporter API で実行する。
/// </summary>
public sealed class ExportSession
{
    private readonly List<UObject> _exports = [];

    public ExportSession Add(UObject export)
    {
        // Unsupported export types must keep the old fallback behavior.
        _ = new Exporter(export);
        if (_exports.TrueForAll(item => !ReferenceEquals(item, export)))
            _exports.Add(export);
        return this;
    }

    public async Task<IReadOnlyList<ExportResult>> RunAsync(
        string baseDirectory,
        Options.ExportOptions options,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var directory = new DirectoryInfo(baseDirectory);
        directory.Create();
        var results = new List<ExportResult>(_exports.Count);
        var completed = 0;

        foreach (var export in _exports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExportResult result;
            try
            {
                var exporter = new Exporter(export, ToExporterOptions(options));
                var success = exporter.TryWriteToDir(directory, out _, out var savedFilePath);
                result = success
                    ? new ExportResult(true, export.GetPathName(), savedFilePath)
                    : ExportResult.Failure(export.GetPathName(), new IOException("Exporter returned false."));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                result = ExportResult.Failure(export.GetPathName(), error);
            }

            results.Add(result);
            progress?.Report(new ExportProgress(++completed, _exports.Count, result));
        }

        await Task.CompletedTask;
        return results;
    }

    private static ExporterOptions ToExporterOptions(Options.ExportOptions options)
    {
        var exporterOptions = new ExporterOptions
        {
            NaniteMeshFormat = options.NaniteMeshFormat,
            Platform = options.Platform,
            ExportHdrTexturesAsHdr = options.ExportHdrTexturesAsHdr,
            ExportMaterials = options.ExportMaterials,
            ExportMorphTargets = options.ExportMorphTargets
        };

        exporterOptions.MeshFormat = options.MeshFormat switch
        {
            Options.EMeshFormat.ActorX => Meshes.EMeshFormat.ActorX,
            Options.EMeshFormat.Gltf2 => Meshes.EMeshFormat.Gltf2,
            Options.EMeshFormat.OBJ => Meshes.EMeshFormat.OBJ,
            Options.EMeshFormat.UEFormat => Meshes.EMeshFormat.UEFormat,
            _ => throw new NotSupportedException($"Mesh format '{options.MeshFormat}' is not supported by the requested CUE4Parse revision.")
        };

        return exporterOptions;
    }
}
}
