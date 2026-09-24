using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Wwise;
using CUE4Parse.Utils;
using FModel.ViewModels;
using Serilog;

namespace FModel.Services;

public enum EAudioLookupNote
{
    /// <summary>no Wwise content in the loaded build, only package references were searched</summary>
    NoWwise,
    /// <summary>the audio isn't a Wwise media (or its id couldn't be found), events weren't searched</summary>
    NoMediaId,
    /// <summary>soundbanks couldn't be loaded, events that only reference media through banks were skipped</summary>
    WwiseBanksUnavailable,
    /// <summary>package references come from the IoStore container headers, pak only games aren't supported</summary>
    NotIoStore,
    /// <summary>the audio was added from disk, there is no package to search references for</summary>
    NoSourceAsset,
    /// <summary>too many referencers, the search stopped expanding</summary>
    Truncated
}

public sealed class AudioUsage
{
    public required GameFile File { get; init; }
    public required string ClassName { get; set; }
    public required string Name { get; init; }
    /// <summary>1 = uses the audio directly, 2 = uses something that uses it, ...</summary>
    public required int Depth { get; init; }
    public string Via { get; init; }
    public string Detail { get; init; }
    public bool IsWwiseEvent { get; init; }

    public string Path => File.Path;
}

public sealed class AudioReverseLookupResult
{
    public required string TargetName { get; init; }
    public GameFile SourceAsset { get; init; }
    public IReadOnlyList<uint> MediaIds { get; init; } = [];
    public List<AudioUsage> Usages { get; } = [];
    public HashSet<EAudioLookupNote> Notes { get; } = [];
    public int IndexedEventCount { get; set; }
}

/// <summary>
/// finds what uses a sound:
/// - Wwise events that play the media, through an index of every event's cooked media (and soundbank hierarchy for older integrations)
/// - packages that import the sound (or the events above), from the IoStore container headers, a few levels up
/// </summary>
public static partial class AudioReverseLookup
{
    public const int MaxDepth = 3;
    private const int MaxUsages = 1000;
    private const int MaxNewPerLevel = 300;

    [GeneratedRegex(@"^(?:(?<id>\d+)|.*\((?<id>\d+)\))$")]
    private static partial Regex MediaIdRegex();

    public static AudioReverseLookupResult Run(CUE4ParseViewModel cue4Parse, GameFile sourceAsset, string audioName, CancellationToken cancellationToken) =>
        Run(cue4Parse.Provider, () => cue4Parse.WwiseProvider, sourceAsset, audioName, cancellationToken);

    /// <param name="getWwise">soundbanks are only loaded if some events need them</param>
    public static AudioReverseLookupResult Run(AbstractVfsFileProvider provider, Func<WwiseProvider> getWwise, GameFile sourceAsset, string audioName, CancellationToken cancellationToken)
    {
        var hasWwise = HasWwiseContent(provider);
        var mediaIds = hasWwise ? ResolveMediaIds(provider, sourceAsset, audioName) : [];
        var result = new AudioReverseLookupResult
        {
            TargetName = audioName ?? sourceAsset?.NameWithoutExtension ?? "?",
            SourceAsset = sourceAsset,
            MediaIds = mediaIds
        };

        // Wwise: media id -> events
        var seeds = new List<(GameFile File, string Label)>();
        if (!hasWwise)
        {
            result.Notes.Add(EAudioLookupNote.NoWwise);
        }
        else if (mediaIds.Count == 0)
        {
            result.Notes.Add(EAudioLookupNote.NoMediaId);
        }
        else
        {
            var index = WwiseEventIndex.Get(provider, getWwise, cancellationToken, result.Notes);
            result.IndexedEventCount = index.EventCount;

            foreach (var group in mediaIds
                         .SelectMany(id => index.Media.TryGetValue(id, out var events) ? events.Select(e => (Id: id, e.Event, e.Language)) : [])
                         .GroupBy(x => (x.Event.File.Path, x.Event.Name)))
            {
                var first = group.First();
                if (first.Event.File.Path.Equals(sourceAsset?.Path, StringComparison.OrdinalIgnoreCase))
                    continue; // the event it was extracted from, not a finding

                var languages = group.Select(x => x.Language).Where(l => !string.IsNullOrEmpty(l)).Distinct().ToArray();
                result.Usages.Add(new AudioUsage
                {
                    File = first.Event.File,
                    ClassName = "AkAudioEvent",
                    Name = first.Event.Name,
                    Depth = 1,
                    Via = result.TargetName,
                    Detail = string.Join(", ", group.Select(x => x.Id).Distinct()) + (languages.Length > 0 ? $" ({string.Join(", ", languages)})" : string.Empty),
                    IsWwiseEvent = true
                });
                seeds.Add((first.Event.File, first.Event.Name));
            }
        }

        // packages: whoever imports the sound or the events
        if (sourceAsset is { IsUePackage: true })
            seeds.Insert(0, (sourceAsset, result.TargetName));
        else if (sourceAsset == null)
            result.Notes.Add(EAudioLookupNote.NoSourceAsset);

        if (seeds.Count > 0)
            FindReferencers(provider, seeds, result, cancellationToken);

        return result;
    }

    #region media ids

    public static IReadOnlyList<uint> ResolveMediaIds(AbstractVfsFileProvider provider, GameFile sourceAsset, string audioName)
    {
        var ids = new HashSet<uint>();

        // "123456", "Play_Foo (123456)" when resolved through soundbanks, or a loose "123456.wem"
        if (audioName != null && TryParseMediaId(audioName, out var id)) ids.Add(id);
        if (sourceAsset is { Extension: "wem" } && uint.TryParse(sourceAsset.NameWithoutExtension, out id)) ids.Add(id);
        if (ids.Count > 0 || sourceAsset is not { IsUePackage: true }) return [.. ids];

        try
        {
            if (!provider.TryLoadPackage(sourceAsset, out var package)) return [];
            foreach (var export in package.GetExports())
            {
                switch (export)
                {
                    case UAkMediaAsset { ID: > 0 } mediaAsset:
                        ids.Add(mediaAsset.ID);
                        break;
                    case UAkAudioEvent { EventCookedData: { } cooked }:
                        // cooked media are extracted as "{DebugName} ({Language})", match the one that was played
                        foreach (var (language, eventData) in cooked.EventLanguageMap)
                        {
                            if (eventData is not { } data) continue;
                            foreach (var media in EnumerateMedia(data))
                            {
                                if (audioName == null || GetExtractedName(media, language).Equals(audioName, StringComparison.OrdinalIgnoreCase))
                                    ids.Add(media.MediaId);
                            }
                        }
                        break;
                }
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to read the Wwise media of {Path}", sourceAsset.Path);
        }

        ids.Remove(0);
        return [.. ids];
    }

    private static bool TryParseMediaId(string name, out uint id)
    {
        id = 0;
        var match = MediaIdRegex().Match(name.Trim());
        return match.Success && uint.TryParse(match.Groups["id"].Value, out id) && id != 0;
    }

    /// <summary>
    /// same naming as <see cref="WwiseProvider"/> uses for cooked media
    /// </summary>
    private static string GetExtractedName(FWwiseMediaCookedData media, FWwiseLanguageCookedData language)
    {
        var mediaPath = media.MediaPathName.IsNone
            ? media.PackagedFile?.PathName.ToString() ?? string.Empty
            : media.MediaPathName.Text;
        var name = !string.IsNullOrEmpty(media.DebugName.Text) && !media.DebugName.IsNone
            ? media.DebugName.Text.SubstringBeforeLast('.')
            : Path.GetFileNameWithoutExtension(mediaPath);
        return $"{name} ({language.LanguageName.Text})";
    }

    private static IEnumerable<FWwiseMediaCookedData> EnumerateMedia(FWwiseEventCookedData data)
    {
        foreach (var media in data.Media) yield return media;
        foreach (var node in data.AudioNodes.Values)
        {
            if (node == null) continue;
            foreach (var media in node.Media) yield return media;
        }
        foreach (var leaf in data.SwitchContainerLeaves)
        {
            foreach (var media in leaf.Media) yield return media;
        }
    }

    private static bool HasEventSoundBanks(FWwiseEventCookedData data) =>
        data.SoundBanks.Length > 0 ||
        data.AudioNodes.Values.Any(n => n is { SoundBanks.Length: > 0 }) ||
        data.SwitchContainerLeaves.Any(l => l.SoundBanks.Length > 0);

    private static readonly ConditionalWeakTable<AbstractVfsFileProvider, StrongBox<bool>> _hasWwise = new();

    private static bool HasWwiseContent(AbstractVfsFileProvider provider) =>
        _hasWwise.GetValue(provider, p => new StrongBox<bool>(p.Files.Values.Any(f =>
            f.Extension is "wem" or "bnk" or "pck" || f.Path.Contains("Wwise", StringComparison.OrdinalIgnoreCase)))).Value;

    #endregion

    #region Wwise event index

    private sealed record WwiseEvent(GameFile File, string Name);

    private sealed class WwiseEventIndex
    {
        public required Dictionary<uint, List<(WwiseEvent Event, string Language)>> Media { get; init; }
        public required int EventCount { get; init; }

        private static readonly object _lock = new();
        private static WeakReference<AbstractVfsFileProvider> _provider;
        private static int _fileCount;
        private static WwiseEventIndex _cached;
        private static HashSet<EAudioLookupNote> _cachedNotes = [];

        /// <summary>
        /// built once per loaded build, every lookup after that is a dictionary hit
        /// </summary>
        public static WwiseEventIndex Get(AbstractVfsFileProvider provider, Func<WwiseProvider> getWwise, CancellationToken cancellationToken, HashSet<EAudioLookupNote> notes)
        {
            lock (_lock)
            {
                if (_cached != null && _provider.TryGetTarget(out var cachedProvider) && cachedProvider == provider && _fileCount == provider.Files.Count)
                {
                    notes.UnionWith(_cachedNotes);
                    return _cached;
                }

                var buildNotes = new HashSet<EAudioLookupNote>();
                var index = Build(provider, getWwise, cancellationToken, buildNotes);

                _cached = index;
                _cachedNotes = buildNotes;
                _provider = new WeakReference<AbstractVfsFileProvider>(provider);
                _fileCount = provider.Files.Count;
                notes.UnionWith(buildNotes);
                return index;
            }
        }

        private static WwiseEventIndex Build(AbstractVfsFileProvider provider, Func<WwiseProvider> getWwise, CancellationToken cancellationToken, HashSet<EAudioLookupNote> notes)
        {
            var candidates = FindEventCandidates(provider, cancellationToken);
            var entries = new ConcurrentBag<(uint Id, WwiseEvent Event, string Language)>();
            var needsSoundBanks = new ConcurrentBag<(WwiseEvent Event, UAkAudioEvent Export)>();
            var eventCount = 0;

            Parallel.ForEach(candidates, new ParallelOptions { CancellationToken = cancellationToken }, file =>
            {
                try
                {
                    if (!provider.TryLoadPackage(file, out var package)) return;
                    foreach (var export in package.GetExports())
                    {
                        if (export is not UAkAudioEvent audioEvent) continue;

                        Interlocked.Increment(ref eventCount);
                        var wwiseEvent = new WwiseEvent(file, audioEvent.Name);
                        if (audioEvent.EventCookedData is not { } cooked)
                        {
                            // older integrations only have the event id, the media are in the soundbank hierarchy
                            needsSoundBanks.Add((wwiseEvent, audioEvent));
                            continue;
                        }

                        var usesSoundBanks = false;
                        foreach (var (language, eventData) in cooked.EventLanguageMap)
                        {
                            if (eventData is not { } data) continue;
                            foreach (var media in EnumerateMedia(data))
                                entries.Add((media.MediaId, wwiseEvent, language.LanguageName.Text));
                            usesSoundBanks |= HasEventSoundBanks(data);
                        }

                        // media stored inside a soundbank aren't listed in the cooked data
                        if (usesSoundBanks) needsSoundBanks.Add((wwiseEvent, audioEvent));
                    }
                }
                catch (Exception e)
                {
                    Log.Debug(e, "Failed to index Wwise events of {Path}", file.Path);
                }
            });

            if (!needsSoundBanks.IsEmpty)
                ResolveThroughSoundBanks(getWwise, needsSoundBanks, entries, notes, cancellationToken);

            var media = new Dictionary<uint, List<(WwiseEvent Event, string Language)>>();
            foreach (var (id, wwiseEvent, language) in entries)
            {
                if (id == 0) continue;
                if (!media.TryGetValue(id, out var list))
                    media[id] = list = [];
                list.Add((wwiseEvent, language));
            }

            Log.Information("Indexed {EventCount} Wwise events ({MediaCount} media) from {CandidateCount} packages", eventCount, media.Count, candidates.Count);
            return new WwiseEventIndex { Media = media, EventCount = eventCount };
        }

        private static void ResolveThroughSoundBanks(Func<WwiseProvider> getWwise, IEnumerable<(WwiseEvent Event, UAkAudioEvent Export)> events,
            ConcurrentBag<(uint Id, WwiseEvent Event, string Language)> entries, HashSet<EAudioLookupNote> notes, CancellationToken cancellationToken)
        {
            WwiseProvider wwise;
            try
            {
                wwise = getWwise();
            }
            catch (Exception e)
            {
                Log.Warning(e, "Wwise soundbanks couldn't be loaded, skipping soundbank resolution");
                notes.Add(EAudioLookupNote.WwiseBanksUnavailable);
                return;
            }

            // WwiseProvider caches as it goes and isn't thread safe
            foreach (var (wwiseEvent, export) in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // sounds resolved through the hierarchy are named "{Event} ({MediaId})"
                    foreach (var sound in wwise.ExtractAudioEventSounds(export))
                    {
                        if (TryParseMediaId(sound.OutputPath.SubstringAfterLast('/'), out var id))
                            entries.Add((id, wwiseEvent, null));
                    }
                }
                catch (Exception e)
                {
                    Log.Debug(e, "Failed to resolve the soundbank media of {Event}", wwiseEvent.Name);
                }
            }
        }

        /// <summary>
        /// the asset registry knows every event without loading anything, fall back to where events usually live
        /// </summary>
        private static List<GameFile> FindEventCandidates(AbstractVfsFileProvider provider, CancellationToken cancellationToken)
        {
            var fromRegistry = new Dictionary<string, GameFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var registryFile in provider.Files.Values.Where(f => f.Extension == "bin" && f.Name.Contains("AssetRegistry", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var registry = new FAssetRegistryState(registryFile.CreateReader());
                    foreach (var asset in registry.PreallocatedAssetDataBuffers)
                    {
                        if (!asset.AssetClass.Text.EndsWith("AkAudioEvent", StringComparison.OrdinalIgnoreCase)) continue;
                        if (provider.TryGetGameFile(asset.PackageName.Text, out var file))
                            fromRegistry.TryAdd(file.Path, file);
                    }
                }
                catch (Exception e)
                {
                    Log.Warning(e, "Failed to read {Path} while looking for Wwise events", registryFile.Path);
                }
            }

            if (fromRegistry.Count > 0)
                return [.. fromRegistry.Values];

            return provider.Files.Values.Where(f =>
                f.Extension == "uasset" &&
                (f.Path.Contains("/WwiseAudio/", StringComparison.OrdinalIgnoreCase) ||
                 f.Path.Contains("/Wwise/", StringComparison.OrdinalIgnoreCase) ||
                 f.Name.StartsWith("Play_", StringComparison.OrdinalIgnoreCase) ||
                 f.Name.StartsWith("Stop_", StringComparison.OrdinalIgnoreCase))).ToList();
        }
    }

    #endregion

    #region package referencers

    private sealed class Node
    {
        public required GameFile File { get; init; }
        public required int Depth { get; init; }
        public required string Via { get; init; }
    }

    /// <summary>
    /// breadth first over the container headers, each level is one pass over every store entry
    /// </summary>
    private static void FindReferencers(AbstractVfsFileProvider provider, List<(GameFile File, string Label)> seeds, AudioReverseLookupResult result, CancellationToken cancellationToken)
    {
        var readers = provider.MountedVfs.OfType<IoStoreReader>()
            .Where(r => r.ContainerHeader is { StoreEntries.Length: > 0 })
            .ToArray();
        if (readers.Length == 0)
        {
            result.Notes.Add(EAudioLookupNote.NotIoStore);
            return;
        }

        var labels = new Dictionary<FPackageId, string>();
        foreach (var (file, label) in seeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (provider.TryLoadPackage(file, out var package))
                    labels.TryAdd(FPackageId.FromName(package.Name), label);
            }
            catch (Exception e)
            {
                Log.Debug(e, "Failed to load {Path} to search its referencers", file.Path);
            }
        }

        var visited = new HashSet<FPackageId>(labels.Keys);
        var frontier = labels;
        var found = new List<Node>();

        for (var depth = 1; depth <= MaxDepth && frontier.Count > 0; depth++)
        {
            var level = new ConcurrentDictionary<FPackageId, (GameFile File, FPackageId Via)>();
            Parallel.ForEach(readers, new ParallelOptions { CancellationToken = cancellationToken }, reader =>
            {
                var header = reader.ContainerHeader!;
                for (var i = 0; i < header.StoreEntries.Length; i++)
                {
                    foreach (var imported in header.StoreEntries[i].ImportedPackages)
                    {
                        if (!frontier.ContainsKey(imported)) continue;

                        var packageId = header.PackageIds[i];
                        if (!visited.Contains(packageId) && reader.PackageIdIndex.TryGetValue(packageId, out var file))
                            level.TryAdd(packageId, (file, imported));
                        break;
                    }
                }
            });

            var next = new Dictionary<FPackageId, string>();
            foreach (var (packageId, (file, via)) in level.OrderBy(kv => kv.Value.File.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (found.Count >= MaxUsages)
                {
                    result.Notes.Add(EAudioLookupNote.Truncated);
                    break;
                }

                visited.Add(packageId);
                found.Add(new Node { File = file, Depth = depth, Via = frontier[via] });

                // levels are what everything else hangs off, following them only adds noise
                if (!file.Extension.Equals("umap", StringComparison.OrdinalIgnoreCase))
                    next[packageId] = file.NameWithoutExtension;
            }

            if (level.Count > MaxNewPerLevel && depth < MaxDepth)
            {
                // a widely shared asset, the next level would be most of the game
                result.Notes.Add(EAudioLookupNote.Truncated);
                break;
            }

            frontier = next;
        }

        var usages = new AudioUsage[found.Count];
        Parallel.For(0, found.Count, new ParallelOptions { CancellationToken = cancellationToken }, i =>
        {
            var node = found[i];
            usages[i] = new AudioUsage
            {
                File = node.File,
                ClassName = GetMainClassName(provider, node.File),
                Name = node.File.NameWithoutExtension,
                Depth = node.Depth,
                Via = node.Via
            };
        });

        // an event found through the media can also import the sound directly, keep the event row
        var known = result.Usages.Select(u => u.File.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        result.Usages.AddRange(usages.Where(u => !known.Contains(u.File.Path)));
    }

    private static string GetMainClassName(AbstractVfsFileProvider provider, GameFile file)
    {
        try
        {
            if (!provider.TryLoadPackage(file, out var package)) return string.Empty;
            var name = file.NameWithoutExtension;
            switch (package)
            {
                case IoPackage ioPackage when ioPackage.ExportMap.Length > 0:
                {
                    // cooked blueprints only keep the generated "_C" class
                    var names = ioPackage.ExportMap.Select(e => ioPackage.CreateFNameFromMappedName(e.ObjectName).Text).ToArray();
                    var index = Array.FindIndex(names, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (index < 0) index = Array.FindIndex(names, n => n.Equals(name + "_C", StringComparison.OrdinalIgnoreCase));
                    return ioPackage.ResolveObjectIndex(ioPackage.ExportMap[Math.Max(0, index)].ClassIndex)?.Name.Text ?? string.Empty;
                }
                case Package legacy when legacy.ExportMap.Length > 0:
                    return (legacy.ExportMap.FirstOrDefault(e => e.ObjectName.Text.Equals(name, StringComparison.OrdinalIgnoreCase))
                            ?? legacy.ExportMap.FirstOrDefault(e => e.ObjectName.Text.Equals(name + "_C", StringComparison.OrdinalIgnoreCase))
                            ?? legacy.ExportMap[0]).ClassName;
            }
        }
        catch (Exception e)
        {
            Log.Debug(e, "Failed to read the class of {Path}", file.Path);
        }

        return string.Empty;
    }

    #endregion
}
