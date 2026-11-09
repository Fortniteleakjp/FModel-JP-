using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.VirtualFileSystem;
using Newtonsoft.Json;
using Serilog;

namespace FModel.Services;

/// <summary>
/// a package as the reference graph knows it
/// <see cref="Id"/> is only known for IoStore packages, pak packages are matched by <see cref="Path"/>
/// </summary>
public readonly record struct PackageKey(FPackageId? Id, string Path)
{
    /// <summary>
    /// the name of a zen package is the engine one ("/Game/Foo/Bar"), a pak package is named after its file
    /// </summary>
    public static PackageKey From(GameFile file, IPackage package) =>
        new(package is IoPackage ? FPackageId.FromName(package.Name) : null, file.Path);
}

/// <param name="ParentIndex">index, in the queried frontier, of the package this one references</param>
public readonly record struct PackageReferencer(GameFile File, PackageKey Key, int ParentIndex);

/// <summary>
/// package to package references
/// IoStore packages are resolved from the container headers (nothing is loaded), pak and loose packages from their import maps
/// </summary>
public sealed class PackageReferenceGraph
{
    private static readonly ConditionalWeakTable<AbstractVfsFileProvider, PackageReferenceGraph> _cache = new();
    private static readonly object _cacheLock = new();

    private readonly AbstractVfsFileProvider _provider;
    private readonly int _mountedCount;
    private readonly int _fileCount;
    private readonly object _legacyLock = new();
    private IoStoreReader[] _readers;
    private LegacyIndex _legacy;

    private PackageReferenceGraph(AbstractVfsFileProvider provider)
    {
        _provider = provider;
        _mountedCount = provider.MountedVfs.Count;
        _fileCount = provider.Files.Count;
    }

    /// <summary>
    /// the graph of the provider, rebuilt when archives were mounted since
    /// </summary>
    public static PackageReferenceGraph Get(AbstractVfsFileProvider provider)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(provider, out var graph) &&
                graph._mountedCount == provider.MountedVfs.Count && graph._fileCount == provider.Files.Count)
                return graph;

            graph = new PackageReferenceGraph(provider);
            _cache.AddOrUpdate(provider, graph);
            return graph;
        }
    }

    /// <summary>
    /// containers whose header can be read without a download
    /// the header of an on-demand container is fetched from the CDN when first read, a full scan would hang on it
    /// </summary>
    private IoStoreReader[] Readers => _readers ??= _provider.MountedVfs.OfType<IoStoreReader>()
        .Where(r => r is not IoStoreOnDemandReader)
        .Where(r =>
        {
            try
            {
                return r.ContainerHeader is { StoreEntries.Length: > 0 };
            }
            catch (Exception e)
            {
                Log.Debug(e, "Failed to read the container header of {Name}", r.Name);
                return false;
            }
        })
        .ToArray();

    private static bool IsIoStoreFile(GameFile file) => file is VfsEntry { Vfs: IoStoreReader };

    private static bool IsOnDemand(GameFile file) => file is VfsEntry { Vfs: IoStoreOnDemandReader };

    #region dependencies

    /// <summary>
    /// packages <paramref name="file"/> imports, the loaded <paramref name="package"/> gives its name and import map
    /// </summary>
    public List<GameFile> GetHardDependencies(GameFile file, IPackage package)
    {
        var result = new List<GameFile>();
        switch (package)
        {
            case IoPackage ioPackage:
            {
                if (file is not VfsEntry { Vfs: IoStoreReader reader }) break;

                // the header was read when the package was loaded, even for an on-demand container
                var header = reader.ContainerHeader;
                if (header is not { StoreEntries.Length: > 0 }) break;

                var index = Array.IndexOf(header.PackageIds, FPackageId.FromName(ioPackage.Name));
                if (index < 0) break;

                foreach (var imported in header.StoreEntries[index].ImportedPackages)
                {
                    if (TryResolve(imported, reader, out var dependency))
                        result.Add(dependency);
                }
                break;
            }
            case Package legacy:
            {
                foreach (var import in legacy.ImportMap)
                {
                    if (import.ClassName.Text != "Package") continue;
                    if (TryResolve(import.ObjectName.Text, out var dependency))
                        result.Add(dependency);
                }
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// soft object paths found in the exports, these aren't in the container headers or the import maps
    /// </summary>
    public List<GameFile> GetSoftDependencies(IPackage package)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // walk the json the properties tab shows without building the text
            JsonSerializer.CreateDefault().Serialize(new SoftPathCollector(paths), package.GetExports());
        }
        catch (Exception e)
        {
            Log.Debug(e, "Failed to read the soft references of {Name}", package.Name);
        }

        var result = new Dictionary<string, GameFile>(StringComparer.OrdinalIgnoreCase);
        var packageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            // "/Game/Foo/Bar.Bar:SubObject" or "/Game/Foo/Bar.0"
            var dot = path.IndexOf('.');
            var packageName = dot > 0 ? path[..dot] : path;
            if (packageName.Equals(package.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (packageName.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!packageNames.Add(packageName)) continue;

            if (TryResolve(packageName, out var file))
                result.TryAdd(file.Path, file);
        }

        return result.Values.ToList();
    }

    private bool TryResolve(FPackageId id, IoStoreReader fallbackReader, out GameFile file)
    {
        if (_provider.FilesById.TryGetValue(id, out file)) return true;
        return fallbackReader != null && fallbackReader.PackageIdIndex.TryGetValue(id, out file);
    }

    private bool TryResolve(string packageName, out GameFile file)
    {
        file = null;
        if (string.IsNullOrEmpty(packageName)) return false;

        // the id lookup avoids FixPath for the common IoStore case
        if (_provider.FilesById.TryGetValue(FPackageId.FromName(packageName), out file)) return true;

        var path = packageName.TrimStart('/');
        return _provider.TryGetGameFile(path + ".uasset", out file) || _provider.TryGetGameFile(path + ".umap", out file);
    }

    #endregion

    #region referencers

    /// <summary>
    /// packages importing any of <paramref name="frontier"/>, one pass over every container header
    /// a package importing several of them is returned once, attached to the first one
    /// </summary>
    public List<PackageReferencer> FindReferencers(IReadOnlyList<PackageKey> frontier, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var ids = new Dictionary<FPackageId, int>();
        var paths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < frontier.Count; i++)
        {
            if (frontier[i].Id is { } id) ids.TryAdd(id, i);
            if (!string.IsNullOrEmpty(frontier[i].Path)) paths.TryAdd(frontier[i].Path, i);
        }

        var found = new Dictionary<string, PackageReferencer>(StringComparer.OrdinalIgnoreCase);
        void Add(PackageReferencer referencer)
        {
            if (!found.TryGetValue(referencer.File.Path, out var existing) || referencer.ParentIndex < existing.ParentIndex)
                found[referencer.File.Path] = referencer;
        }

        if (ids.Count > 0)
        {
            var hits = new ConcurrentBag<(FPackageId Id, int Parent, IoStoreReader Reader)>();
            Parallel.ForEach(Readers, new ParallelOptions { CancellationToken = cancellationToken }, reader =>
            {
                var header = reader.ContainerHeader!;
                for (var i = 0; i < header.StoreEntries.Length; i++)
                {
                    var parent = int.MaxValue;
                    foreach (var imported in header.StoreEntries[i].ImportedPackages)
                    {
                        if (ids.TryGetValue(imported, out var index) && index < parent)
                            parent = index;
                    }

                    if (parent != int.MaxValue)
                        hits.Add((header.PackageIds[i], parent, reader));
                }
            });

            foreach (var (id, parent, reader) in hits)
            {
                if (!TryResolve(id, reader, out var file)) continue;
                // an asset patched into an on-demand container is still known locally, prefer that copy
                if (IsOnDemand(file) && reader.PackageIdIndex.TryGetValue(id, out var local)) file = local;
                Add(new PackageReferencer(file, new PackageKey(id, file.Path), parent));
            }
        }

        if (paths.Count > 0)
        {
            var legacy = GetLegacyIndex(progress, cancellationToken);
            foreach (var (path, parent) in paths)
            {
                if (!legacy.Referencers.TryGetValue(path, out var referencers)) continue;
                foreach (var index in referencers)
                {
                    var file = legacy.Packages[index];
                    Add(new PackageReferencer(file, new PackageKey(null, file.Path), parent));
                }
            }
        }

        return found.Values.OrderBy(r => r.ParentIndex).ThenBy(r => r.File.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private sealed class LegacyIndex
    {
        public required List<GameFile> Packages { get; init; }
        /// <summary>path of the imported package file -> indexes in <see cref="Packages"/></summary>
        public required Dictionary<string, List<int>> Referencers { get; init; }
    }

    /// <summary>
    /// pak and loose packages have no container header, their import maps are read once per provider
    /// </summary>
    private LegacyIndex GetLegacyIndex(IProgress<string> progress, CancellationToken cancellationToken)
    {
        lock (_legacyLock)
        {
            if (_legacy != null) return _legacy;

            var files = new Dictionary<string, GameFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in _provider.Files.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.IsUePackage && !IsIoStoreFile(file))
                    files.TryAdd(file.Path, file);
            }

            var list = files.Values.ToArray();
            var imports = new string[list.Length][];
            if (list.Length > 0)
            {
                var format = System.Windows.Application.Current?.TryFindResource("UI_RefChain_IndexingPackages") as string ?? "Indexing pak packages ({0}/{1})";
                var done = 0;
                Parallel.For(0, list.Length, new ParallelOptions { CancellationToken = cancellationToken }, i =>
                {
                    try
                    {
                        if (_provider.TryLoadPackage(list[i], out var package) && package is Package legacy)
                        {
                            imports[i] = legacy.ImportMap
                                .Where(import => import.ClassName.Text == "Package")
                                .Select(import => import.ObjectName.Text)
                                .ToArray();
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Debug(e, "Failed to read the imports of {Path}", list[i].Path);
                    }

                    var count = Interlocked.Increment(ref done);
                    if (count % 500 == 0) progress?.Report(string.Format(format, count, list.Length));
                });
            }

            // imports name the engine package ("/Game/Foo/Bar") while a pak package is named after its file,
            // the index is keyed by the file each import resolves to
            var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var packages = new List<GameFile>();
            var referencers = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < list.Length; i++)
            {
                if (imports[i] == null) continue;
                cancellationToken.ThrowIfCancellationRequested();

                var index = packages.Count;
                packages.Add(list[i]);
                foreach (var imported in imports[i])
                {
                    if (imported.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!resolved.TryGetValue(imported, out var path))
                        resolved[imported] = path = TryResolve(imported, out var file) ? file.Path : null;
                    if (path == null) continue;

                    if (!referencers.TryGetValue(path, out var indexes))
                        referencers[path] = indexes = [];
                    if (indexes.Count == 0 || indexes[^1] != index) indexes.Add(index);
                }
            }

            return _legacy = new LegacyIndex { Packages = packages, Referencers = referencers };
        }
    }

    #endregion

    /// <summary>
    /// receives the json of the exports and keeps the soft object paths, nothing is written
    /// </summary>
    private sealed class SoftPathCollector(HashSet<string> paths) : JsonWriter
    {
        private bool _capture;

        public override void WritePropertyName(string name)
        {
            base.WritePropertyName(name);
            _capture = name is "AssetPathName" or "ObjectPath";
        }

        public override void WriteStartObject()
        {
            base.WriteStartObject();
            _capture = false;
        }

        public override void WriteStartArray()
        {
            base.WriteStartArray();
            _capture = false;
        }

        public override void WriteValue(string value)
        {
            base.WriteValue(value);
            if (_capture && !string.IsNullOrEmpty(value) && value[0] == '/' && value != "None")
                paths.Add(value);
            _capture = false;
        }

        public override void Flush() { }
    }
}
