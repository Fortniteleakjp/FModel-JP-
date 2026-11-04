using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;
using FModel.Framework;
using Serilog;

namespace FModel.Services;

/// <summary>
/// 配信されるマッピングには、クラスのプロパティ定義が丸ごと抜けたものが混ざることがある
/// (Fortnite 43.00 の usmap では Skeleton など 20 クラスが 0 件)。unversioned なアセットはスキーマ無しでは読めず、
/// 例えば Skeleton は ReferenceSkeleton が null のまま返って 3D ビューアやエクスポートが落ちる。
/// 手元に残っている同じゲームの旧バージョンの usmap に定義があれば、その空のクラスだけを旧定義で補う。
/// </summary>
public static partial class MappingsBackfill
{
    /// <summary>旧 usmap の読み込みは 1 つ数秒かかるので、新しい順にこの数までに留める</summary>
    private const int MaxCandidates = 3;

    // ++Fortnite+Release-43.00-CL-..., FortniteRelease-39.40-..., Game-5.8.0-...+++Fortnite+Release-39.20-...
    [GeneratedRegex(@"([A-Za-z]+)\+?Release-(\d+)\.(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseVersionRegex();

    /// <returns>補ったクラス名 (補えなかった、または対象外なら空)</returns>
    public static IReadOnlyList<string> Apply(ITypeMappingsProvider provider, string mappingsPath)
    {
        try
        {
            return ApplyCore(provider, mappingsPath);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not backfill empty classes of '{Mappings}'", Path.GetFileName(mappingsPath));
            return [];
        }
    }

    private static IReadOnlyList<string> ApplyCore(ITypeMappingsProvider provider, string mappingsPath)
    {
        if (provider is not FileUsmapTypeMappingsProvider || provider.MappingsForGame is not { } mappings ||
            !TryParseRelease(Path.GetFileName(mappingsPath), out var project, out var version))
            return [];

        var empty = mappings.Types.Values
            .Where(type => type.PropertyCount == 0 && (type.Properties is null || type.Properties.Count == 0))
            .Select(type => type.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (empty.Count == 0) return [];

        var currentFullPath = Path.GetFullPath(mappingsPath);
        var candidates = new[] { Path.GetDirectoryName(currentFullPath), CacheManager.MappingsDirectory }
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(directory => new DirectoryInfo(directory).GetFiles("*.usmap"))
            .Where(file => !file.FullName.Equals(currentFullPath, StringComparison.OrdinalIgnoreCase))
            .Select(file => (File: file, Parsed: TryParseRelease(file.Name, out var p, out var v), Project: p, Version: v))
            .Where(c => c.Parsed && c.Project.Equals(project, StringComparison.OrdinalIgnoreCase) && c.Version < version)
            .OrderByDescending(c => c.Version)
            .ThenByDescending(c => c.File.LastWriteTimeUtc)
            .DistinctBy(c => c.Version)
            .Take(MaxCandidates)
            .Select(c => c.File)
            .ToList();

        var filled = new List<string>();
        foreach (var candidate in candidates)
        {
            var oldTypes = new FileUsmapTypeMappingsProvider(candidate.FullName).MappingsForGame?.Types;
            if (oldTypes is null) continue;

            var fromThisFile = new List<string>();
            foreach (var name in empty)
            {
                if (!mappings.Types.TryGetValue(name, out var current) ||
                    !oldTypes.TryGetValue(name, out var old) || old.PropertyCount == 0 ||
                    !string.Equals(current.SuperType, old.SuperType, StringComparison.OrdinalIgnoreCase))
                    continue;

                // 型の参照 (Super や構造体名) は今のマッピングから引くように Context を差し替える
                mappings.Types[name] = new Struct(mappings, current.Name, current.SuperType, new Dictionary<int, PropertyInfo>(old.Properties), old.PropertyCount);
                fromThisFile.Add(name);
            }

            if (fromThisFile.Count == 0) continue;

            empty.ExceptWith(fromThisFile);
            filled.AddRange(fromThisFile);
            Log.Warning("Mappings '{Mappings}' have no properties for {Count} classes, filled them from '{Fallback}': {Classes}",
                Path.GetFileName(mappingsPath), fromThisFile.Count, candidate.Name, string.Join(", ", fromThisFile.Order()));
        }

        return filled;
    }

    private static bool TryParseRelease(string fileName, out string project, out Version version)
    {
        var match = ReleaseVersionRegex().Match(fileName);
        if (!match.Success)
        {
            project = null;
            version = null;
            return false;
        }

        project = match.Groups[1].Value;
        version = new Version(int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
        return true;
    }
}
