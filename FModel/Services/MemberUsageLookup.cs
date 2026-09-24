using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.VirtualFileSystem;
using FModel.Framework;
using FModel.ViewModels;
using Serilog;

namespace FModel.Services;

public enum EMemberKind
{
    Any,
    Function,
    Variable
}

public enum EMemberUsageKind
{
    /// <summary>the function is called</summary>
    Call,
    /// <summary>the variable is read</summary>
    Read,
    /// <summary>the variable is written</summary>
    Write,
    /// <summary>the function is bound to a delegate by name (Create Event, Bind Event)</summary>
    Bind,
    /// <summary>the blueprint implements the function or event itself</summary>
    Override,
    /// <summary>the package references the name, the bytecode wasn't read to tell how</summary>
    Reference
}

public enum EMemberLookupNote
{
    /// <summary>"Serialize Script Bytecode" is off, packages are only matched on their names and imports</summary>
    ScriptDataNotRead,
    /// <summary>the owner is a blueprint, only the packages importing it were searched</summary>
    OwnerImportersOnly,
    /// <summary>virtual calls and delegate bindings don't name their class, some rows only match by name</summary>
    NameOnlyMatches,
    /// <summary>too many results, the list was cut</summary>
    Truncated,
    /// <summary>packages streamed on demand from the CDN (IoStore on-demand) aren't downloaded to be searched</summary>
    OnDemandSkipped,
    /// <summary>blueprints importing on-demand packages not downloaded yet were only matched on their names and imports</summary>
    OnDemandImports
}

/// <summary>
/// what to look for: a member name, optionally the class it belongs to
/// </summary>
public sealed class MemberUsageQuery
{
    public required string Name { get; init; }
    /// <summary>class declaring the member, Actor or BP_Foo_C, null for any</summary>
    public string OwnerClass { get; init; }
    /// <summary>package of the owner, /Script/Engine or /Game/Path/BP_Foo, null when unknown</summary>
    public string OwnerPackage { get; init; }
    public EMemberKind Kind { get; init; }
    /// <summary>only search packages under this folder, null for everything</summary>
    public string Scope { get; init; }

    public bool IsBlueprintOwner => OwnerPackage != null && !OwnerPackage.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase);

    public string Display => OwnerClass != null ? $"{OwnerClass}.{Name}" : Name;

    /// <summary>
    /// Name, Class:Name, Class.Name, /Script/Engine.Actor:Name or /Game/Path/BP_Foo.BP_Foo_C:Name
    /// </summary>
    public static bool TryParse(string text, EMemberKind kind, string scope, out MemberUsageQuery query)
    {
        query = null;
        text = text?.Trim().Trim('\'', '"');
        if (string.IsNullOrEmpty(text)) return false;

        string ownerPackage = null, ownerClass = null, name;
        text = text.Replace("::", ":");
        if (text.StartsWith('/'))
        {
            // object path: /Package/Path.Class:Member
            var colon = text.LastIndexOf(':');
            if (colon < 0) return false;
            name = text[(colon + 1)..];
            var owner = text[..colon];
            var dot = owner.LastIndexOf('.');
            if (dot < 0) return false;
            ownerPackage = owner[..dot];
            ownerClass = owner[(dot + 1)..];
        }
        else
        {
            var separator = text.LastIndexOfAny([':', '.']);
            name = separator >= 0 ? text[(separator + 1)..] : text;
            ownerClass = separator > 0 ? text[..separator] : null;
        }

        // blueprint members may have spaces, "Set Timeline Playrates"
        name = name.Trim();
        if (name.Length == 0) return false;

        scope = scope?.Trim().Replace('\\', '/').Trim('/');
        query = new MemberUsageQuery
        {
            Name = name,
            OwnerClass = string.IsNullOrWhiteSpace(ownerClass) ? null : ownerClass.Trim(),
            OwnerPackage = string.IsNullOrWhiteSpace(ownerPackage) ? null : ownerPackage,
            Kind = kind,
            Scope = string.IsNullOrEmpty(scope) ? null : scope
        };
        return true;
    }

    public override string ToString() => OwnerPackage != null && OwnerClass != null
        ? $"{OwnerPackage}.{OwnerClass}:{Name}"
        : OwnerClass != null ? $"{OwnerClass}:{Name}" : Name;
}

public sealed class MemberUsage
{
    public required GameFile File { get; init; }
    public required EMemberUsageKind Kind { get; init; }
    /// <summary>member as the bytecode references it, Actor.K2_GetActorLocation, ?.Foo when the class isn't named</summary>
    public required string Member { get; init; }
    /// <summary>where it is used: the function, or the event of the event graph</summary>
    public required string UsedIn { get; init; }
    /// <summary>function to open in the graph</summary>
    public string FunctionName { get; init; }
    /// <summary>statement to center in the graph, -1 for none</summary>
    public int Offset { get; init; } = -1;
    public int Count { get; set; }
    /// <summary>the class of the member was checked, false when only its name matched</summary>
    public bool IsExact { get; init; }

    public string Name => File.NameWithoutExtension;
    public string Path => File.Path;
}

/// <summary>the index of the packages holding bytecode, kept for <see cref="MemberUsageLookup.CacheLifetime"/></summary>
public sealed record MemberUsageCacheInfo(DateTime CreatedAt, DateTime ExpiresAt, int PackageCount);

public sealed class MemberUsageResult
{
    public required MemberUsageQuery Query { get; init; }
    public List<MemberUsage> Usages { get; } = [];
    public HashSet<EMemberLookupNote> Notes { get; } = [];
    /// <summary>packages read to look for the name</summary>
    public int ScannedCount { get; set; }
    /// <summary>packages whose bytecode was searched</summary>
    public int CandidateCount { get; set; }
    /// <summary>on-demand packages left out, reading them would download each one</summary>
    public int OnDemandSkippedCount { get; set; }
    /// <summary>candidates whose bytecode wasn't read, loading their functions would download the on-demand packages they import</summary>
    public int OnDemandImportCount { get; set; }
}

/// <summary>
/// finds the blueprints that call a function or read / write a variable, "Find References" of the editor:
/// - every package in scope is read raw and searched for the member's name and, for a native function, the hash of its
///   script import (zen packages don't keep the names of what they import), which rules out most of the game at IO speed
/// - the packages left are loaded and the bytecode of their functions is walked to tell how and where the member is used
/// a blueprint owner narrows the first step to the packages importing it, from the IoStore container headers
/// </summary>
public static class MemberUsageLookup
{
    private const int MaxUsages = 20000;
    private const int MaxScriptHashes = 64;

    /// <summary>how long the index of the packages holding bytecode is reused, across restarts too</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(1);

    private static string _cacheDirectory;

    /// <summary>where the index is saved, one file per build</summary>
    public static string CacheDirectory
    {
        get => _cacheDirectory ??= System.IO.Path.Combine(CacheManager.DataDirectory, "member-usage");
        set => _cacheDirectory = value;
    }

    /// <summary>the index of the loaded build, null when the next search over the whole game builds it (reads the disk, call off the UI thread)</summary>
    public static MemberUsageCacheInfo GetCacheInfo(AbstractVfsFileProvider provider) => BytecodeIndex.Info(provider);

    /// <summary>forgets the index of every build, in memory and on disk</summary>
    public static void ClearCache() => BytecodeIndex.Clear();

    public static MemberUsageResult Run(AbstractVfsFileProvider provider, MemberUsageQuery query, IProgress<(int Done, int Total)> progress, CancellationToken cancellationToken)
    {
        var result = new MemberUsageResult { Query = query };
        var target = Target.Prepare(provider, query);

        // step 1: what might reference it
        var candidates = new ConcurrentBag<GameFile>();
        IReadOnlyCollection<GameFile> packages;
        List<GameFile> importers = null;
        if (query.IsBlueprintOwner && TryGetImporters(provider, query.OwnerPackage, out importers))
        {
            // everything using a blueprint's member imports its package
            packages = importers.Where(f => InScope(f, query.Scope) && !IsOnDemand(f)).ToList();
            result.OnDemandSkippedCount = importers.Count(f => InScope(f, query.Scope) && IsOnDemand(f));
            result.Notes.Add(EMemberLookupNote.OwnerImportersOnly);
            foreach (var file in packages) candidates.Add(file);
            result.ScannedCount = packages.Count;
        }
        else
        {
            (packages, result.OnDemandSkippedCount) = BytecodeIndex.Packages(provider, query.Scope);
            var done = 0;
            var names = SearchValues.Create([query.Name], StringComparison.OrdinalIgnoreCase);
            var bytecodePackages = BytecodeIndex.CanBuild(provider, query.Scope) ? new ConcurrentBag<GameFile>() : null;

            Parallel.ForEach(packages, new ParallelOptions { CancellationToken = cancellationToken }, file =>
            {
                try
                {
                    var data = file.Read();
                    // loose .uasset of a pak keep the names of what they import, no marker to look for
                    var hasBytecode = target.FunctionClassHash is not { } marker || file is not FIoStoreEntry || Contains(data, marker);
                    if (hasBytecode)
                    {
                        bytecodePackages?.Add(file);
                        if (ContainsName(data, names) || target.ScriptHashes.Any(h => Contains(data, h)))
                            candidates.Add(file);
                    }
                }
                catch (Exception e)
                {
                    // kept in the index, a read that failed once must not hide it until the index expires
                    bytecodePackages?.Add(file);
                    Log.Debug(e, "Failed to read {Path} while looking for {Member}", file.Path, query.Display);
                }

                var count = Interlocked.Increment(ref done);
                if ((count & 1023) == 0) progress?.Report((count, packages.Count));
            });

            if (bytecodePackages != null) BytecodeIndex.Store(provider, bytecodePackages);
            result.ScannedCount = packages.Count;
        }

        progress?.Report((result.ScannedCount, result.ScannedCount));
        result.CandidateCount = candidates.Count;
        if (!provider.ReadScriptData) result.Notes.Add(EMemberLookupNote.ScriptDataNotRead);

        // step 2: how they use it
        var usages = new ConcurrentBag<MemberUsage>();
        var onDemandImports = new OnDemandImports(provider);
        var onDemandImportCount = 0;
        Parallel.ForEach(candidates, new ParallelOptions { CancellationToken = cancellationToken }, file =>
        {
            try
            {
                if (!provider.TryLoadPackage(file, out var package)) return;

                IEnumerable<MemberUsage> found;
                if (!provider.ReadScriptData)
                {
                    found = target.MatchHeader(file, package);
                }
                else if (onDemandImports.WouldDownload(file, package))
                {
                    // a download per import, many timing out when the CDN is slow: the header tells enough to list it
                    Interlocked.Increment(ref onDemandImportCount);
                    found = target.MatchHeader(file, package);
                }
                else
                {
                    found = new PackageSearch(file, package, target).Run(cancellationToken);
                }

                foreach (var usage in found) usages.Add(usage);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Log.Debug(e, "Failed to search {Path} for {Member}", file.Path, query.Display);
            }
        });

        var sorted = usages
            .OrderBy(u => u.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(u => u.UsedIn, StringComparer.OrdinalIgnoreCase)
            .ThenBy(u => u.Kind)
            .ToList();
        if (sorted.Count > MaxUsages)
        {
            sorted.RemoveRange(MaxUsages, sorted.Count - MaxUsages);
            result.Notes.Add(EMemberLookupNote.Truncated);
        }

        result.Usages.AddRange(sorted);
        if (sorted.Any(u => !u.IsExact)) result.Notes.Add(EMemberLookupNote.NameOnlyMatches);
        if (result.OnDemandSkippedCount > 0) result.Notes.Add(EMemberLookupNote.OnDemandSkipped);
        result.OnDemandImportCount = onDemandImportCount;
        if (onDemandImportCount > 0) result.Notes.Add(EMemberLookupNote.OnDemandImports);
        return result;
    }

    #region query text

    /// <summary><see cref="MemberUsageQuery"/> text of a member of <paramref name="owner"/>, a class of <paramref name="package"/></summary>
    public static string QueryFor(string package, string owner, string member)
    {
        if (string.IsNullOrEmpty(member)) return null;
        if (string.IsNullOrEmpty(owner)) return member;

        owner = StripDefault(owner);
        return string.IsNullOrEmpty(package) ? $"{owner}:{member}" : $"{package}.{owner}:{member}";
    }

    public static string QueryFor(ResolvedObject owner, string member) =>
        owner == null ? QueryFor(null, null, member) : QueryFor(PackageOf(owner), owner.Name.Text, member);

    /// <summary>the function a final call runs</summary>
    public static string QueryForFunction(FPackageIndex function)
    {
        try
        {
            return function?.ResolvedObject is { } resolved ? QueryFor(resolved.Outer, resolved.Name.Text) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>a function of the class of an object, Default__BP_Foo_C.Function() for a function library</summary>
    public static string QueryForObjectMember(FPackageIndex instance, string member)
    {
        try
        {
            return instance?.ResolvedObject is { } resolved ? QueryFor(PackageOf(resolved), resolved.Name.Text, member) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>the member variable a property pointer names, null for a local of the function</summary>
    public static string QueryForProperty(FKismetPropertyPointer pointer)
    {
        try
        {
            if (pointer is { bNew: true, New: { Path.Length: > 0 } path })
            {
                var owner = path.ResolvedOwner?.ResolvedObject;
                if (owner?.Class?.Name.Text == "Function") return null;
                return QueryFor(owner, path.Path[0].Text);
            }

            return pointer?.Old?.ResolvedObject is { } resolved ? QueryFor(resolved.Outer, resolved.Name.Text) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// /Script/Engine for a native object, the package name of an export
    /// </summary>
    private static string PackageOf(ResolvedObject resolved)
    {
        var top = resolved;
        for (var depth = 0; top.Outer is { } outer && depth < 16; depth++) top = outer;
        var name = top.Name.Text;
        return name.StartsWith('/') ? name : resolved.Package?.Name;
    }

    #endregion

    private static bool InScope(GameFile file, string scope) =>
        scope == null || file.Path.StartsWith(scope, StringComparison.OrdinalIgnoreCase) &&
        (file.Path.Length == scope.Length || file.Path[scope.Length] == '/');

    /// <summary>
    /// a package of an IoStore on-demand container: every read downloads its chunk from the CDN (and caches it on disk),
    /// scanning them would download that part of the game one request at a time
    /// </summary>
    private static bool IsOnDemand(GameFile file) => file is VfsEntry { Vfs: IoStoreOnDemandReader };

    /// <summary>
    /// deserializing a function resolves its super function, which loads every package the blueprint imports:
    /// the on-demand ones that aren't in the chunk cache yet are then downloaded one by one
    /// </summary>
    private sealed class OnDemandImports(AbstractVfsFileProvider provider)
    {
        private readonly string _cache = provider.OnDemandOptions?.ChunkCacheDirectory?.FullName;
        private readonly ConcurrentDictionary<IoStoreReader, Dictionary<FPackageId, int>> _storeEntries = new();
        private readonly ConcurrentDictionary<FPackageId, bool> _needsDownload = new();

        public bool WouldDownload(GameFile file, IPackage package)
        {
            if (provider.OnDemandOptions == null || file is not FIoStoreEntry { IoStoreReader: var reader }) return false;
            if (reader is IoStoreOnDemandReader) return true;
            if (reader.ContainerHeader is not { } header) return false;

            var index = _storeEntries.GetOrAdd(reader, _ =>
            {
                var ids = new Dictionary<FPackageId, int>(header.PackageIds.Length);
                for (var i = 0; i < header.PackageIds.Length; i++) ids.TryAdd(header.PackageIds[i], i);
                return ids;
            });
            if (!index.TryGetValue(FPackageId.FromName(package.Name), out var entry)) return false;

            foreach (var imported in header.StoreEntries[entry].ImportedPackages ?? [])
            {
                if (_needsDownload.GetOrAdd(imported, NeedsDownload)) return true;
            }

            return false;
        }

        private bool NeedsDownload(FPackageId id)
        {
            if (!provider.FilesById.TryGetValue(id, out var file) ||
                file is not FIoStoreEntry { IoStoreReader: IoStoreOnDemandReader onDemand } entry)
                return false;

            // IoStoreOnDemandDownloader caches a chunk as {hash}.{ext}
            return _cache == null || !onDemand.Container.TryGetFileEntryHash(entry.ChunkId, out var chunk) ||
                   !System.IO.File.Exists(System.IO.Path.Combine(_cache, $"{chunk.FileEntryHash.ToString().ToLower()}.{chunk.ChunkExt}"));
        }
    }

    private static bool IsSearchable(GameFile file) =>
        file.Extension is "uasset" or "umap" && !file.Path.EndsWith(".o.uasset", StringComparison.OrdinalIgnoreCase);

    #region raw search

    private static bool Contains(byte[] data, ulong value) =>
        data.AsSpan().IndexOf(MemoryMarshal.AsBytes(new ReadOnlySpan<ulong>(in value))) >= 0;

    /// <summary>
    /// names are ASCII in both the zen name batches and the legacy name maps, FNames ignore case
    /// </summary>
    private static bool ContainsName(byte[] data, SearchValues<string> names)
    {
        var buffer = ArrayPool<char>.Shared.Rent(data.Length);
        try
        {
            var length = Encoding.Latin1.GetChars(data, buffer);
            return buffer.AsSpan(0, length).IndexOfAny(names) >= 0;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    #endregion

    #region candidates

    /// <summary>
    /// the packages with any bytecode, remembered after a first full scan so the next searches skip the rest of the game.
    /// Kept a day on disk too, for the same build: the archives mounted (path, size, date) make its fingerprint
    /// </summary>
    private static class BytecodeIndex
    {
        private const int _VERSION = 2;

        private sealed class Entry
        {
            public required string Fingerprint { get; init; }
            public required DateTime CreatedAt { get; init; }
            public required GameFile[] Packages { get; init; }

            public bool IsExpired => DateTime.UtcNow >= CreatedAt + CacheLifetime;
        }

        private sealed class Stored
        {
            public int Version { get; set; }
            public string Project { get; set; }
            public DateTime CreatedAt { get; set; }
            /// <summary>
            /// package paths by the archive they were read from: a path alone resolves to the archive mounted last,
            /// an on-demand container or a patch overriding the package, not the file that was scanned
            /// </summary>
            public Dictionary<string, string[]> Archives { get; set; }
        }

        private static readonly object _lock = new();
        private static readonly ConditionalWeakTable<AbstractVfsFileProvider, Entry> _cache = new();

        /// <returns>the local packages to read, and how many on-demand ones were left out</returns>
        public static (IReadOnlyCollection<GameFile> Packages, int OnDemand) Packages(AbstractVfsFileProvider provider, string scope)
        {
            var onDemand = provider.Files.Values.Count(f => IsOnDemand(f) && IsSearchable(f) && InScope(f, scope));
            IEnumerable<GameFile> packages = Get(provider)?.Packages ?? provider.Files.Values.Where(f => IsSearchable(f) && !IsOnDemand(f));
            return (packages.Where(f => InScope(f, scope)).ToList(), onDemand);
        }

        /// <summary>only a scan of the whole game over every package, without the index, can build it</summary>
        public static bool CanBuild(AbstractVfsFileProvider provider, string scope) =>
            scope == null && provider.GlobalData != null && Get(provider) == null;

        public static void Store(AbstractVfsFileProvider provider, IEnumerable<GameFile> packages)
        {
            var entry = new Entry { Fingerprint = Fingerprint(provider), CreatedAt = DateTime.UtcNow, Packages = packages.ToArray() };
            lock (_lock) _cache.AddOrUpdate(provider, entry);

            try
            {
                var directory = System.IO.Directory.CreateDirectory(CacheDirectory);
                var stored = new Stored
                {
                    Version = _VERSION, Project = provider.ProjectName, CreatedAt = entry.CreatedAt,
                    Archives = entry.Packages.GroupBy(f => f is VfsEntry vfsEntry ? vfsEntry.Vfs.Path : string.Empty)
                        .ToDictionary(g => g.Key, g => g.Select(f => f.Path).ToArray())
                };
                System.IO.File.WriteAllText(System.IO.Path.Combine(directory.FullName, $"{entry.Fingerprint}.json"), Newtonsoft.Json.JsonConvert.SerializeObject(stored));
                DeleteExpired(directory);
            }
            catch (Exception e)
            {
                Log.Warning(e, "Failed to save the blueprint package index");
            }
        }

        /// <summary>the index of the loaded build: from memory, from disk after a restart, null when it has to be built</summary>
        public static MemberUsageCacheInfo Info(AbstractVfsFileProvider provider) =>
            Get(provider) is { } entry ? new MemberUsageCacheInfo(entry.CreatedAt.ToLocalTime(), (entry.CreatedAt + CacheLifetime).ToLocalTime(), entry.Packages.Length) : null;

        public static void Clear()
        {
            lock (_lock) _cache.Clear();
            try
            {
                if (System.IO.Directory.Exists(CacheDirectory)) System.IO.Directory.Delete(CacheDirectory, true);
            }
            catch (Exception e)
            {
                Log.Warning(e, "Failed to delete the blueprint package index");
            }
        }

        private static Entry Get(AbstractVfsFileProvider provider)
        {
            var fingerprint = Fingerprint(provider);
            lock (_lock)
            {
                if (_cache.TryGetValue(provider, out var entry) && entry.Fingerprint == fingerprint && !entry.IsExpired) return entry;

                entry = Load(provider, fingerprint);
                if (entry != null) _cache.AddOrUpdate(provider, entry);
                else _cache.Remove(provider);
                return entry;
            }
        }

        private static Entry Load(AbstractVfsFileProvider provider, string fingerprint)
        {
            string path = null;
            try
            {
                path = System.IO.Path.Combine(CacheDirectory, $"{fingerprint}.json");
                if (!System.IO.File.Exists(path)) return null;

                var stored = Newtonsoft.Json.JsonConvert.DeserializeObject<Stored>(System.IO.File.ReadAllText(path));
                if (stored is not { Version: _VERSION, Archives: not null } || DateTime.UtcNow >= stored.CreatedAt + CacheLifetime)
                {
                    System.IO.File.Delete(path);
                    return null;
                }

                var archives = provider.MountedVfs.GroupBy(v => v.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                var packages = new List<GameFile>(stored.Archives.Values.Sum(p => p.Length));
                foreach (var (archivePath, paths) in stored.Archives)
                {
                    IReadOnlyDictionary<string, GameFile> files = archivePath.Length == 0 ? provider.Files
                        : archives.TryGetValue(archivePath, out var archive) ? archive.Files : null;
                    foreach (var packagePath in paths)
                    {
                        // a package that's gone: not the build the index was made for
                        if (files == null || !files.TryGetValue(packagePath, out var file))
                        {
                            System.IO.File.Delete(path);
                            return null;
                        }

                        packages.Add(file);
                    }
                }

                return new Entry { Fingerprint = fingerprint, CreatedAt = stored.CreatedAt, Packages = [.. packages] };
            }
            catch (Exception e)
            {
                Log.Warning(e, "Failed to read the blueprint package index {Path}", path);
                return null;
            }
        }

        private static void DeleteExpired(System.IO.DirectoryInfo directory)
        {
            foreach (var file in directory.EnumerateFiles("*.json"))
            {
                if (DateTime.UtcNow - file.LastWriteTimeUtc >= CacheLifetime) file.Delete();
            }
        }

        /// <summary>
        /// the local archives mounted, a new build, a new key or an added pak changes it (on-demand containers aren't searched)
        /// </summary>
        private static string Fingerprint(AbstractVfsFileProvider provider)
        {
            var builder = new StringBuilder(provider.ProjectName).Append('|').Append(provider.Versions.Game).Append('\n');
            foreach (var vfs in provider.MountedVfs.Where(v => v is not IoStoreOnDemandReader).OrderBy(v => v.Path, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append(vfs.Path);
                var file = new System.IO.FileInfo(vfs.Path);
                if (file.Exists) builder.Append('|').Append(file.Length).Append('|').Append(file.LastWriteTimeUtc.Ticks);
                builder.Append('\n');
            }

            return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        }
    }

    /// <summary>
    /// packages importing <paramref name="packageName"/> and the package itself, from the IoStore container headers
    /// </summary>
    private static bool TryGetImporters(AbstractVfsFileProvider provider, string packageName, out List<GameFile> importers)
    {
        importers = [];
        // the header of an on-demand container is downloaded when first read, and its packages aren't searched anyway
        var readers = provider.MountedVfs.OfType<IoStoreReader>()
            .Where(r => r is not IoStoreOnDemandReader && r.ContainerHeader is { StoreEntries.Length: > 0 })
            .ToArray();
        if (readers.Length == 0) return false;

        var packageId = FPackageId.FromName(packageName);
        var found = new ConcurrentDictionary<string, GameFile>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(readers, reader =>
        {
            var header = reader.ContainerHeader!;
            for (var i = 0; i < header.StoreEntries.Length; i++)
            {
                var id = header.PackageIds[i];
                if ((id.Equals(packageId) || header.StoreEntries[i].ImportedPackages.Contains(packageId)) &&
                    reader.PackageIdIndex.TryGetValue(id, out var file) && IsSearchable(file))
                    found.TryAdd(file.Path, file);
            }
        });

        if (found.IsEmpty) return false; // not a package of this build, search everything by name
        importers = [.. found.Values];
        return true;
    }

    #endregion

    #region matching

    /// <summary>
    /// the query resolved against the loaded build: which class names own the member and which script imports it has
    /// </summary>
    private sealed class Target
    {
        public required MemberUsageQuery Query { get; init; }
        /// <summary>the owner as it may be named (Actor, AActor, BP_Foo, BP_Foo_C), null accepts any class</summary>
        public HashSet<string> Owners { get; init; }
        /// <summary>parents of the owner, an inherited member is declared on one of them</summary>
        public HashSet<string> Ancestors { get; init; }
        /// <summary>zen script imports of the native function, the only trace a final call leaves</summary>
        public ulong[] ScriptHashes { get; init; } = [];
        /// <summary>script import of UFunction, the class of every export holding bytecode</summary>
        public ulong? FunctionClassHash { get; init; }
        /// <summary>package declaring a blueprint owner, its own definition isn't a use</summary>
        public string OwnerFilePath { get; init; }

        public bool WantsFunctions => Query.Kind != EMemberKind.Variable;
        public bool WantsVariables => Query.Kind != EMemberKind.Function;

        public static Target Prepare(AbstractVfsFileProvider provider, MemberUsageQuery query)
        {
            HashSet<string> owners = null, ancestors = null;
            if (query.OwnerClass != null)
            {
                owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                AddOwner(owners, query.OwnerClass);
                foreach (var parent in BlueprintParents(provider, query)) AddOwner(ancestors, parent);
                foreach (var native in owners.Concat(ancestors).ToList())
                foreach (var super in BlueprintNodeDatabase.Supers(native))
                    AddOwner(ancestors, super);
                ancestors.ExceptWith(owners);
            }

            ulong[] scriptHashes = [];
            ulong? functionClass = null;
            if (provider.GlobalData is { } globalData)
            {
                var entries = globalData.ScriptObjectEntriesMap;
                string NameOf(FPackageObjectIndex index) =>
                    entries.TryGetValue(index, out var entry) ? new FName(entry.ObjectName, globalData.GlobalNameMap).Text : null;

                var hashes = new List<ulong>();
                foreach (var (index, entry) in entries)
                {
                    var name = new FName(entry.ObjectName, globalData.GlobalNameMap).Text;
                    if (name == "Function" && NameOf(entry.OuterIndex) == "/Script/CoreUObject")
                        functionClass = index.TypeAndId;

                    if (query.Kind == EMemberKind.Variable || !name.Equals(query.Name, StringComparison.OrdinalIgnoreCase)) continue;

                    // a function's outer is its class, the class's outer its /Script/ package
                    var outer = NameOf(entry.OuterIndex);
                    if (outer == null || outer.StartsWith('/') || outer.StartsWith("Default__", StringComparison.Ordinal)) continue;
                    if (owners != null && !owners.Contains(outer) && !ancestors.Contains(outer)) continue;
                    hashes.Add(index.TypeAndId);
                }

                scriptHashes = hashes.Count <= MaxScriptHashes ? [.. hashes] : [];
            }

            string ownerFile = null;
            if (query.IsBlueprintOwner && provider.TryGetGameFile(query.OwnerPackage + ".uasset", out var file)) ownerFile = file.Path;

            return new Target
            {
                Query = query, Owners = owners, Ancestors = ancestors, ScriptHashes = scriptHashes, FunctionClassHash = functionClass, OwnerFilePath = ownerFile
            };
        }

        private static void AddOwner(HashSet<string> owners, string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            owners.Add(name);
            if (name.EndsWith("_C", StringComparison.OrdinalIgnoreCase)) owners.Add(name[..^2]);
            else owners.Add(name + "_C");
            // AActor, UObject... as the C++ code names them
            if (name.Length > 2 && name[0] is 'A' or 'U' or 'F' or 'I' && char.IsUpper(name[1])) owners.Add(name[1..]);
        }

        /// <summary>
        /// the blueprint classes above a blueprint owner, and the first native class
        /// </summary>
        private static IEnumerable<string> BlueprintParents(AbstractVfsFileProvider provider, MemberUsageQuery query)
        {
            if (!query.IsBlueprintOwner) yield break;

            UStruct current = null;
            try
            {
                if (provider.TryLoadPackage(query.OwnerPackage, out var package))
                    current = package.GetExports().OfType<UClass>().FirstOrDefault(c =>
                        c.Name.Equals(query.OwnerClass, StringComparison.OrdinalIgnoreCase) ||
                        c.Name.Equals(query.OwnerClass + "_C", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception e)
            {
                Log.Debug(e, "Failed to load {Package} to read its parents", query.OwnerPackage);
            }

            for (var depth = 0; current?.SuperStruct is { IsNull: false } super && depth < 32; depth++)
            {
                yield return super.Name;
                UStruct next = null;
                try
                {
                    if (super.TryLoad(out var loaded)) next = loaded as UClass;
                }
                catch (Exception)
                {
                    // a native class doesn't load, its parents come from the SDK dump
                }

                current = next;
            }
        }

        public bool NameMatches(string name) => name != null && name.Equals(Query.Name, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// the member of the owner, one it inherits, or the same member reached through a child class;
        /// a null owner (the bytecode doesn't say) is accepted as a name only match
        /// </summary>
        public bool OwnerMatches(string owner)
        {
            if (Owners == null || owner == null) return true;

            owner = StripDefault(owner);
            return Owners.Contains(owner) || Ancestors.Contains(owner) || BlueprintNodeDatabase.Supers(owner).Any(Owners.Contains);
        }

        /// <summary>
        /// without the bytecode: the name in the name map, or the script import of the function
        /// </summary>
        public IEnumerable<MemberUsage> MatchHeader(GameFile file, IPackage package)
        {
            var named = package.NameMap.Any(n => NameMatches(n.Name));
            var imported = package is IoPackage io && ScriptHashes.Length > 0 && io.ImportMap.Any(i => ScriptHashes.Contains(i.TypeAndId));
            if (!named && !imported) return [];

            return
            [
                new MemberUsage
                {
                    File = file, Kind = EMemberUsageKind.Reference, Member = imported ? Query.Display : $"?.{Query.Name}",
                    UsedIn = string.Empty, Count = 1, IsExact = imported
                }
            ];
        }
    }

    private static string StripDefault(string name) =>
        name.StartsWith("Default__", StringComparison.Ordinal) ? name["Default__".Length..] : name;

    /// <summary>
    /// class of a resolved function and the package it lives in (/Script/Engine for native ones)
    /// </summary>
    private static string OwnerName(ResolvedObject resolved)
    {
        try
        {
            return resolved?.Outer?.Name.Text is { } outer ? StripDefault(outer) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    #endregion

    #region bytecode

    /// <summary>
    /// walks the bytecode of every function of a package, collecting where the member is used
    /// </summary>
    private sealed class PackageSearch(GameFile file, IPackage package, Target target)
    {
        private readonly Dictionary<(string UsedIn, EMemberUsageKind Kind, string Member), MemberUsage> _usages = new();
        private string _usedIn;
        private string _functionName;
        private int _statement;
        private UStruct _class;
        private List<(int Entry, string Event)> _events = [];
        private static readonly EX_Self _self = new();

        public IEnumerable<MemberUsage> Run(CancellationToken cancellationToken)
        {
            var functions = Functions().ToList();
            if (functions.Count == 0) return [];

            _events = EventEntries(functions);
            foreach (var function in functions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _class = function.Outer is { } outer && outer.TryLoad(out var loaded) ? loaded as UStruct : null;
                _functionName = function.Name;
                var isUbergraph = function.Name.StartsWith("ExecuteUbergraph", StringComparison.Ordinal);

                // the blueprint implementing an event or overriding a function under that name
                if (target.WantsFunctions && target.NameMatches(function.Name) && IsOverride(function, out var overridden))
                {
                    _usedIn = function.Name;
                    _statement = -1;
                    Add(EMemberUsageKind.Override, overridden, overridden != null);
                }

                foreach (var statement in function.ScriptBytecode ?? [])
                {
                    _statement = statement.StatementIndex;
                    _usedIn = isUbergraph ? EventAt(statement.StatementIndex) : function.Name;
                    Walk(statement, false, 0);
                }
            }

            return _usages.Values;
        }

        private IEnumerable<UFunction> Functions()
        {
            for (var i = 0; i < package.ExportsLazy.Length; i++)
            {
                if (!IsFunctionExport(i)) continue;

                UFunction function = null;
                try
                {
                    function = package.ExportsLazy[i].Value as UFunction;
                }
                catch (Exception e)
                {
                    Log.Debug(e, "Failed to read export {Index} of {Path}", i, file.Path);
                }

                if (function != null) yield return function;
            }
        }

        /// <summary>only the functions are deserialized, not the whole package</summary>
        private bool IsFunctionExport(int index)
        {
            try
            {
                return package switch
                {
                    IoPackage io => io.ResolveObjectIndex(io.ExportMap[index].ClassIndex)?.Name.Text == "Function",
                    Package legacy => legacy.ExportMap[index].ClassName == "Function",
                    _ => true
                };
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// a function overriding a parent's keeps it as its super, an event implemented in the blueprint is flagged as one
        /// </summary>
        private bool IsOverride(UFunction function, out string overridden)
        {
            overridden = null;
            if (function.SuperStruct is { IsNull: false })
            {
                // overriding an override: the chain up to the first declaration, any of them may be the one searched
                var chain = new List<string>();
                var super = function.SuperStruct;
                for (var depth = 0; super is { IsNull: false } && depth < 16; depth++)
                {
                    try
                    {
                        if (OwnerName(super.ResolvedObject) is { } owner) chain.Add(owner);
                        super = super.TryLoad(out var loaded) && loaded is UFunction parent ? parent.SuperStruct : null;
                    }
                    catch (Exception)
                    {
                        break; // native functions don't load
                    }
                }

                overridden = chain.LastOrDefault();
                return chain.Count == 0 || chain.Any(target.OwnerMatches);
            }

            // the member's own declaration isn't a use of it
            if (file.Path.Equals(target.OwnerFilePath, StringComparison.OrdinalIgnoreCase)) return false;
            return function.FunctionFlags.HasFlag(EFunctionFlags.FUNC_Event) && target.Owners == null;
        }

        /// <summary>
        /// the event graph is one function, its events are small stubs calling it at their entry offset
        /// </summary>
        private static List<(int Entry, string Event)> EventEntries(List<UFunction> functions)
        {
            var entries = new List<(int, string)>();
            foreach (var function in functions)
            {
                if (function.Name.StartsWith("ExecuteUbergraph", StringComparison.Ordinal)) continue;
                foreach (var statement in function.ScriptBytecode ?? [])
                {
                    if (statement is EX_FinalFunction { Parameters: [EX_IntConst entry, ..], StackNode.IsExport: true } call &&
                        call.StackNode.Name.StartsWith("ExecuteUbergraph", StringComparison.Ordinal))
                        entries.Add((entry.Value, function.Name));
                }
            }

            entries.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return entries;
        }

        private string EventAt(int offset)
        {
            string name = null;
            foreach (var (entry, eventName) in _events)
            {
                if (entry > offset) break;
                name = eventName;
            }

            return name != null ? $"EventGraph: {name}" : "EventGraph";
        }

        private void Add(EMemberUsageKind kind, string owner, bool exact)
        {
            owner = owner != null ? StripDefault(owner) : null;
            var member = $"{owner ?? "?"}.{target.Query.Name}";
            var key = (_usedIn, kind, member);
            if (_usages.TryGetValue(key, out var usage))
            {
                usage.Count++;
                return;
            }

            _usages[key] = new MemberUsage
            {
                File = file,
                Kind = kind,
                Member = member,
                UsedIn = _usedIn,
                FunctionName = _functionName,
                Offset = _statement,
                Count = 1,
                IsExact = exact && owner != null
            };
        }

        private void Function(string name, string owner, bool ownerKnown, EMemberUsageKind kind)
        {
            if (!target.WantsFunctions || !target.NameMatches(name)) return;
            if (ownerKnown && !target.OwnerMatches(owner)) return;
            Add(kind, owner, ownerKnown);
        }

        private void Variable(FKismetPropertyPointer pointer, bool write)
        {
            if (!target.WantsVariables || pointer == null) return;

            string name, owner;
            try
            {
                if (pointer.bNew)
                {
                    if (pointer.New is not { Path.Length: > 0 } path) return;
                    name = path.Path[0].Text;
                    if (!target.NameMatches(name)) return;
                    owner = path.ResolvedOwner is { IsNull: false } resolvedOwner ? resolvedOwner.Name : null;
                }
                else
                {
                    name = pointer.Old?.Name;
                    if (!target.NameMatches(name)) return;
                    owner = OwnerName(pointer.Old?.ResolvedObject);
                }
            }
            catch (Exception)
            {
                return;
            }

            if (owner != null && !target.OwnerMatches(owner)) return;
            Add(write ? EMemberUsageKind.Write : EMemberUsageKind.Read, owner, true);
        }

        /// <summary>
        /// the class first declaring <paramref name="name"/> as <paramref name="type"/> sees it, overrides are the same member:
        /// the topmost blueprint whose functions list it, or the native class the SDK dump says declares it
        /// </summary>
        /// <param name="found">a class below <paramref name="type"/> already known to declare it</param>
        private static string DeclaringClass(FPackageIndex type, string name, string found = null)
        {
            for (var depth = 0; type is { IsNull: false } && depth < 16; depth++)
            {
                UClass blueprint = null;
                try
                {
                    if (type.TryLoad(out var loaded)) blueprint = loaded as UClass;
                }
                catch (Exception)
                {
                    // native classes don't load
                }

                if (blueprint is not { FuncMap: not null })
                {
                    var native = StripDefault(type.Name);
                    return BlueprintNodeDatabase.FindInHierarchy(native, name)?.Class ?? found ?? native;
                }

                if (Declares(blueprint, name)) found = blueprint.Name;
                type = blueprint.SuperStruct;
            }

            return found;
        }

        private static bool Declares(UClass blueprint, string name) =>
            blueprint.FuncMap?.Keys.Any(k => k.Text.Equals(name, StringComparison.OrdinalIgnoreCase)) == true;

        /// <summary>
        /// the class a Target.Function() call runs on: self, a class default object (function libraries), or the declared type of a variable
        /// </summary>
        private string ContextClass(KismetExpression target, string name)
        {
            try
            {
                switch (target)
                {
                    case EX_Self:
                        return _class is UClass self ? DeclaringClass(self.SuperStruct, name, Declares(self, name) ? self.Name : null) : null;
                    case EX_ObjectConst { Value: { IsNull: false } value }:
                    {
                        var resolved = value.ResolvedObject;
                        if (!value.Name.StartsWith("Default__", StringComparison.Ordinal)) return resolved?.Class?.Name.Text;

                        // the class of a default object sits next to it, in the same package
                        if (resolved?.Class?.Object?.Value is UClass blueprint)
                            return DeclaringClass(blueprint.SuperStruct, name, Declares(blueprint, name) ? blueprint.Name : null) ?? blueprint.Name;

                        var native = value.Name["Default__".Length..];
                        return BlueprintNodeDatabase.FindInHierarchy(native, name)?.Class ?? native;
                    }
                    case EX_InterfaceContext interfaceContext:
                        return ContextClass(interfaceContext.InterfaceValue, name);
                    case EX_VariableBase variable:
                        return VariableClass(variable.Variable, name);
                }
            }
            catch (Exception)
            {
                // unresolved imports
            }

            return null;
        }

        /// <summary>the object class of a variable, looked up on the struct declaring it</summary>
        private static string VariableClass(FKismetPropertyPointer pointer, string function)
        {
            if (pointer?.New is not { Path.Length: > 0 } path || path.ResolvedOwner is not { IsNull: false } owner) return null;

            var property = path.Path[0].Text;
            FPackageIndex type = null;
            if (owner.TryLoad(out var loaded) && loaded is UStruct { ChildProperties: not null } structure)
            {
                type = structure.ChildProperties.OfType<FProperty>().FirstOrDefault(p => p.Name.Text == property) switch
                {
                    FInterfaceProperty interfaceProperty => interfaceProperty.InterfaceClass,
                    FObjectProperty objectProperty => objectProperty.PropertyClass,
                    _ => null
                };
            }

            if (type is { IsNull: false }) return DeclaringClass(type, function);

            // a member of a native class, typed by the SDK dump
            var memberType = BlueprintNodeDatabase.MemberType(StripDefault(owner.Name), property);
            return memberType != null ? BlueprintNodeDatabase.FindInHierarchy(memberType, function)?.Class ?? memberType : null;
        }

        private void Walk(KismetExpression expression, bool write, int depth)
        {
            if (expression == null || depth > 64) return;
            depth++;

            switch (expression)
            {
                case EX_FinalFunction final: // EX_CallMath, EX_LocalFinalFunction
                {
                    string name = null, owner = null;
                    try
                    {
                        var resolved = MayBeTarget(final.StackNode) ? final.StackNode.ResolvedObject : null;
                        name = resolved?.Name.Text;
                        owner = OwnerName(resolved);
                    }
                    catch (Exception)
                    {
                        // an import that doesn't resolve
                    }

                    Function(name, owner, owner != null, EMemberUsageKind.Call);
                    WalkAll(final.Parameters, depth);
                    break;
                }
                case EX_VirtualFunction virtualFunction: // EX_LocalVirtualFunction, a self call out of a context
                    VirtualCall(virtualFunction, _self, depth);
                    break;
                case EX_BindDelegate bind:
                    Function(bind.FunctionName.Text, null, false, EMemberUsageKind.Bind);
                    Walk(bind.Delegate, false, depth);
                    Walk(bind.ObjectTerm, false, depth);
                    break;
                case EX_InstanceDelegate instanceDelegate:
                    Function(instanceDelegate.FunctionName.Text, null, false, EMemberUsageKind.Bind);
                    break;
                case EX_CallMulticastDelegate multicast:
                    Walk(multicast.Delegate, false, depth);
                    WalkAll(multicast.Parameters, depth);
                    break;
                case EX_LocalVariable or EX_LocalOutVariable:
                    break; // locals of the function, not members
                case EX_VariableBase variable: // instance, default, sparse data
                    Variable(variable.Variable, write);
                    break;
                case EX_StructMemberContext member:
                    Variable(member.Property, write);
                    Walk(member.StructExpression, write, depth);
                    break;
                case EX_PropertyConst property:
                    Variable(property.Property, false);
                    break;
                case EX_Context context: // EX_Context_FailSilent, EX_ClassContext
                    Walk(context.ObjectExpression, false, depth);
                    if (context.ContextExpression is EX_VirtualFunction contextCall) VirtualCall(contextCall, context.ObjectExpression, depth);
                    else Walk(context.ContextExpression, write, depth);
                    break;
                case EX_Let let:
                    Walk(let.Variable, true, depth);
                    Walk(let.Assignment, false, depth);
                    break;
                case EX_LetBase let:
                    Walk(let.Variable, true, depth);
                    Walk(let.Assignment, false, depth);
                    break;
                case EX_LetValueOnPersistentFrame persistent:
                    Walk(persistent.AssignmentExpression, false, depth);
                    break;
                case EX_AddMulticastDelegate add:
                    Walk(add.Delegate, false, depth);
                    Walk(add.DelegateToAdd, false, depth);
                    break;
                case EX_RemoveMulticastDelegate remove:
                    Walk(remove.Delegate, false, depth);
                    Walk(remove.DelegateToAdd, false, depth);
                    break;
                case EX_ClearMulticastDelegate clear:
                    Walk(clear.DelegateToClear, false, depth);
                    break;
                case EX_ArrayGetByRef get:
                    Walk(get.ArrayVariable, write, depth);
                    Walk(get.ArrayIndex, false, depth);
                    break;
                case EX_InterfaceContext interfaceContext:
                    Walk(interfaceContext.InterfaceValue, false, depth);
                    break;
                case EX_CastBase cast:
                    Walk(cast.Target, false, depth);
                    break;
                case EX_Cast cast:
                    Walk(cast.Target, false, depth);
                    break;
                case EX_JumpIfNot jumpIfNot:
                    Walk(jumpIfNot.BooleanExpression, false, depth);
                    break;
                case EX_PopExecutionFlowIfNot popIfNot:
                    Walk(popIfNot.BooleanExpression, false, depth);
                    break;
                case EX_Skip skip:
                    Walk(skip.SkipExpression, false, depth);
                    break;
                case EX_ComputedJump computedJump:
                    Walk(computedJump.CodeOffsetExpression, false, depth);
                    break;
                case EX_Return ret:
                    Walk(ret.ReturnExpression, false, depth);
                    break;
                case EX_Assert assert:
                    Walk(assert.AssertExpression, false, depth);
                    break;
                case EX_SwitchValue select:
                    Walk(select.IndexTerm, false, depth);
                    Walk(select.DefaultTerm, false, depth);
                    foreach (var switchCase in select.Cases ?? [])
                    {
                        Walk(switchCase.CaseIndexValueTerm, false, depth);
                        Walk(switchCase.CaseTerm, false, depth);
                    }
                    break;
                case EX_SetArray setArray:
                    Walk(setArray.AssigningProperty, true, depth);
                    WalkAll(setArray.Elements, depth);
                    break;
                case EX_SetSet setSet:
                    Walk(setSet.SetProperty, true, depth);
                    WalkAll(setSet.Elements, depth);
                    break;
                case EX_SetMap setMap:
                    Walk(setMap.MapProperty, true, depth);
                    WalkAll(setMap.Elements, depth);
                    break;
                case EX_ArrayConst arrayConst:
                    WalkAll(arrayConst.Elements, depth);
                    break;
                case EX_SetConst setConst:
                    WalkAll(setConst.Elements, depth);
                    break;
                case EX_MapConst mapConst:
                    WalkAll(mapConst.Elements, depth);
                    break;
                case EX_StructConst structConst:
                    WalkAll(structConst.Properties, depth);
                    break;
                case EX_AutoRtfmTransact transact:
                    WalkAll(transact.Parameters, depth);
                    break;
                case EX_FieldPathConst fieldPath:
                    Walk(fieldPath.Value, false, depth);
                    break;
            }
        }

        /// <summary>a virtual call only names the function, its class is the type of the object it runs on</summary>
        private void VirtualCall(EX_VirtualFunction call, KismetExpression context, int depth)
        {
            var name = call.VirtualFunctionName.Text;
            if (target.WantsFunctions && target.NameMatches(name))
            {
                var owner = ContextClass(context, name);
                Function(name, owner, owner != null, EMemberUsageKind.Call);
            }

            WalkAll(call.Parameters, depth);
        }

        /// <summary>
        /// resolving a function of another package loads every package this one imports (textures, meshes... some of them
        /// on-demand downloads), skipped when that function can't be the one searched: the target is a native function
        /// </summary>
        private bool MayBeTarget(FPackageIndex function)
        {
            if (function is not { IsNull: false }) return false;
            if (!function.IsImport || package is not IoPackage io || target.ScriptHashes.Length == 0 || target.Query.IsBlueprintOwner) return true;

            var index = -function.Index - 1;
            return index >= io.ImportMap.Length || !io.ImportMap[index].IsPackageImport;
        }

        private void WalkAll(KismetExpression[] expressions, int depth)
        {
            foreach (var expression in expressions ?? []) Walk(expression, false, depth);
        }
    }

    #endregion
}
