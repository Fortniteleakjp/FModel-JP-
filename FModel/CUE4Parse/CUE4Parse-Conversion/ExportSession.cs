using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse_Conversion.Exporters;
using CUE4Parse_Conversion.Options;
using CUE4Parse.UE4.Assets.Exports;
// L7 で Add(UObject) の dispatch を有効化する際に使用する型の名前空間（現状は未使用）。
using CUE4Parse.UE4.Assets.Exports.Actor;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Component.Landscape;
using CUE4Parse.UE4.Assets.Exports.Component.SplineMesh;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Rig;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.Engine.Animation;

namespace CUE4Parse_Conversion;

public sealed class ExportSession : INotifyPropertyChanged
{
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    private DirectoryInfo? _baseDirectory;
    internal DirectoryInfo BaseDirectory => _baseDirectory ?? throw new InvalidOperationException("Session is not currently running.");

    private ExportOptions? _options;
    internal ExportOptions Options => _options ?? throw new InvalidOperationException("Session is not currently running.");

    private int _totalQueued;
    public int TotalQueued => _totalQueued;

    // NOTE: レガシー CUE4Parse_Conversion.IExporter/ExporterBase との衝突を避けるため Exporters. で明示修飾する。
    private readonly ConcurrentQueue<Exporters.IExporter> _roots = new();
    private readonly ConcurrentDictionary<string, byte> _paths = new(StringComparer.OrdinalIgnoreCase);

    // PR #358 back-port (L6 骨格): 具象エクスポータ(L7)が揃うまで Add(UObject) の dispatch は保留する。
    // L7 で各エクスポータを移植したら、対応する case のコメントを順次解除する。
    public ExportSession Add(UObject export)
    {
        return export switch
        {
            UTexture texture => Add(new TextureExporter(texture)),
            UMaterialInterface material => Add(new MaterialExporter(material)),
            USkeletalMesh skeletalMesh => Add(new SkeletalMeshExporter(skeletalMesh)),
            UStaticMesh staticMesh => Add(new StaticMeshExporter(staticMesh)),
            USkeleton skeleton => Add(new SkeletonExporter(skeleton)),
            UPoseAsset poseAsset => Add(new PoseAssetExporter(poseAsset)),
            UAnimationAsset animation => Add(new AnimationExporter(animation)),
            UDNAAsset dna => Add(new DnaExporter(dna)),
            UWorld world => Add(new WorldExporter(world)),
            ALandscapeProxy landscape => Add(new LandscapeMeshExporter(landscape)),
            ULandscapeComponent landscape => Add(new LandscapeMeshExporter2(landscape)),
            USplineMeshComponent spline => Add(new SplineMeshExporter(spline)),
            _ => throw new NotSupportedException($"No exporter is available yet for export of type '{export.GetType().Name}' (new pipeline exporters are still being rolled out).")
        };
    }

    public ExportSession Add(Exporters.ExporterBase exporter)
    {
        if (!_paths.TryAdd(exporter.ObjectPath, 0)) return this;

        exporter._session = this;
        _roots.Enqueue(exporter);

        Interlocked.Increment(ref _totalQueued);
        OnPropertyChanged(nameof(TotalQueued));
        exporter.Log.Debug("Queued for export");
        return this;
    }

    public void Clear()
    {
        _roots.Clear();
        _paths.Clear();
        Interlocked.Exchange(ref _totalQueued, 0);
        OnPropertyChanged(nameof(TotalQueued));
    }

    public async Task<IReadOnlyList<ExportResult>> RunAsync(string baseDirectory, ExportOptions options, IProgress<ExportProgress>? progress = null, CancellationToken ct = default)
    {
        _baseDirectory = new DirectoryInfo(baseDirectory);
        _options = options;

        var completed = 0;
        var total = _totalQueued;
        var allResults = new ConcurrentQueue<ExportResult>();
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism, CancellationToken = ct };

        var current = new List<Exporters.IExporter>();
        while (true)
        {
            current.Clear();
            while (_roots.TryDequeue(out var exporter))
            {
                ct.ThrowIfCancellationRequested();
                current.Add(exporter);
            }
            if (current.Count == 0) break;

            await Parallel.ForEachAsync(current, parallelOptions, Process).ConfigureAwait(false);
        }

        _paths.Clear();
        progress?.Report(new ExportProgress(completed, total)); // that's kinda cheating but useful too

        _baseDirectory = null;
        _options = null;
        return [.. allResults];

        async ValueTask Process(Exporters.IExporter exporter, CancellationToken token)
        {
            var results = await exporter.ExportAsync(token).ConfigureAwait(false);

            Interlocked.Decrement(ref _totalQueued);
            OnPropertyChanged(nameof(TotalQueued));

            foreach (var result in results)
            {
                allResults.Enqueue(result);

                var c = Interlocked.Increment(ref completed);
                progress?.Report(new ExportProgress(c, total, result));
            }
        }
    }

    internal string ResolveOutputPath(string savePath, string ext, string? nameSuffix = null)
    {
        var fullPath = Path.Combine(BaseDirectory.FullName, savePath) + nameSuffix + '.' + ext.ToLower();
        var dir = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException($"Cannot determine directory for path: {fullPath}");
        Directory.CreateDirectory(dir);
        return fullPath.Replace('/', '\\');
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
