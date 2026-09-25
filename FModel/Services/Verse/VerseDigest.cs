using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Objects.UObject;
using Serilog;

namespace FModel.Services.Verse;

/// <summary>a declaration line of a Verse digest, with the comments and attributes written above it</summary>
public sealed class VerseDigestMember
{
    public required string Name { get; init; }
    /// <summary>the declaration as the digest writes it, without "= external {}" and the trailing colon</summary>
    public required string Declaration { get; init; }
    public IReadOnlyList<string> Comments { get; init; } = [];
    public IReadOnlyList<string> Attributes { get; init; } = [];
    /// <summary>functions: the parameters as (name, type), the receiver of an extension method first</summary>
    public IReadOnlyList<(string? Name, string Type)> Parameters { get; init; } = [];
    public bool IsExtension { get; init; }
}

public sealed class VerseDigestType
{
    public required VerseDigestMember Header { get; init; }
    public required string Kind { get; init; }
    /// <summary>the modules it is nested in, outermost first</summary>
    public required IReadOnlyList<string> Modules { get; init; }
    public Dictionary<string, VerseDigestMember> Fields { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<VerseDigestMember>> Functions { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The public API of Verse modules as their digests (*.digest.verse, what UEFN hands to the Verse
/// language server) spell it. A cooked package loses parameter names, declared field types that are
/// interfaces, specifiers and doc comments; the digest of the module still has them, so recovered
/// declarations that match a digest entry take those from it.
/// </summary>
public sealed class VerseDigestIndex
{
    private static readonly Regex TypeDeclaration = new(
        @"^(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<specs>(?:<[^>]*>)*)(?<params>\([^)]*\))?(?<specs2>(?:<[^>]*>)*)\s*:=\s*(?<kind>module|class|struct|enum|interface)\b",
        RegexOptions.Compiled);

    private static readonly Regex Identifier = new(@"^[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    private readonly Dictionary<string, List<VerseDigestType>> _types = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Sources => _sources;
    private readonly List<string> _sources = [];

    public bool IsEmpty => _types.Count == 0;

    #region shared index

    private static readonly object SharedLock = new();
    private static VerseDigestIndex? _shared;

    /// <summary>folders searched for *.digest.verse; set before first use, or left to the defaults</summary>
    public static IReadOnlyList<(string Path, int Depth)>? SearchRoots { get; set; }

    /// <summary>the folder a user drops digests into, under the FModel data folder</summary>
    public static string? UserFolder { get; set; }

    /// <summary>the digests found on this machine, loaded once</summary>
    public static VerseDigestIndex Shared
    {
        get
        {
            lock (SharedLock)
            {
                if (_shared is not null) return _shared;

                if (UserFolder is null)
                {
                    try
                    {
                        UserFolder = Path.Combine(Settings.UserSettings.Default.OutputDirectory, ".data", "VerseDigests");
                    }
                    catch
                    {
                        // without settings only the default locations are searched
                    }
                }

                var index = new VerseDigestIndex();
                foreach (var file in FindDigestFiles())
                {
                    try
                    {
                        index.Add(File.ReadAllText(file), file);
                    }
                    catch (Exception e)
                    {
                        Log.Warning(e, "Could not read Verse digest {File}", file);
                    }
                }

                Log.Information("Loaded {Count} Verse digest file(s)", index._sources.Count);
                return _shared = index;
            }
        }
    }

    /// <summary>
    /// the shared digests plus the ones a package carries itself ($Digest / $EpicInternalDigest),
    /// whose text a cook keeps in some builds and strips in others
    /// </summary>
    public static VerseDigestIndex ForPackage(IPackage package)
    {
        var shared = Shared;
        VerseDigestIndex? local = null;
        for (var i = 0; i < package.ExportMapLength; i++)
        {
            var pointer = new FPackageIndex(package, i + 1).ResolvedObject;
            if (pointer?.Class?.Name.Text != "VerseDigest") continue;

            try
            {
                if (pointer.Object?.Value?.GetOrDefault<byte[]>("DigestCode") is not { Length: > 0 } code) continue;
                local ??= shared.Copy();
                local.Add(Encoding.UTF8.GetString(code), $"{package.Name}.{pointer.Name.Text}");
            }
            catch (Exception e)
            {
                Log.Debug(e, "Could not read the digest {Digest} of {Package}", pointer.Name.Text, package.Name);
            }
        }

        return local ?? shared;
    }

    private VerseDigestIndex Copy()
    {
        var copy = new VerseDigestIndex();
        copy._sources.AddRange(_sources);
        foreach (var (name, types) in _types) copy._types[name] = [.. types];
        return copy;
    }

    /// <summary>drops the loaded digests so newly placed files are picked up</summary>
    public static void Reset()
    {
        lock (SharedLock) _shared = null;
    }

    private static IEnumerable<string> FindDigestFiles()
    {
        var roots = new List<(string Path, int Depth)>();
        if (UserFolder is { } user)
        {
            try
            {
                Directory.CreateDirectory(user);
            }
            catch
            {
                // a folder we cannot create is simply not searched
            }

            roots.Add((user, 8));
        }

        roots.AddRange(SearchRoots ?? DefaultRoots());

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, depth) in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.digest.verse", new EnumerationOptions
                {
                    RecurseSubdirectories = true, MaxRecursionDepth = depth, IgnoreInaccessible = true
                }).ToList();
            }
            catch (Exception e)
            {
                Log.Debug(e, "Could not search {Root} for Verse digests", root);
                continue;
            }

            foreach (var file in files)
                if (seen.Add(Path.GetFullPath(file))) yield return file;
        }
    }

    /// <summary>where UEFN keeps the digests of the Verse API and of the projects built with it</summary>
    private static IEnumerable<(string, int)> DefaultRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return (Path.Combine(local, "UnrealEditorFortnite"), 10);
        yield return (Path.Combine(documents, "Fortnite Projects"), 8);
    }

    #endregion

    #region parsing

    /// <summary>adds the declarations of one digest file</summary>
    public void Add(string text, string source)
    {
        _sources.Add(source);

        var stack = new List<(int Indent, string Name, VerseDigestType? Type)>();
        var comments = new List<string>();
        var attributes = new List<string>();
        var inBlockComment = false;

        foreach (var rawLine in text.Replace("\r", string.Empty).Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (inBlockComment)
            {
                if (trimmed.Contains("#>", StringComparison.Ordinal)) inBlockComment = false;
                continue;
            }

            if (trimmed.StartsWith("<#", StringComparison.Ordinal))
            {
                inBlockComment = !trimmed.Contains("#>", StringComparison.Ordinal);
                continue;
            }

            if (trimmed.Length == 0)
            {
                comments.Clear();
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                comments.Add(trimmed);
                continue;
            }

            if (trimmed.StartsWith("using", StringComparison.Ordinal) && trimmed.Contains('{')) continue;

            if (trimmed.StartsWith('@'))
            {
                attributes.Add(trimmed);
                continue;
            }

            var indent = rawLine.Length - rawLine.TrimStart().Length;
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            var owner = stack.Count > 0 ? stack[^1].Type : null;

            var match = TypeDeclaration.Match(trimmed);
            if (match.Success)
            {
                var name = match.Groups["name"].Value;
                var kind = match.Groups["kind"].Value;
                var type = new VerseDigestType
                {
                    Header = Member(name, trimmed.TrimEnd(':').TrimEnd(), comments, attributes),
                    Kind = kind,
                    Modules = stack.Select(level => level.Name).ToList()
                };
                if (!_types.TryGetValue(name, out var list)) _types[name] = list = [];
                list.Add(type);
                stack.Add((indent, name, type));
            }
            else if (owner is not null)
            {
                ParseMember(owner, trimmed, comments, attributes);
            }

            comments.Clear();
            attributes.Clear();
        }
    }

    private static VerseDigestMember Member(string name, string declaration, List<string> comments, List<string> attributes,
        IReadOnlyList<(string?, string)>? parameters = null, bool extension = false) => new()
    {
        Name = name, Declaration = declaration, Comments = comments.ToList(), Attributes = attributes.ToList(),
        Parameters = parameters ?? [], IsExtension = extension
    };

    private static void ParseMember(VerseDigestType owner, string line, List<string> comments, List<string> attributes)
    {
        var declaration = StripBody(line);
        var text = declaration;
        var extension = false;
        (string?, string)? receiver = null;

        // (Receiver:type).Name(...)
        if (text.StartsWith('('))
        {
            var close = Matching(text, 0);
            if (close < 0 || close + 1 >= text.Length || text[close + 1] != '.') return;
            receiver = SplitParameter(text[1..close]);
            text = text[(close + 2)..];
            extension = true;
        }

        if (text.StartsWith("var ", StringComparison.Ordinal)) text = text[4..].TrimStart();

        var id = Identifier.Match(text);
        if (!id.Success) return;
        var name = id.Value;
        var i = id.Length;
        while (i < text.Length && text[i] == '<')
        {
            var end = text.IndexOf('>', i);
            if (end < 0) return;
            i = end + 1;
        }

        if (i < text.Length && text[i] == '(')
        {
            var close = Matching(text, i);
            if (close < 0) return;
            var parameters = VerseSignatureParser.SplitParameters(text[(i + 1)..close]).Select(SplitParameter).ToList();
            if (receiver is { } self) parameters.Insert(0, self);
            if (!owner.Functions.TryGetValue(name, out var overloads)) owner.Functions[name] = overloads = [];
            overloads.Add(Member(name, declaration, comments, attributes, parameters, extension));
            return;
        }

        if (!extension && i < text.Length && text[i] == ':')
            owner.Fields[name] = Member(name, declaration, comments, attributes);
    }

    /// <summary>"Name:type", "?Name:type = ...", or just "type"</summary>
    private static (string?, string) SplitParameter(string parameter)
    {
        parameter = parameter.Trim();
        var equals = TopLevelIndex(parameter, " = ");
        if (equals >= 0) parameter = parameter[..equals];

        var named = parameter.StartsWith('?');
        var body = named ? parameter[1..] : parameter;
        var id = Identifier.Match(body);
        if (id.Success && id.Length < body.Length && body[id.Length] == ':')
            return (named ? "?" + id.Value : id.Value, body[(id.Length + 1)..].Trim());
        return (null, body.TrimStart(':').Trim());
    }

    private static string StripBody(string line)
    {
        var at = TopLevelIndex(line, " = external");
        if (at < 0) at = TopLevelIndex(line, "= external");
        return (at >= 0 ? line[..at] : line).TrimEnd();
    }

    private static int TopLevelIndex(string text, string value)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(' or '{' or '[':
                    depth++;
                    continue;
                case ')' or '}' or ']':
                    depth--;
                    continue;
            }

            if (depth == 0 && string.CompareOrdinal(text, i, value, 0, value.Length) == 0) return i;
        }

        return -1;
    }

    private static int Matching(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i;
        }

        return -1;
    }

    #endregion

    #region lookup

    /// <summary>
    /// the digest entry of a type, told apart from same named ones by the module it sits in;
    /// modules is the verse path it lives at, e.g. /Fortnite.com/Devices
    /// </summary>
    public VerseDigestType? Find(string name, string modulePath)
    {
        if (!_types.TryGetValue(name, out var candidates)) return null;
        if (candidates.Count == 1) return candidates[0];

        var segments = modulePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return candidates
            .OrderByDescending(candidate => MatchingTail(candidate.Modules, segments))
            .First();
    }

    private static int MatchingTail(IReadOnlyList<string> modules, string[] segments)
    {
        var count = 0;
        for (int m = modules.Count - 1, s = segments.Length - 1; m >= 0 && s >= 0; m--, s--)
        {
            if (!string.Equals(modules[m], segments[s], StringComparison.Ordinal)) break;
            count++;
        }

        return count;
    }

    /// <summary>the overload whose parameter types match; with a single overload, that one</summary>
    public static VerseDigestMember? FindFunction(VerseDigestType type, string name, IReadOnlyList<string> parameterTypes)
    {
        if (!type.Functions.TryGetValue(name, out var overloads)) return null;
        if (overloads.Count == 1) return overloads[0];

        var wanted = parameterTypes.Select(NormaliseType).ToList();
        return overloads.FirstOrDefault(overload =>
            overload.Parameters.Count == wanted.Count &&
            overload.Parameters.Select(p => NormaliseType(p.Type)).SequenceEqual(wanted));
    }

    /// <summary>a type spelled without module qualifiers or spaces, so the digest and the cook compare</summary>
    public static string NormaliseType(string type)
    {
        var builder = new StringBuilder(type.Length);
        for (var i = 0; i < type.Length; i++)
        {
            // (/Path:)name
            if (type[i] == '(' && i + 1 < type.Length && type[i + 1] == '/')
            {
                var close = type.IndexOf(":)", i, StringComparison.Ordinal);
                if (close > 0)
                {
                    i = close + 1;
                    continue;
                }
            }

            if (!char.IsWhiteSpace(type[i])) builder.Append(type[i]);
        }

        return builder.ToString();
    }

    #endregion
}
