using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Objects.GameplayTags;
using FModel.Framework;

namespace FModel.ViewModels;

/// <summary>
/// One segment of the gameplay tag hierarchy, e.g. "Storm" inside "Cosmetics.Set.Storm".
/// </summary>
public class GameplayTagNode : ViewModel
{
    public string Segment { get; init; }
    public string FullTag { get; init; }
    public List<GameplayTagNode> Children { get; } = [];

    /// <summary>Assets and config files declaring this exact tag.</summary>
    public List<string> Sources { get; } = [];

    public string Comment { get; set; }

    /// <summary>False for segments that only exist because a longer tag was declared.</summary>
    public bool IsDeclared => Sources.Count > 0;

    public int TotalCount => 1 + Children.Sum(child => child.TotalCount);
    public string Hint => Children.Count == 0 ? null : $"({Children.Count})";

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool Matches(string filter) =>
        FullTag != null && FullTag.Contains(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Copy of this subtree holding only branches that lead to a match, expanded.
    /// </summary>
    public GameplayTagNode Filtered(string filter)
    {
        var kept = new List<GameplayTagNode>();
        foreach (var child in Children)
        {
            var filtered = child.Filtered(filter);
            if (filtered != null) kept.Add(filtered);
        }

        if (kept.Count == 0 && !Matches(filter)) return null;

        var copy = new GameplayTagNode { Segment = Segment, FullTag = FullTag, Comment = Comment, IsExpanded = true };
        copy.Sources.AddRange(Sources);
        copy.Children.AddRange(kept);
        return copy;
    }
}

public class GameplayTagCollection
{
    public List<GameplayTagNode> Roots { get; init; } = [];
    public int TagCount { get; init; }
    public int SourceCount { get; init; }
    public List<string> ScannedSources { get; init; } = [];
}

/// <summary>
/// Collects every gameplay tag the build declares, the way the editor's tag manager lists them.
/// Config files and tag tables are the two places UE declares tags in, so only those are read.
/// </summary>
public static class GameplayTagCollector
{
    private static readonly Regex _tagListRegex = new(
        """\+GameplayTagList\s*=\s*\(\s*Tag\s*=\s*"(?'tag'[^"]+)"(?:\s*,\s*DevComment\s*=\s*"(?'comment'[^"]*)")?""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Assets whose name looks like a tag table, loading every data table of a build is not an option.</summary>
    private static readonly Regex _tagTableRegex = new("(gameplaytag|tagtable|tags?table)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static GameplayTagCollection Collect(AbstractVfsFileProvider provider, Action<string> progress, CancellationToken cancellationToken)
    {
        var tags = new Dictionary<string, (string Comment, List<string> Sources)>(StringComparer.OrdinalIgnoreCase);
        var scanned = new List<string>();

        void Add(string tag, string comment, string source)
        {
            if (string.IsNullOrWhiteSpace(tag)) return;

            tag = tag.Trim();
            if (!tags.TryGetValue(tag, out var existing))
                existing = tags[tag] = (comment, []);
            else if (string.IsNullOrEmpty(existing.Comment) && !string.IsNullOrEmpty(comment))
                tags[tag] = existing = (comment, existing.Sources);

            if (!existing.Sources.Contains(source)) existing.Sources.Add(source);
        }

        var files = provider.Files.Keys.ToArray();

        // 1. config files, this is where DefaultGameplayTags.ini declares the project's tags
        foreach (var path in files.Where(p => p.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string text;
            try
            {
                if (!provider.TrySaveAsset(path, out var data)) continue;
                text = Encoding.UTF8.GetString(data);
            }
            catch (Exception)
            {
                continue;
            }

            if (!text.Contains("GameplayTagList", StringComparison.OrdinalIgnoreCase)) continue;

            progress?.Invoke(path);
            scanned.Add(path);
            foreach (var (tag, comment) in ParseConfig(text))
                Add(tag, comment, path);
        }

        // 2. gameplay tag tables
        foreach (var path in files.Where(IsTagTableCandidate))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!provider.TryLoadPackage(path, out var package)) continue;

                var found = false;
                foreach (var table in package.GetExports().OfType<UDataTable>())
                {
                    foreach (var (rowName, row) in table.RowMap ?? [])
                    {
                        string tag = null;
                        if (row.TryGetValue(out FGameplayTag gameplayTag, "Tag") && !gameplayTag.TagName.IsNone)
                            tag = gameplayTag.TagName.Text;
                        else if (row.TryGetValue(out CUE4Parse.UE4.Objects.UObject.FName name, "Tag") && !name.IsNone)
                            tag = name.Text;
                        else if (rowName.Text.Contains('.'))
                            tag = rowName.Text; // some tables key the row by the tag itself

                        if (tag == null) continue;

                        found = true;
                        Add(tag, row.GetOrDefault<string>("DevComment"), path);
                    }
                }

                if (found)
                {
                    progress?.Invoke(path);
                    scanned.Add(path);
                }
            }
            catch (Exception)
            {
                // a table that fails to parse is not worth aborting the whole scan
            }
        }

        return BuildTree(tags, scanned);
    }

    /// <summary>
    /// Reads the +GameplayTagList entries of a config file, the way DefaultGameplayTags.ini declares tags.
    /// </summary>
    public static List<(string Tag, string Comment)> ParseConfig(string text)
    {
        var declarations = new List<(string, string)>();
        if (string.IsNullOrEmpty(text)) return declarations;

        foreach (Match match in _tagListRegex.Matches(text))
            declarations.Add((match.Groups["tag"].Value, match.Groups["comment"].Value));

        return declarations;
    }

    private static bool IsTagTableCandidate(string path) =>
        path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && _tagTableRegex.IsMatch(path);

    /// <summary>
    /// Builds the hierarchy out of flat declarations, creating the intermediate segments a longer tag implies.
    /// </summary>
    public static GameplayTagCollection Build(IEnumerable<(string Tag, string Comment, string Source)> declarations)
    {
        var tags = new Dictionary<string, (string Comment, List<string> Sources)>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<string>();
        foreach (var (tag, comment, source) in declarations)
        {
            if (string.IsNullOrWhiteSpace(tag)) continue;

            var key = tag.Trim();
            if (!tags.TryGetValue(key, out var existing))
                existing = tags[key] = (comment, []);
            else if (string.IsNullOrEmpty(existing.Comment) && !string.IsNullOrEmpty(comment))
                tags[key] = existing = (comment, existing.Sources);

            if (source != null && !existing.Sources.Contains(source)) existing.Sources.Add(source);
            if (source != null && !sources.Contains(source)) sources.Add(source);
        }

        return BuildTree(tags, sources);
    }

    private static GameplayTagCollection BuildTree(Dictionary<string, (string Comment, List<string> Sources)> tags, List<string> scanned)
    {
        var roots = new List<GameplayTagNode>();
        var lookup = new Dictionary<string, GameplayTagNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in tags.Keys.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        {
            var segments = tag.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var path = string.Empty;
            List<GameplayTagNode> siblings = roots;
            GameplayTagNode node = null;

            foreach (var segment in segments)
            {
                path = path.Length == 0 ? segment : $"{path}.{segment}";
                if (!lookup.TryGetValue(path, out node))
                {
                    node = new GameplayTagNode { Segment = segment, FullTag = path };
                    lookup[path] = node;
                    siblings.Add(node);
                }

                siblings = node.Children;
            }

            if (node == null) continue;

            var (comment, sources) = tags[tag];
            node.Comment = comment;
            foreach (var source in sources.Where(source => !node.Sources.Contains(source)))
                node.Sources.Add(source);
        }

        foreach (var root in roots) root.IsExpanded = true;

        return new GameplayTagCollection
        {
            Roots = roots,
            TagCount = tags.Count,
            SourceCount = scanned.Count,
            ScannedSources = scanned
        };
    }
}
