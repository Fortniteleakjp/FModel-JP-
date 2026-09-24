using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;

namespace FModel.ViewModels;

/// <summary>
/// One parameter of a native function, in declaration order (the order the bytecode passes arguments in).
/// </summary>
/// <param name="IsDirectionUnknown">read from an SDK dump, which does not say whether a parameter is an output</param>
public sealed record BlueprintParameterInfo(string Name, bool IsOut, bool IsBool, bool IsHidden, string DisplayName, bool IsDirectionUnknown = false);

/// <summary>
/// What the editor shows for a native UFUNCTION, read from its declaration in the engine headers.
/// </summary>
public sealed class BlueprintFunctionInfo
{
    public string Class { get; init; }
    public string Name { get; init; }
    public string DisplayName { get; init; }
    public string CompactNodeTitle { get; init; }
    public string ReturnDisplayName { get; init; }
    public bool IsPure { get; init; }
    public bool IsStatic { get; init; }
    public bool IsLatent { get; init; }
    public bool IsEvent { get; init; }
    public bool IsInternal { get; init; }
    public bool HasReturnValue { get; init; }
    public bool ReturnsBool { get; init; }
    public string ExpandEnumAsExecs { get; init; }
    public IReadOnlyList<BlueprintParameterInfo> Parameters { get; init; } = [];
}

/// <summary>
/// Node names of the engine functions a blueprint can call. Cooked builds strip the editor metadata of native
/// functions, so the table is generated from the UFUNCTION declarations of the Unreal Engine headers
/// (Tools/GenerateBlueprintNodeDatabase.py) and shipped as a resource. The game's own classes, which the engine
/// source does not have, come from an SDK dump of the game (Dumpspace): parameters and flags, without metadata. The naming rules themselves
/// (NameToDisplayString, the "Target is" line, pin names) follow the editor's own code.
/// </summary>
public static class BlueprintNodeDatabase
{
    private const string _RESOURCE = "BlueprintNodeDatabase.json.gz";

    private static readonly Lazy<Data> _data = new(Load);

    private sealed class Data
    {
        public string Source;
        public readonly Dictionary<string, string> ClassDisplayNames = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> StructDisplayNames = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Dictionary<string, BlueprintFunctionInfo>> Functions = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<BlueprintFunctionInfo>> ByName = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string[]> Supers = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Dictionary<string, string>> MemberTypes = new(StringComparer.Ordinal);
    }

    /// <summary>Engine revision the table was generated from.</summary>
    public static string Source => _data.Value.Source;

    public static BlueprintFunctionInfo Find(string className, string functionName)
    {
        if (string.IsNullOrEmpty(functionName)) return null;

        var data = _data.Value;
        if (className != null && data.Functions.TryGetValue(className, out var functions) && functions.TryGetValue(functionName, out var info))
            return info;

        return null;
    }

    /// <summary>
    /// The function as <paramref name="className"/> sees it, declared by the class itself or one of its parents
    /// (the parent chains come from the SDK dump).
    /// </summary>
    public static BlueprintFunctionInfo FindInHierarchy(string className, string functionName)
    {
        var info = Find(className, functionName);
        if (info != null || className == null || !_data.Value.Supers.TryGetValue(className, out var supers)) return info;

        foreach (var super in supers)
        {
            info = Find(super, functionName);
            if (info != null) return info;
        }

        return null;
    }

    /// <summary>
    /// Class of an object member of a native class or of one of its parents (from the SDK dump), Mesh -> SkeletalMeshComponent.
    /// </summary>
    public static string MemberType(string className, string member)
    {
        if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(member)) return null;

        var data = _data.Value;
        IEnumerable<string> chain = data.Supers.TryGetValue(className, out var supers) ? supers.Prepend(className) : [className];
        foreach (var type in chain)
        {
            if (data.MemberTypes.TryGetValue(type, out var members) && members.TryGetValue(member, out var memberType)) return memberType;
        }

        return null;
    }

    /// <summary>Parent classes of a native class, nearest first (from the SDK dump), empty when unknown.</summary>
    public static IReadOnlyList<string> Supers(string className) =>
        className != null && _data.Value.Supers.TryGetValue(className, out var supers) ? supers : [];

    /// <summary>
    /// Lookup by name alone, for virtual calls and events whose class the bytecode does not name.
    /// Only answers when every class declaring that name agrees, or when <paramref name="preferEvent"/> picks the event.
    /// </summary>
    public static BlueprintFunctionInfo FindByName(string functionName, bool preferEvent = false)
    {
        if (string.IsNullOrEmpty(functionName) || !_data.Value.ByName.TryGetValue(functionName, out var candidates)) return null;
        if (candidates.Count == 1) return candidates[0];

        if (preferEvent)
        {
            var events = candidates.Where(c => c.IsEvent).ToList();
            if (events.Count > 0 && events.All(e => e.DisplayName == events[0].DisplayName)) return events[0];
        }

        var first = candidates[0];
        return candidates.All(c => c.DisplayName == first.DisplayName && c.Parameters.Count == first.Parameters.Count) ? first : null;
    }

    /// <summary>UField::GetDisplayNameText for a class: its DisplayName metadata or its friendly name.</summary>
    public static string ClassDisplayName(string className)
    {
        if (string.IsNullOrEmpty(className)) return string.Empty;
        if (_data.Value.ClassDisplayNames.TryGetValue(className, out var display)) return display;

        // FDisplayNameHelper: a blueprint class shows without its _C / SKEL_
        var name = className;
        if (name.EndsWith("_C", StringComparison.Ordinal)) name = name[..^2];
        if (name.StartsWith("SKEL_", StringComparison.Ordinal)) name = name[5..];
        return NameToDisplayString(name, false);
    }

    public static string StructDisplayName(string structName)
    {
        if (string.IsNullOrEmpty(structName)) return string.Empty;
        return _data.Value.StructDisplayNames.TryGetValue(structName, out var display) ? display : NameToDisplayString(structName, false);
    }

    /// <summary>
    /// ObjectTools::GetUserFacingFunctionName: the DisplayName metadata, or the function name made friendly.
    /// </summary>
    public static string FunctionDisplayName(string functionName, BlueprintFunctionInfo info) =>
        !string.IsNullOrEmpty(info?.DisplayName) ? info.DisplayName : NameToDisplayString(functionName, false);

    /// <summary>UEdGraphSchema_K2::GetPinDisplayName with friendly names on (the editor default).</summary>
    public static string PinDisplayName(string name, bool isBool) => NameToDisplayString(name ?? string.Empty, isBool);

    private static readonly string[] _articles = ["In", "As", "To", "Or", "At", "On", "If", "Be", "By", "The", "For", "And", "With", "When", "From"];

    /// <summary>
    /// Port of FName::NameToDisplayString (Runtime/Core/Private/UObject/UnrealNames.cpp).
    /// "bIsEnabled" -> "Is Enabled", "K2_GetActorLocation" -> "K2 Get Actor Location", "DrawScale3D" -> "Draw Scale 3D".
    /// </summary>
    public static string NameToDisplayString(string name, bool isBool)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;

        var output = new StringBuilder(name.Length + 8);
        bool inRun = false, wasSpace = false, wasOpenParen = false, wasNumber = false, wasMinusSign = false;

        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            var isLower = char.IsLower(ch);
            var isUpper = char.IsUpper(ch);
            var isDigit = ch is >= '0' and <= '9';
            var isUnderscore = ch == '_';

            // a bool property starts with a 'b' that is not shown
            if (i == 0 && isBool && ch == 'b' && name.Length > 1 && char.IsUpper(name[1]))
                continue;

            if ((isUpper || (isDigit && !wasMinusSign)) && !inRun && !wasOpenParen && !wasNumber)
            {
                if (!wasSpace && output.Length > 0)
                {
                    output.Append(' ');
                    wasSpace = true;
                }

                inRun = true;
            }

            if (isLower) inRun = false;

            if (isUnderscore)
            {
                ch = ' ';
                inRun = true;
            }

            if (output.Length == 0)
            {
                ch = char.ToUpperInvariant(ch);
            }
            else if (!isDigit && (wasSpace || wasOpenParen))
            {
                var isArticle = false;
                foreach (var article in _articles)
                {
                    var length = article.Length;
                    if (name.Length - i > length && !char.IsLower(name[i + length]) && name[i + length] != '\0' &&
                        string.CompareOrdinal(name, i, article, 0, length) == 0)
                    {
                        isArticle = true;
                        break;
                    }
                }

                ch = isArticle ? char.ToLowerInvariant(ch) : char.ToUpperInvariant(ch);
            }

            wasSpace = ch == ' ';
            wasOpenParen = ch == '(';
            wasMinusSign = ch == '-';
            var potentialNumericalChar = wasMinusSign || ch == '.';
            wasNumber = isDigit || (wasNumber && potentialNumericalChar);

            output.Append(ch);
        }

        return output.ToString();
    }

    private static Data Load()
    {
        var data = new Data();
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream($"{assembly.GetName().Name}.Resources.{_RESOURCE}");
            if (stream == null) return data;

            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            var root = JObject.Parse(reader.ReadToEnd());

            data.Source = root.Value<string>("source");

            foreach (var (structName, token) in (JObject) root["structs"] ?? new JObject())
            {
                var display = token?.Value<string>("d");
                if (!string.IsNullOrEmpty(display)) data.StructDisplayNames[structName] = display;
            }

            foreach (var (className, token) in (JObject) root["classes"] ?? new JObject())
            {
                if (token is not JObject cls) continue;

                var display = cls.Value<string>("d");
                if (!string.IsNullOrEmpty(display)) data.ClassDisplayNames[className] = display;
                if (cls["s"] is JArray supers) data.Supers[className] = supers.Select(t => t.Value<string>()).ToArray();
                if (cls["m"] is JObject members)
                    data.MemberTypes[className] = members.Properties().ToDictionary(m => m.Name, m => m.Value.Value<string>(), StringComparer.Ordinal);

                var functions = new Dictionary<string, BlueprintFunctionInfo>(StringComparer.Ordinal);
                foreach (var (functionName, fnToken) in (JObject) cls["f"] ?? new JObject())
                {
                    if (fnToken is not JObject fn) continue;

                    var parameters = new List<BlueprintParameterInfo>();
                    foreach (var pin in (JArray) fn["p"] ?? new JArray())
                    {
                        if (pin is not JArray values || values.Count < 2) continue;

                        var flags = values[1].Value<int>();
                        parameters.Add(new BlueprintParameterInfo(values[0].Value<string>(), (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0,
                            values.Count > 2 ? values[2].Value<string>() : null, (flags & 8) != 0));
                    }

                    var ret = fn.Value<int?>("r") ?? 0;
                    var info = new BlueprintFunctionInfo
                    {
                        Class = className,
                        Name = functionName,
                        DisplayName = fn.Value<string>("d"),
                        CompactNodeTitle = fn.Value<string>("c"),
                        ReturnDisplayName = fn.Value<string>("rd"),
                        IsPure = fn.Value<int?>("pure") == 1,
                        IsStatic = fn.Value<int?>("static") == 1,
                        IsLatent = fn.Value<int?>("latent") == 1,
                        IsEvent = fn.Value<int?>("event") == 1,
                        IsInternal = fn.Value<int?>("internal") == 1,
                        HasReturnValue = ret != 0,
                        ReturnsBool = ret == 2,
                        ExpandEnumAsExecs = fn.Value<string>("ExpandEnumAsExecs"),
                        Parameters = parameters
                    };

                    functions[functionName] = info;
                    if (!data.ByName.TryGetValue(functionName, out var list)) data.ByName[functionName] = list = [];
                    list.Add(info);
                }

                data.Functions[className] = functions;
            }
        }
        catch (Exception)
        {
            // without the table nodes fall back to the editor's naming rules alone
        }

        return data;
    }
}
