using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Verse;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;
using Serilog;

namespace FModel.Services.Verse;

/// <summary>
/// Recovers the Verse declarations of a cooked package by walking its Verse type exports.
/// </summary>
public static class VerseDeclarationRecovery
{
    private static readonly HashSet<string> VerseTypeClasses =
        ["VerseClass", "VerseStruct", "VerseEnum", "SolarisGeneratedEnum"];
    private const uint SolClassFlagModule = 1 << 3;
    private static readonly ConditionalWeakTable<byte[], DebugSnapshot> RawDebugDataCache = new();

    private sealed record VerseType(string RelativePath, string CookedName, UObject Type);
    private sealed record SourceAttribution(string SourcePath, uint Row);
    private sealed record DebugSnapshot(string?[] Snippets, List<DebugFunction> Functions);
    private sealed record DebugFunction(string FunctionPathName, List<DebugTracepoint> Tracepoints);
    private readonly record struct DebugTracepoint(uint ByteCodeOffset, int SnippetIndex, uint Row, uint Column);

    /// <summary>
    /// true when the package holds anything this can recover, i.e. at least one cooked Verse type
    /// </summary>
    public static bool HasVerseTypes(IPackage package)
    {
        for (var i = 0; i < package.ExportMapLength; i++)
        {
            var pointer = new FPackageIndex(package, i + 1).ResolvedObject;
            if (pointer?.Class?.Name.Text is { } name && VerseTypeClasses.Contains(name))
                return true;
        }

        return false;
    }

    public static string FromPackage(IPackage package, bool withBodies = false) =>
        FromPackage(package, withBodies ? VerseBodyMode.Rebuilt : VerseBodyMode.None);

    public static string FromPackage(IPackage package, VerseBodyMode mode)
    {
        // the task classes suspends functions are lowered into were never declared in the source;
        // their bodies are written where the suspends function is
        // the structs the compiler makes for tuple types are not declarations either, except to look at the cook
        var types = Collect(package)
            .Where(type => !IsLoweredTaskType(type) &&
                           (mode == VerseBodyMode.Listing || !type.CookedName.StartsWith("tuple_", StringComparison.Ordinal)))
            .ToList();
        if (types.Count == 0) return VerseDeclarationWriter.Header(package.Name) + "# no cooked Verse types in this package\n";

        var package_ = PackageVersePath(types);
        var writer = new VerseDeclarationWriter(package_, mode, Digests(package, mode));
        var builder = new StringBuilder(VerseDeclarationWriter.Header(package_, mode));

        var paths = types.Select(t => t.RelativePath).ToHashSet(StringComparer.Ordinal);
        foreach (var type in types.OrderBy(t => t.RelativePath, StringComparer.Ordinal))
        {
            builder.AppendLine($"# {type.RelativePath}");
            builder.Append(writer.Write(type.Type, DepthOf(type.RelativePath, paths)));
            builder.AppendLine();
        }

        return VerseDeclarationWriter.Finish(builder.ToString(), package_, mode);
    }

    /// <summary>
    /// Original .verse source paths retained by the cooked Verse debug-data snippet table.
    /// A UEFN island normally stores many source files inside one Content/_Verse.uasset.
    /// </summary>
    public static IReadOnlyList<string> SourceFiles(IPackage package, byte[]? rawPackage = null)
    {
        var debugData = GetDebugData(package, rawPackage);
        if (debugData is null)
            return rawPackage is null ? [] : ScanSourcePaths(rawPackage);

        var result = new List<string>(debugData.Snippets.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourcePath in debugData.Snippets)
        {
            if (sourcePath is null || !seen.Add(sourcePath)) continue;
            result.Add(sourcePath);
        }

        // The reference reconstruction also exposes four synthetic files for reflected Verse types
        // that have no VerseDebugData tracepoint and therefore cannot be tied to an original source
        // filename. Keep them beside the dominant source module instead of silently losing them.
        var syntheticFolder = DominantSourceFolder(result);
        if (!string.IsNullOrEmpty(syntheticFolder))
        {
            foreach (var name in new[] { "shared_classes.verse", "shared_structs.verse", "shared_enums.verse", "_unplaced_modules.verse" })
            {
                var path = $"{syntheticFolder}/{name}";
                if (seen.Add(path)) result.Add(path);
            }
        }

        return result;
    }

    /// <summary>
    /// Reconstructs only the declarations and bodies attributed to one original .verse source file.
    /// FunctionPathName identifies the owning cooked type and each tracepoint identifies the source
    /// snippet it came from, so no filename guessing is required.
    /// </summary>
    public static string FromPackageSource(IPackage package, string sourcePath, bool withBodies = true,
        byte[]? rawPackage = null) =>
        FromPackageSource(package, sourcePath, withBodies ? VerseBodyMode.Rebuilt : VerseBodyMode.None, rawPackage);

    public static string FromPackageSource(IPackage package, string sourcePath, VerseBodyMode mode, byte[]? rawPackage = null)
    {
        if (TryGetSyntheticKind(sourcePath, out var syntheticKind))
            return FromPackageSynthetic(package, sourcePath, syntheticKind, mode, rawPackage);

        // Resolve source ownership from debug data first. This avoids deserializing all ~thousands
        // of Verse exports just to open one source file, and prevents an unrelated malformed export
        // from making every recovered .verse file appear empty.
        var debugData = GetDebugData(package, rawPackage);
        var cookedAttribution = BuildCookedSourceAttribution(debugData);
        var functionAttribution = BuildCookedFunctionAttribution(debugData);
        var functionsForSource = functionAttribution
            .Select(pair => new
            {
                Owner = pair.Key,
                Functions = pair.Value
                    .Where(function => function.Value.SourcePath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(function => function.Key, function => function.Value, StringComparer.Ordinal)
            })
            .Where(pair => pair.Functions.Count > 0)
            .ToDictionary(pair => pair.Owner, pair => pair.Functions, StringComparer.Ordinal);

        var wantedCookedNames = cookedAttribution
            .Where(pair => pair.Value.SourcePath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase) &&
                           !pair.Key.StartsWith("task_", StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
        wantedCookedNames.UnionWith(functionsForSource.Keys.Where(owner =>
            !owner.StartsWith("task_", StringComparison.Ordinal)));

        if (wantedCookedNames.Count == 0)
            Log.Warning("Verse source {SourcePath} has no non-task cooked owners in recovered DebugData", sourcePath);

        var types = Collect(package, wantedCookedNames);
        var packagePath = PackageVersePath(types);
        var writer = new VerseDeclarationWriter(packagePath, mode, Digests(package, mode));
        var builder = new StringBuilder(VerseDeclarationWriter.Header(packagePath, mode));

        var selected = types
            .Where(type => wantedCookedNames.Contains(type.CookedName) && !IsLoweredTaskType(type))
            .OrderBy(type => SourceRow(type.CookedName, sourcePath, cookedAttribution, functionsForSource))
            .ThenBy(type => type.RelativePath, StringComparer.Ordinal)
            .ToList();

        if (selected.Count == 0)
        {
            builder.AppendLine($"# {sourcePath}");
            builder.AppendLine("# The cooked debug data retained this source filename, but no reflected Verse type could be attributed to it.");
            return VerseDeclarationWriter.Finish(builder.ToString(), packagePath, mode);
        }

        var selectedPaths = selected.Select(type => type.RelativePath).ToHashSet(StringComparer.Ordinal);
        foreach (var type in selected)
        {
            var row = SourceRow(type.CookedName, sourcePath, cookedAttribution, functionsForSource);
            builder.AppendLine($"# ~line {row} of {sourcePath}");

            functionsForSource.TryGetValue(type.CookedName, out var functions);
            var includeFields = cookedAttribution.TryGetValue(type.CookedName, out var typeSource) &&
                                typeSource.SourcePath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase);
            functionAttribution.TryGetValue(type.CookedName, out var allAttributedFunctions);
            builder.Append(writer.Write(type.Type, DepthOf(type.RelativePath, selectedPaths),
                functions?.Keys.ToHashSet(StringComparer.Ordinal), includeFields,
                allAttributedFunctions?.Keys.ToHashSet(StringComparer.Ordinal)));
            builder.AppendLine();
        }

        return VerseDeclarationWriter.Finish(builder.ToString(), packagePath, mode);
    }

    private static VerseDigestIndex? Digests(IPackage package, VerseBodyMode mode) =>
        mode == VerseBodyMode.Listing ? null : VerseDigestIndex.ForPackage(package);

    private static uint SourceRow(string cookedName, string sourcePath,
        IReadOnlyDictionary<string, SourceAttribution> typeAttribution,
        IReadOnlyDictionary<string, Dictionary<string, SourceAttribution>> functionsForSource)
    {
        if (typeAttribution.TryGetValue(cookedName, out var typeSource) &&
            typeSource.SourcePath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return typeSource.Row;
        }

        return functionsForSource.TryGetValue(cookedName, out var functions) && functions.Count > 0
            ? functions.Values.Min(function => function.Row)
            : 0;
    }

    private enum SyntheticKind
    {
        Classes,
        Structs,
        Enums,
        Modules
    }

    private static bool TryGetSyntheticKind(string sourcePath, out SyntheticKind kind)
    {
        switch (sourcePath.SubstringAfterLast('/'))
        {
            case "shared_classes.verse":
                kind = SyntheticKind.Classes;
                return true;
            case "shared_structs.verse":
                kind = SyntheticKind.Structs;
                return true;
            case "shared_enums.verse":
                kind = SyntheticKind.Enums;
                return true;
            case "_unplaced_modules.verse":
                kind = SyntheticKind.Modules;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static string FromPackageSynthetic(IPackage package, string sourcePath, SyntheticKind kind,
        VerseBodyMode mode, byte[]? rawPackage)
    {
        var attribution = BuildCookedSourceAttribution(package, rawPackage);
        var folder = sourcePath.Contains('/') ? sourcePath.SubstringBeforeLast('/') : string.Empty;
        var prefix = string.IsNullOrEmpty(folder) ? string.Empty : folder + "-";
        var wanted = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < package.ExportMapLength; i++)
        {
            var pointer = new FPackageIndex(package, i + 1).ResolvedObject;
            if (pointer?.Class?.Name.Text is not { } className || !VerseTypeClasses.Contains(className)) continue;

            var cookedName = pointer.Name.Text;
            if (cookedName.StartsWith("task_", StringComparison.Ordinal) || attribution.ContainsKey(cookedName)) continue;

            var tail = !string.IsNullOrEmpty(prefix) && cookedName.StartsWith(prefix, StringComparison.Ordinal)
                ? cookedName[prefix.Length..]
                : cookedName;
            var directInFolder = string.IsNullOrEmpty(prefix)
                ? !tail.Contains('-')
                : cookedName.StartsWith(prefix, StringComparison.Ordinal) && !tail.Contains('-');

            if (kind is SyntheticKind.Structs && className == "VerseStruct" && directInFolder)
                wanted.Add(cookedName);
            else if (kind is SyntheticKind.Enums && className is "VerseEnum" or "SolarisGeneratedEnum" && directInFolder)
                wanted.Add(cookedName);
            else if (kind is SyntheticKind.Classes or SyntheticKind.Modules && className == "VerseClass" &&
                     (directInFolder || !cookedName.Contains('-')))
                wanted.Add(cookedName);
        }

        var candidates = Collect(package, wanted);
        var selected = candidates.Where(type => type.Type switch
        {
            UEnum => kind == SyntheticKind.Enums,
            UClass @class => kind == SyntheticKind.Modules
                ? (@class.GetOrDefault<uint>("SolClassFlags") & SolClassFlagModule) != 0
                : kind == SyntheticKind.Classes && (@class.GetOrDefault<uint>("SolClassFlags") & SolClassFlagModule) == 0,
            UScriptStruct => kind == SyntheticKind.Structs,
            _ => false
        }).OrderBy(type => type.RelativePath, StringComparer.Ordinal).ToList();

        var packagePath = PackageVersePath(selected);
        var builder = new StringBuilder(VerseDeclarationWriter.Header(packagePath, mode));
        builder.AppendLine($"# {sourcePath}");
        builder.AppendLine("# These reflected types have no VerseDebugData source tracepoint; their declarations are recovered exactly, but their original home file is unknown.");
        builder.AppendLine();

        if (selected.Count == 0)
        {
            builder.AppendLine("# no matching unattributed reflected Verse types");
            return VerseDeclarationWriter.Finish(builder.ToString(), packagePath, mode);
        }

        var writer = new VerseDeclarationWriter(packagePath, mode, Digests(package, mode));
        var paths = selected.Select(type => type.RelativePath).ToHashSet(StringComparer.Ordinal);
        foreach (var type in selected)
        {
            builder.Append(writer.Write(type.Type, DepthOf(type.RelativePath, paths)));
            builder.AppendLine();
        }

        return VerseDeclarationWriter.Finish(builder.ToString(), packagePath, mode);
    }

    private static string DominantSourceFolder(IEnumerable<string> sourcePaths) => sourcePaths
        .Where(path => path.Contains('/'))
        .Select(path => path[..path.IndexOf('/')])
        .GroupBy(folder => folder, StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(group => group.Count())
        .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.Key)
        .FirstOrDefault() ?? string.Empty;

    private static Dictionary<string, SourceAttribution> BuildCookedSourceAttribution(IPackage package, byte[]? rawPackage)
        => BuildCookedSourceAttribution(GetDebugData(package, rawPackage));

    private static Dictionary<string, SourceAttribution> BuildCookedSourceAttribution(DebugSnapshot? debugData)
    {
        var result = new Dictionary<string, SourceAttribution>(StringComparer.Ordinal);
        if (debugData is null) return result;

        var snippetPaths = debugData.Snippets;
        var votes = new Dictionary<string, Dictionary<int, (int Count, uint FirstRow)>>(StringComparer.Ordinal);

        foreach (var function in debugData.Functions)
        {
            var owner = FunctionOwner(function.FunctionPathName);
            if (owner is null) continue;

            if (!votes.TryGetValue(owner, out var ownerVotes))
            {
                ownerVotes = new Dictionary<int, (int Count, uint FirstRow)>();
                votes.Add(owner, ownerVotes);
            }

            foreach (var tracepoint in function.Tracepoints)
            {
                if (tracepoint.SnippetIndex < 0 || tracepoint.SnippetIndex >= snippetPaths.Length ||
                    snippetPaths[tracepoint.SnippetIndex] is null)
                {
                    continue;
                }

                if (ownerVotes.TryGetValue(tracepoint.SnippetIndex, out var vote))
                {
                    ownerVotes[tracepoint.SnippetIndex] =
                        (vote.Count + 1, Math.Min(vote.FirstRow, tracepoint.Row));
                }
                else
                {
                    ownerVotes.Add(tracepoint.SnippetIndex, (1, tracepoint.Row));
                }
            }
        }

        foreach (var (owner, ownerVotes) in votes)
        {
            if (ownerVotes.Count == 0) continue;

            var best = ownerVotes
                .OrderByDescending(pair => pair.Value.Count)
                .ThenBy(pair => pair.Value.FirstRow)
                .First();
            var sourcePath = snippetPaths[best.Key];
            if (sourcePath is null) continue;

            result[owner] = new SourceAttribution(sourcePath, best.Value.FirstRow);
        }

        return result;
    }

    /// <summary>
    /// Maps each cooked function to its original source file. A single cooked VerseClass can own
    /// functions declared across many .verse files (especially a directory module), so type-level
    /// attribution alone would make every non-dominant source file appear empty.
    /// </summary>
    private static Dictionary<string, Dictionary<string, SourceAttribution>> BuildCookedFunctionAttribution(
        IPackage package, byte[]? rawPackage)
        => BuildCookedFunctionAttribution(GetDebugData(package, rawPackage));

    private static Dictionary<string, Dictionary<string, SourceAttribution>> BuildCookedFunctionAttribution(
        DebugSnapshot? debugData)
    {
        var result = new Dictionary<string, Dictionary<string, SourceAttribution>>(StringComparer.Ordinal);
        if (debugData is null) return result;

        foreach (var function in debugData.Functions)
        {
            var owner = FunctionOwner(function.FunctionPathName);
            var functionName = FunctionName(function.FunctionPathName);
            if (owner is null || functionName is null) continue;

            var best = function.Tracepoints
                .Where(tracepoint => tracepoint.SnippetIndex >= 0 &&
                                     tracepoint.SnippetIndex < debugData.Snippets.Length &&
                                     debugData.Snippets[tracepoint.SnippetIndex] is not null)
                .GroupBy(tracepoint => tracepoint.SnippetIndex)
                .Select(group => new
                {
                    SnippetIndex = group.Key,
                    Count = group.Count(),
                    FirstRow = group.Min(tracepoint => tracepoint.Row)
                })
                .OrderByDescending(candidate => candidate.Count)
                .ThenBy(candidate => candidate.FirstRow)
                .FirstOrDefault();
            if (best is null) continue;

            var sourcePath = debugData.Snippets[best.SnippetIndex];
            if (sourcePath is null) continue;

            if (!result.TryGetValue(owner, out var ownerFunctions))
            {
                ownerFunctions = new Dictionary<string, SourceAttribution>(StringComparer.Ordinal);
                result.Add(owner, ownerFunctions);
            }

            ownerFunctions[functionName] = new SourceAttribution(sourcePath, best.FirstRow);
        }

        return result;
    }

    private static DebugSnapshot? GetDebugData(IPackage package, byte[]? rawPackage)
    {
        // For recovered virtual .verse files we already have the complete cooked _Verse.uasset
        // bytes. Prefer the raw Solaris debug-data parser before touching the $DebugData UObject.
        // Current Fortnite/UE6 packages use a VerseDebugData serialization layout that the generic
        // CUE4Parse UVerseDebugData reader does not understand yet (it currently tries to read an
        // obsolete bool and logs "Invalid bool value (2)"). The raw parser below has been validated
        // against this package and recovers all 84 snippets and 2385 function records.
        if (rawPackage is not null)
        {
            lock (RawDebugDataCache)
            {
                if (RawDebugDataCache.TryGetValue(rawPackage, out var cached))
                    return cached;
            }

            if (TryParseRawDebugData(package, rawPackage, out var rawDebugData))
            {
                lock (RawDebugDataCache) RawDebugDataCache.AddOrUpdate(rawPackage, rawDebugData!);
                return rawDebugData;
            }
        }

        // Do not enumerate every export of a large _Verse package just to find the one debug-data
        // object when raw bytes are unavailable. The cooked object has the stable name "$DebugData"
        // and class VerseDebugData.
        for (var i = 0; i < package.ExportMapLength; i++)
        {
            var pointer = new FPackageIndex(package, i + 1).ResolvedObject;
            if (pointer is null) continue;
            var objectName = pointer.Name.Text;
            var className = pointer.Class?.Name.Text;
            if (!objectName.Equals("$DebugData", StringComparison.Ordinal) &&
                !string.Equals(className, "VerseDebugData", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (pointer.Object?.Value is FixedVerseDebugData { DebugData: not null } debug)
                    return Snapshot(debug.DebugData);
            }
            catch
            {
                // Some IoStore/version combinations keep the payload intact but cannot deserialize
                // VerseDebugData through the registered UObject type. The raw parser below handles it.
            }
        }

        return null;
    }

    private static DebugSnapshot Snapshot(UVerseDebugData.FSolarisPackageDebugData debugData)
    {
        var snippets = debugData.Snippets.Select(snippet => DecodeSnippetPath(snippet.Data)).ToArray();
        var functions = new List<DebugFunction>(debugData.Functions.Length);
        foreach (var function in debugData.Functions)
        {
            var tracepoints = new List<DebugTracepoint>(function.Tracepoints.Length);
            foreach (var tracepoint in function.Tracepoints)
            {
                tracepoints.Add(new DebugTracepoint(tracepoint.ByteCodeOffset, tracepoint.SnippetIndex,
                    tracepoint.Locus.Row, tracepoint.Locus.Column));
            }

            functions.Add(new DebugFunction(function.FunctionPathName, tracepoints));
        }

        return new DebugSnapshot(snippets, functions);
    }

    /// <summary>
    /// Reads FSolarisPackageDebugData directly from the cooked package bytes. This is intentionally
    /// kept as a fallback because some IoStore package/version combinations expose $DebugData as a
    /// generic UObject even though its serialized payload is intact.
    /// </summary>
    private static bool TryParseRawDebugData(IPackage package, byte[] data, out DebugSnapshot? snapshot)
    {
        snapshot = null;
        ReadOnlySpan<byte> bytes = data;
        ReadOnlySpan<byte> marker = ".verse"u8;
        var searchOffset = 0;

        while (searchOffset < bytes.Length)
        {
            var relative = bytes[searchOffset..].IndexOf(marker);
            if (relative < 0) break;

            var markerStart = searchOffset + relative;
            var markerEnd = markerStart + marker.Length;
            var minimumStart = Math.Max(8, markerStart - 1024);

            // The first snippet is serialized as: int32 SnippetCount, int32 ByteCount, UTF-8 bytes.
            // Walk backwards from the first .verse suffix until the length/count prefix matches.
            for (var pathStart = markerStart; pathStart >= minimumStart; pathStart--)
            {
                var pathLength = markerEnd - pathStart;
                if (pathStart < 8 || ReadInt32(bytes, pathStart - 4) != pathLength) continue;

                var snippetCount = ReadInt32(bytes, pathStart - 8);
                if (snippetCount is <= 0 or > 4096) continue;

                if (TryParseRawDebugDataAt(bytes, pathStart - 8, snippetCount, out snapshot) ||
                    TryParseCompactRawDebugDataAt(package, bytes, pathStart - 8, snippetCount, out snapshot))
                    return true;
            }

            searchOffset = markerEnd;
        }

        return false;
    }

    private static bool TryReadSnippets(ReadOnlySpan<byte> data, ref int offset, int snippetCount,
        out string?[] snippets)
    {
        snippets = new string?[snippetCount];
        for (var i = 0; i < snippetCount; i++)
        {
            if (!TryReadInt32(data, ref offset, out var byteCount) || byteCount is <= 0 or > 4096 ||
                offset > data.Length - byteCount)
            {
                return false;
            }

            snippets[i] = DecodeSnippetPath(data.Slice(offset, byteCount));
            if (snippets[i] is null) return false;
            offset += byteCount;
        }

        return true;
    }

    /// <summary>
    /// Fortnite 42.20+ layout. Function path strings were replaced by a CityHash64 id table and the
    /// per-function tracepoints were moved into two shared pools:
    ///   int32 SnippetCount, { int32 ByteCount, UTF-8 path }[]
    ///   int32 FunctionCount, { int32 Offset, int32 Count }[]   Offset = (PoolIndex &lt;&lt; 1) | IsWide
    ///   int32 NarrowCount, { uint16 ByteCodeOffset, uint16 SnippetIndex, uint16 Row, uint16 Column }[]
    ///   int32 WideCount, { uint32 ByteCodeOffset, int32 SnippetIndex, uint32 Row, uint32 Column }[]
    ///   int32 IdCount, { uint64 CityHash64(UTF-16 function path name), int32 FunctionIndex }[]
    /// The function path names are recovered by hashing the path names of the package's own exports.
    /// </summary>
    private static bool TryParseCompactRawDebugDataAt(IPackage package, ReadOnlySpan<byte> data, int start,
        int snippetCount, out DebugSnapshot? snapshot)
    {
        snapshot = null;
        var offset = start + 4;
        if (!TryReadSnippets(data, ref offset, snippetCount, out var snippets))
            return false;

        if (!TryReadInt32(data, ref offset, out var functionCount) || functionCount is < 0 or > 100_000 ||
            offset > data.Length - (long) functionCount * 8)
        {
            return false;
        }

        var ranges = new (int Offset, int Count)[functionCount];
        for (var i = 0; i < functionCount; i++)
        {
            ranges[i] = (ReadInt32(data, offset), ReadInt32(data, offset + 4));
            offset += 8;
            if (ranges[i].Offset < 0 || ranges[i].Count < 0) return false;
        }

        if (!TryReadInt32(data, ref offset, out var narrowCount) || narrowCount is < 0 or > 10_000_000 ||
            offset > data.Length - (long) narrowCount * 8)
        {
            return false;
        }

        var narrowStart = offset;
        offset += narrowCount * 8;

        if (!TryReadInt32(data, ref offset, out var wideCount) || wideCount is < 0 or > 10_000_000 ||
            offset > data.Length - (long) wideCount * 16)
        {
            return false;
        }

        var wideStart = offset;
        offset += wideCount * 16;

        if (!TryReadInt32(data, ref offset, out var idCount) || idCount != functionCount ||
            offset > data.Length - (long) idCount * 12)
        {
            return false;
        }

        var functionIds = new ulong[functionCount];
        var assigned = new bool[functionCount];
        for (var i = 0; i < idCount; i++)
        {
            var hash = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
            var functionIndex = ReadInt32(data, offset + 8);
            offset += 12;
            if (functionIndex < 0 || functionIndex >= functionCount || assigned[functionIndex]) return false;
            functionIds[functionIndex] = hash;
            assigned[functionIndex] = true;
        }

        var pathsByHash = FunctionPathsByHash(package);
        var functions = new List<DebugFunction>(functionCount);
        var unresolved = 0;
        for (var i = 0; i < functionCount; i++)
        {
            var (poolOffset, count) = ranges[i];
            var isWide = (poolOffset & 1) != 0;
            var poolIndex = poolOffset >> 1;
            if ((long) poolIndex + count > (isWide ? wideCount : narrowCount)) return false;

            var tracepoints = new List<DebugTracepoint>(count);
            for (var k = 0; k < count; k++)
            {
                DebugTracepoint tracepoint;
                if (isWide)
                {
                    var at = wideStart + (poolIndex + k) * 16;
                    tracepoint = new DebugTracepoint(
                        BinaryPrimitives.ReadUInt32LittleEndian(data[at..]),
                        BinaryPrimitives.ReadInt32LittleEndian(data[(at + 4)..]),
                        BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 8)..]),
                        BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 12)..]));
                }
                else
                {
                    var at = narrowStart + (poolIndex + k) * 8;
                    tracepoint = new DebugTracepoint(
                        BinaryPrimitives.ReadUInt16LittleEndian(data[at..]),
                        BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 2)..]),
                        BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 4)..]),
                        BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 6)..]));
                }

                if (tracepoint.SnippetIndex < 0 || tracepoint.SnippetIndex >= snippetCount) return false;
                tracepoints.Add(tracepoint);
            }

            if (!pathsByHash.TryGetValue(functionIds[i], out var functionPath))
            {
                unresolved++;
                continue;
            }

            functions.Add(new DebugFunction(functionPath, tracepoints));
        }

        if (unresolved > 0)
            Log.Warning("{Unresolved}/{Total} Verse debug function ids could not be matched to an export of {Package}",
                unresolved, functionCount, package.Name);

        snapshot = new DebugSnapshot(snippets, functions);
        return true;
    }

    private static Dictionary<ulong, string> FunctionPathsByHash(IPackage package)
    {
        var result = new Dictionary<ulong, string>();
        for (var i = 0; i < package.ExportMapLength; i++)
        {
            var pointer = new FPackageIndex(package, i + 1).ResolvedObject;
            if (pointer?.Class?.Name.Text is not ("Function" or "VerseFunction")) continue;

            var path = pointer.GetPathName();
            result.TryAdd(CityHash.CityHash64(Encoding.Unicode.GetBytes(path)), path);
        }

        return result;
    }

    private static bool TryParseRawDebugDataAt(ReadOnlySpan<byte> data, int start, int snippetCount,
        out DebugSnapshot? snapshot)
    {
        snapshot = null;
        var offset = start + 4;
        if (!TryReadSnippets(data, ref offset, snippetCount, out var snippets))
            return false;

        if (!TryReadInt32(data, ref offset, out var functionCount) || functionCount is < 0 or > 100_000)
            return false;

        var functions = new List<DebugFunction>(functionCount);
        for (var i = 0; i < functionCount; i++)
        {
            if (!TryReadFString(data, ref offset, out var functionPath) || string.IsNullOrEmpty(functionPath) ||
                !TryReadInt32(data, ref offset, out var tracepointCount) || tracepointCount is < 0 or > 1_000_000 ||
                offset > data.Length - (long) tracepointCount * 16)
            {
                return false;
            }

            var tracepoints = new List<DebugTracepoint>(tracepointCount);
            for (var traceIndex = 0; traceIndex < tracepointCount; traceIndex++)
            {
                var byteCodeOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
                var snippetIndex = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 4)..]);
                var row = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 8)..]);
                var column = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 12)..]);
                offset += 16;

                if (snippetIndex < 0 || snippetIndex >= snippetCount) return false;
                tracepoints.Add(new DebugTracepoint(byteCodeOffset, snippetIndex, row, column));
            }

            functions.Add(new DebugFunction(functionPath, tracepoints));
        }

        snapshot = new DebugSnapshot(snippets, functions);
        return true;
    }

    private static IReadOnlyList<string> ScanSourcePaths(byte[] data)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadOnlySpan<byte> bytes = data;
        ReadOnlySpan<byte> marker = ".verse"u8;
        var searchOffset = 0;

        while (searchOffset < bytes.Length)
        {
            var relative = bytes[searchOffset..].IndexOf(marker);
            if (relative < 0) break;

            var markerStart = searchOffset + relative;
            var markerEnd = markerStart + marker.Length;
            var pathStart = markerStart;
            while (pathStart > 0 && markerStart - pathStart < 1024 && IsSourcePathByte(bytes[pathStart - 1]))
                pathStart--;

            var path = DecodeSnippetPath(bytes[pathStart..markerEnd]);
            if (path is not null && seen.Add(path)) result.Add(path);
            searchOffset = markerEnd;
        }

        return result;
    }

    private static bool IsSourcePathByte(byte value) =>
        value is >= (byte) 'a' and <= (byte) 'z' or >= (byte) 'A' and <= (byte) 'Z' or
            >= (byte) '0' and <= (byte) '9' or (byte) '_' or (byte) '-' or (byte) '.' or
            (byte) '/' or (byte) '@';

    private static int ReadInt32(ReadOnlySpan<byte> data, int offset) =>
        offset >= 0 && offset <= data.Length - 4
            ? BinaryPrimitives.ReadInt32LittleEndian(data[offset..])
            : int.MinValue;

    private static bool TryReadInt32(ReadOnlySpan<byte> data, ref int offset, out int value)
    {
        value = 0;
        if (offset < 0 || offset > data.Length - 4) return false;
        value = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
        offset += 4;
        return true;
    }

    private static bool TryReadFString(ReadOnlySpan<byte> data, ref int offset, out string value)
    {
        value = string.Empty;
        if (!TryReadInt32(data, ref offset, out var length)) return false;
        if (length == 0) return true;

        if (length > 0)
        {
            if (length > 1_000_000 || offset > data.Length - length) return false;
            var byteLength = length;
            var textLength = data[offset + byteLength - 1] == 0 ? byteLength - 1 : byteLength;
            value = Encoding.UTF8.GetString(data.Slice(offset, textLength));
            offset += byteLength;
            return true;
        }

        var charCount = -(long) length;
        var utf16ByteCount = charCount * 2;
        if (charCount > 1_000_000 || utf16ByteCount > int.MaxValue || offset > data.Length - utf16ByteCount)
            return false;

        var bytesToDecode = (int) utf16ByteCount;
        var textBytes = bytesToDecode >= 2 && data[offset + bytesToDecode - 2] == 0 && data[offset + bytesToDecode - 1] == 0
            ? bytesToDecode - 2
            : bytesToDecode;
        value = Encoding.Unicode.GetString(data.Slice(offset, textBytes));
        offset += bytesToDecode;
        return true;
    }

    private static string? DecodeSnippetPath(byte[] data)
        => DecodeSnippetPath(data.AsSpan());

    private static string? DecodeSnippetPath(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return null;

        var path = Encoding.UTF8.GetString(data).TrimEnd('\0').Replace('\\', '/').TrimStart('/');
        if (!path.EndsWith(".verse", StringComparison.OrdinalIgnoreCase)) return null;

        var safeSegments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment is not "." and not "..")
            .ToArray();
        return safeSegments.Length == 0 ? null : string.Join('/', safeSegments);
    }

    private static string? FunctionOwner(string functionPath)
    {
        if (string.IsNullOrEmpty(functionPath)) return null;

        const string marker = "_Verse.";
        var markerIndex = functionPath.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0) return null;

        var ownerStart = markerIndex + marker.Length;
        var ownerEnd = functionPath.IndexOf(':', ownerStart);
        if (ownerEnd <= ownerStart) return null;
        return functionPath[ownerStart..ownerEnd];
    }

    private static string? FunctionName(string functionPath)
    {
        if (string.IsNullOrEmpty(functionPath)) return null;

        const string marker = "_Verse.";
        var markerIndex = functionPath.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0) return null;

        var separator = functionPath.IndexOf(':', markerIndex + marker.Length);
        return separator >= 0 && separator < functionPath.Length - 1 ? functionPath[(separator + 1)..] : null;
    }

    /// <summary>
    /// Suspends functions are lowered into compiler-generated task VerseClasses. They are useful for
    /// source attribution and bytecode recovery, but they were never declarations in the author's
    /// .verse file and therefore must not be emitted as top-level source types.
    /// </summary>
    private static bool IsLoweredTaskType(VerseType type) =>
        type.CookedName.StartsWith("task_", StringComparison.Ordinal);

    /// <summary>
    /// how deep a type sits, counting the enclosing modules that are themselves cooked into the package
    /// </summary>
    private static int DepthOf(string relativePath, HashSet<string> knownPaths)
    {
        var depth = 0;
        var parent = relativePath;
        while (parent.Contains('/'))
        {
            parent = parent.SubstringBeforeLast('/');
            if (knownPaths.Contains(parent)) depth++;
        }

        return depth;
    }

    private static List<VerseType> Collect(IPackage package, HashSet<string>? cookedNames = null)
    {
        var types = new List<VerseType>();

        if (cookedNames is not null)
        {
            // When DebugData has already identified exact cooked owner names, use the package's
            // own export-name lookup instead of reconstructing that lookup by walking FPackageIndex.
            // IoPackage.GetExportIndex resolves the Zen name map directly and is the canonical way
            // to find exports such as "TycoonCode-hotbar_widget".
            foreach (var cookedName in cookedNames)
            {
                var exportIndex = package.GetExportIndex(cookedName, StringComparison.Ordinal);
                if (exportIndex < 0)
                    exportIndex = package.GetExportIndex(cookedName, StringComparison.OrdinalIgnoreCase);
                if (exportIndex < 0)
                {
                    Log.Warning("Attributed Verse owner {CookedName} was not found in package {Package}", cookedName,
                        package.Name);
                    continue;
                }

                UObject? type;
                try
                {
                    type = package.GetExport(exportIndex);
                }
                catch (Exception e)
                {
                    Log.Warning(e, "Could not load attributed Verse export {CookedName} at index {ExportIndex}",
                        cookedName, exportIndex);
                    continue;
                }

                if (type is not (UStruct or UEnum))
                {
                    Log.Warning("Attributed Verse export {CookedName} loaded as unexpected type {Type}", cookedName,
                        type?.GetType().Name ?? "null");
                    continue;
                }

                var relative = type.GetOrDefault<string>("PackageRelativeVersePath");
                if (string.IsNullOrEmpty(relative)) relative = RelativePathFromCookedName(cookedName);
                if (string.IsNullOrEmpty(relative)) continue;

                types.Add(new VerseType(relative, cookedName, type));
            }

            return types;
        }

        for (var i = 0; i < package.ExportMapLength; i++)
        {
            var pointer = new FPackageIndex(package, i + 1).ResolvedObject;
            if (pointer is null) continue;

            var cookedName = pointer.Name.Text;
            if (pointer.Class?.Name.Text is not { } className || !VerseTypeClasses.Contains(className)) continue;

            if (pointer.Object?.Value is not { } type) continue;
            if (type is not (UStruct or UEnum)) continue;

            var relative = type.GetOrDefault<string>("PackageRelativeVersePath");
            if (string.IsNullOrEmpty(relative)) relative = RelativePathFromCookedName(cookedName);
            if (string.IsNullOrEmpty(relative)) continue;

            types.Add(new VerseType(relative, cookedName, type));
        }

        return types;
    }

    private static string RelativePathFromCookedName(string cookedName)
    {
        if (string.IsNullOrEmpty(cookedName)) return string.Empty;
        var name = cookedName.StartsWith("task_", StringComparison.Ordinal) ? cookedName[5..] : cookedName;
        var member = name.IndexOf('$');
        if (member >= 0) name = name[..member];
        return name.Replace('-', '/');
    }

    /// <summary>
    /// verse path of the package itself, e.g. /author@epic.com/Island, taken off any of its types
    /// </summary>
    private static string PackageVersePath(List<VerseType> types)
    {
        foreach (var type in types)
        {
            var mangled = type.Type.GetOrDefault<FName>("MangledPackageVersePath").Text;
            if (!string.IsNullOrEmpty(mangled)) return VerseMangling.UnmangleCasedName(mangled);
        }

        return string.Empty;
    }
}
