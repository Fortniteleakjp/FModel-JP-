using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;

namespace FModel.Services.Verse;

/// <summary>
/// Writes the Verse declarations of a cooked class, struct or enum.
///
/// Field types, inheritance, enum values, default values and @editable markers come straight out of
/// the cooked reflection data and are exact. Function bodies are not cooked as source; with bodies on
/// they are rebuilt from the Kismet bytecode by <see cref="VerseBodyWriter"/>, otherwise they are
/// written as external {}.
/// Access specifiers other than &lt;public&gt; are not cooked at all and cannot be recovered.
/// </summary>
/// <summary>what is written for the body of a function</summary>
public enum VerseBodyMode
{
    /// <summary>external {}</summary>
    None,
    /// <summary>the body rebuilt as Verse source</summary>
    Rebuilt,
    /// <summary>the cooked statements listed one by one, for looking at what the compiler generated</summary>
    Listing
}

public class VerseDeclarationWriter
{
    private const uint SolClassFlagConcrete = 1 << 2;
    private const uint SolClassFlagModule = 1 << 3;

    private readonly VerseTypeResolver _resolver;
    private readonly VerseBodyWriter? _bodies;
    private readonly VerseBodyMode _mode;
    private readonly VerseDigestIndex? _digest;
    private string _owner = string.Empty;
    private IReadOnlyDictionary<string, string>? _initialisers;
    private VerseDigestType? _digestType;

    /// <param name="withBodies">
    /// also re-synthesise each function body from its Kismet bytecode, instead of writing external {}
    /// </param>
    public VerseDeclarationWriter(string package, bool withBodies = false)
        : this(package, withBodies ? VerseBodyMode.Rebuilt : VerseBodyMode.None)
    {
    }

    /// <param name="digest">
    /// the digests matching declarations take their public signatures from; by default the ones
    /// found on this machine, never for a listing, which shows the cook as it is
    /// </param>
    public VerseDeclarationWriter(string package, VerseBodyMode mode, VerseDigestIndex? digest = null)
    {
        _resolver = new VerseTypeResolver(package);
        _mode = mode;
        if (mode != VerseBodyMode.None) _bodies = new VerseBodyWriter(_resolver) { Listing = mode == VerseBodyMode.Listing };
        if (mode != VerseBodyMode.Listing) _digest = digest ?? VerseDigestIndex.Shared;
    }

    /// <summary>compiler generated members, they were never part of the original source</summary>
    private static bool IsCompilerGenerated(string name) =>
        name.StartsWith('$') || name.StartsWith("__verse_0x00000000_", StringComparison.Ordinal) ||
        name.Contains("$OverrideFactory", StringComparison.Ordinal);

    public static string Header(string versePath, bool withBodies = false) =>
        Header(versePath, withBodies ? VerseBodyMode.Rebuilt : VerseBodyMode.None);

    public static string Header(string versePath, VerseBodyMode mode)
    {
        if (mode == VerseBodyMode.Listing)
            return "# ============================================================================\n" +
                   $"# COOKED VERSE BYTECODE LISTING -- {versePath}\n" +
                   "#\n" +
                   "# The declarations of this package with each function body listed statement by\n" +
                   "# statement as the compiler cooked it, for looking into what it generated: the\n" +
                   "# transactional copy only, jumps shown as the if they test, compiler temporaries\n" +
                   "# ($ExprResult_N, $Callee_N, ...) and runtime helpers kept as they are. Parameters\n" +
                   "# read Arg0..ArgN. A suspends function is only a stub that makes its task; the\n" +
                   "# statements of the task's Update, where its body runs, follow it.\n" +
                   "# ============================================================================\n\n";

        var digest = mode == VerseBodyMode.Listing ? null : VerseDigestIndex.Shared;
        var digestNote = digest is null || digest.IsEmpty
            ? $"# No Verse digest (*.digest.verse) was found; placing the digests UEFN generates in\n" +
              $"#   {VerseDigestIndex.UserFolder ?? "the FModel data folder"}\n" +
              "# fills in parameter names, declared types and doc comments of the APIs they cover.\n"
            : $"# {digest.Sources.Count} Verse digest file(s) were found: declarations matching them take their\n" +
              "# public signatures, parameter names, declared types and doc comments from them.\n";

        return "# ============================================================================\n" +
        $"# RECOVERED VERSE DECLARATIONS -- {versePath}\n" +
        "#\n" +
        "# Reconstructed by FModel-JP from the cooked reflection data of this package.\n" +
        "# Field types, inheritance, enum values, default values and @editable markers\n" +
        "# are read straight out of the cooked data and are exact.\n" +
        (mode == VerseBodyMode.Rebuilt
            ? "# Function bodies are RE-SYNTHESISED from the Kismet bytecode: if / not / for / loop,\n" +
              "# failure contexts, calls and assignments are rebuilt from what the compiler emitted,\n" +
              "# and suspends functions are read from the task they were lowered into. Local names\n" +
              "# survive cooking; parameter names do not, so they read Arg0..ArgN unless the body\n" +
              "# copies them into a named local. Compiler temporaries that could not be folded back\n" +
              "# read TmpN, and a body whose control flow could not be rebuilt says so and is listed\n" +
              "# statement by statement instead.\n"
            : "# Bodies are not cooked, so they are written as `external {}`; use Decompile\n" +
              "# for the bytecode level recovery of the implementations.\n") +
        "# Access specifiers other than <public> are NOT cooked and cannot be recovered.\n" +
        "# Effect specifiers such as <transacts> only survive in the names the compiler\n" +
        "# mangled, so they appear where they survived and are left out where they did not.\n" +
        "# A field shown as `any` had its type erased by cooking (a Verse dynamic property).\n" +
        digestNote +
        "# ============================================================================\n\n";
    }

    /// <summary>the finishing touches of a recovered file; a listing keeps every path as cooked</summary>
    public static string Finish(string text, string localPackage, VerseBodyMode mode) =>
        mode == VerseBodyMode.Listing ? text : WithUsings(text, localPackage);

    private static readonly Regex Qualifier = new(@"\((/[A-Za-z0-9_.@\-/]+):\)", RegexOptions.Compiled);

    /// <summary>
    /// takes the (/Path/To/Module:) qualifiers out of the recovered text and lists the modules they
    /// named as using declarations under the header, the way a hand written .verse file spells them;
    /// modules of the package itself need no using
    /// </summary>
    public static string WithUsings(string text, string localPackage)
    {
        const string headerEnd = "=\n\n";
        var split = text.IndexOf(headerEnd, StringComparison.Ordinal);
        split = split < 0 ? 0 : split + headerEnd.Length;

        var modules = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var body = Qualifier.Replace(text[split..], match =>
        {
            var path = match.Groups[1].Value;
            if (string.IsNullOrEmpty(localPackage) ||
                !path.Equals(localPackage, StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith(localPackage + "/", StringComparison.OrdinalIgnoreCase))
                modules.Add(path);
            return string.Empty;
        });

        if (modules.Count == 0) return text[..split] + body;

        var usings = new StringBuilder();
        foreach (var module in modules) usings.Append("using { ").Append(module).Append(" }\n");
        return text[..split] + usings + "\n" + body;
    }

    /// <summary>
    /// declaration of one cooked Verse type, indented for the module level it sits at
    /// </summary>
    public string Write(UObject type, int indentLevel = 0, IReadOnlySet<string>? includedFunctions = null,
        bool includeFields = true, IReadOnlySet<string>? attributedFunctions = null)
    {
        var builder = new StringBuilder();
        var indent = new string(' ', indentLevel * 4);
        var name = NameOf(type);
        _digestType = _digest?.Find(name, ModulePathOf(type));

        switch (type)
        {
            case UEnum enumeration:
                WriteEnum(builder, indent, name, enumeration);
                break;
            case UClass @class:
                WriteClass(builder, indent, name, @class, includedFunctions, includeFields, attributedFunctions);
                break;
            case UStruct structure:
                WriteStruct(builder, indent, name, structure);
                break;
        }

        return builder.ToString();
    }

    /// <summary>
    /// how the type is named in Verse; enums carry their qualified Verse name, everything else
    /// carries the path it sits at relative to its package
    /// </summary>
    private static string NameOf(UObject type)
    {
        if (type.GetOrDefault<string>("QualifiedName") is { Length: > 0 } qualified)
            return VerseMangling.StripOwnerQualifier(qualified);

        return (type.GetOrDefault<string>("PackageRelativeVersePath") ?? type.Name).SubstringAfterLast('/');
    }

    /// <summary>the verse path of the module a type sits in, e.g. /Fortnite.com/Devices</summary>
    private static string ModulePathOf(UObject type)
    {
        var package = VerseMangling.UnmangleCasedName(type.GetOrDefault<FName>("MangledPackageVersePath").Text);
        var relative = type.GetOrDefault<string>("PackageRelativeVersePath") ?? string.Empty;
        return relative.Contains('/') ? $"{package}/{relative.SubstringBeforeLast('/')}" : package;
    }

    /// <summary>the comments and attributes a digest writes above a declaration</summary>
    private static void Preamble(StringBuilder builder, string indent, VerseDigestMember member, bool editable = false)
    {
        foreach (var comment in member.Comments) builder.AppendLine($"{indent}{comment}");
        foreach (var attribute in member.Attributes) builder.AppendLine($"{indent}{attribute}");
        if (editable && !member.Attributes.Any(a => a.StartsWith("@editable", StringComparison.Ordinal)))
            builder.AppendLine($"{indent}@editable");
    }

    /// <summary>the header of a type from its digest entry, or null when there is none</summary>
    private bool DigestHeader(StringBuilder builder, string indent)
    {
        if (_digestType is null) return false;
        Preamble(builder, indent, _digestType.Header);
        builder.AppendLine($"{indent}{_digestType.Header.Declaration}:");
        return true;
    }

    private void WriteEnum(StringBuilder builder, string indent, string name, UEnum enumeration)
    {
        if (!DigestHeader(builder, indent)) builder.AppendLine($"{indent}{name} := enum:");
        foreach (var (key, _) in enumeration.Names ?? [])
        {
            var entry = VerseMangling.UnmangleCasedName(key.Text.SubstringAfterLast(':'));
            // UE appends a _MAX entry of its own, it is not part of the Verse source
            if (entry.EndsWith("_MAX", StringComparison.Ordinal)) continue;
            builder.AppendLine($"{indent}    {entry}");
        }
    }

    private void WriteStruct(StringBuilder builder, string indent, string name, UStruct type)
    {
        if (!DigestHeader(builder, indent)) builder.AppendLine($"{indent}{name} := struct{Specifiers(type)}{Inherits(type)}:");
        if (WriteFields(builder, indent, type, null) == 0)
            builder.AppendLine($"{indent}    # no cooked members");
    }

    private void WriteClass(StringBuilder builder, string indent, string name, UClass @class,
        IReadOnlySet<string>? includedFunctions, bool includeFields, IReadOnlySet<string>? attributedFunctions)
    {
        var specifiers = Specifiers(@class);
        if (!DigestHeader(builder, indent))
            builder.AppendLine((@class.GetOrDefault<uint>("SolClassFlags") & SolClassFlagModule) != 0
                ? $"{indent}{name}{specifiers} := module:"
                : $"{indent}{name} := class{specifiers}{Inherits(@class)}:");

        _owner = @class.Name;
        _initialisers = includeFields && _mode == VerseBodyMode.Rebuilt ? _bodies!.FieldInitialisers(@class) : null;
        var members = includeFields ? WriteFields(builder, indent, @class, @class.ClassDefaultObject.Load()) : 0;
        _initialisers = null;
        members += WriteFunctions(builder, indent, @class, members > 0, includedFunctions, includeFields,
            attributedFunctions);
        if (members == 0) builder.AppendLine($"{indent}    # no cooked members");
    }

    /// <summary>
    /// &lt;concrete&gt; is cooked into SolClassFlags, &lt;abstract&gt; into the UE class flags
    /// </summary>
    private static string Specifiers(UStruct type)
    {
        var specifiers = new List<string>();
        if ((type.GetOrDefault<uint>("SolClassFlags") & SolClassFlagConcrete) != 0) specifiers.Add("concrete");
        if (type is UClass @class && @class.ClassFlags.HasFlag(EClassFlags.CLASS_Abstract)) specifiers.Add("abstract");
        return specifiers.Count == 0 ? string.Empty : $"<{string.Join("><", specifiers)}>";
    }

    private string Inherits(UStruct type)
    {
        if (type.SuperStruct?.ResolvedObject?.Object?.Value is not UStruct super) return string.Empty;
        // every Verse class ultimately derives from Object, which the source never spells out
        return super.Name == "Object" ? string.Empty : $"({_resolver.NameOf(super)})";
    }

    private int WriteFields(StringBuilder builder, string indent, UStruct type, UObject? defaults)
    {
        var written = 0;
        foreach (var field in type.ChildProperties ?? [])
        {
            if (field is not FProperty property) continue;
            if (IsCompilerGenerated(field.Name.Text)) continue;
            // parameters belong to their function, not to the type holding it
            if (property.PropertyFlags.HasFlag(EPropertyFlags.Parm)) continue;

            var editable = property.PropertyFlags.HasFlag(EPropertyFlags.Edit);
            var name = VerseMangling.UnmangleCasedName(field.Name.Text);
            var initial = DefaultOf(field.Name.Text, defaults);
            // a default that is not a literal is whatever the class default object computed for it
            if (initial == "external {}" && _initialisers?.TryGetValue(field.Name.Text, out var computed) == true) initial = computed;

            // the digest declares the field with the type the source gave it, which cooking may have erased
            if (_digestType?.Fields.GetValueOrDefault(name) is { } declared)
            {
                Preamble(builder, indent + "    ", declared, editable);
                builder.AppendLine($"{indent}    {declared.Declaration} = {initial}");
                written++;
                continue;
            }

            if (editable) builder.AppendLine($"{indent}    @editable");
            builder.AppendLine($"{indent}    {name}:{_resolver.Resolve(property)} = {initial}");
            written++;
        }

        return written;
    }

    /// <summary>
    /// the value the class default object carries for a field
    /// anything that is not a literal stays external {} rather than being guessed at
    /// </summary>
    private static string DefaultOf(string cookedName, UObject? defaults)
    {
        if (defaults is null || !defaults.TryGetValue(out object value, cookedName)) return "external {}";

        return value switch
        {
            bool flag => flag ? "true" : "false",
            long or int or short or sbyte or ulong or uint or ushort or byte => value.ToString()!,
            double or float => FormatFloat(Convert.ToDouble(value)),
            string text => $"\"{text}\"",
            _ => "external {}"
        };
    }

    private static string FormatFloat(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.AsSpan().IndexOfAny('.', 'E', 'N') >= 0 ? text : text + ".0";
    }

    private int WriteFunctions(StringBuilder builder, string indent, UClass @class, bool blankLineFirst,
        IReadOnlySet<string>? includedFunctions, bool includeUnattributedFunctions,
        IReadOnlySet<string>? attributedFunctions)
    {
        var declarations = new List<string>();
        // Children keeps the order the compiler cooked the members in, FuncMap does not
        foreach (var child in @class.Children ?? [])
        {
            if (!child.TryLoad(out var export) || export is not UFunction function) continue;
            if (IsCompilerGenerated(function.Name)) continue;
            if (includedFunctions is not null && !includedFunctions.Contains(function.Name) &&
                (!includeUnattributedFunctions || attributedFunctions?.Contains(function.Name) == true)) continue;

            declarations.Add(Signature(function.Name, function, indent + "    "));
        }

        if (declarations.Count == 0) return 0;
        if (blankLineFirst) builder.AppendLine();
        foreach (var declaration in declarations) builder.AppendLine(declaration);
        return declarations.Count;
    }

    /// <summary>
    /// the mangled name already holds the parameter types and the effects the compiler needed for
    /// overload resolution, so it is the best source for a signature; anything it leaves out is
    /// filled in from the cooked parameters
    /// </summary>
    private string Signature(string cookedName, UFunction function, string indent)
    {
        var decoded = VerseMangling.StripOwnerQualifier(VerseMangling.Decode(cookedName));

        if (decoded.IndexOf('(') < 0)
        {
            // a suspends function also takes the task calling it and its resume states, which the source never wrote
            var parameters = (function.ChildProperties ?? [])
                .OfType<FProperty>()
                .Where(p => p.PropertyFlags.HasFlag(EPropertyFlags.Parm) && !p.PropertyFlags.HasFlag(EPropertyFlags.ReturnParm))
                .Where(p => VerseMangling.UnmangleCasedName(p.Name.Text) is not ("CallingTask" or "CallerResumeState" or "CallerCancelState"))
                .Select(p => $":{_resolver.Resolve(p)}");

            decoded = $"{decoded}({string.Join(",", parameters)})";
        }

        var signature = VerseSignatureParser.Parse(decoded);
        if (signature is null) return $"{indent}{decoded} = external {{}}";

        // the decoded name already ends in ":type" when the compiler needed the return type to
        // tell overloads apart, otherwise it comes off the cooked return parameter
        if (signature.Tail.LastIndexOf(':') < 0)
        {
            var (effects, returnType) = ReturnOf(function);
            signature.Tail = $"{signature.Tail}{effects}:{returnType}";
        }

        // the digest names the parameters and spells the signature as the source did
        var preamble = new StringBuilder();
        string? declared = null;
        if (_digestType is not null &&
            VerseDigestIndex.FindFunction(_digestType, signature.Name, signature.Parameters.Select(p => p.Type).ToList()) is { } entry &&
            entry.Parameters.Count == signature.Parameters.Count)
        {
            for (var i = 0; i < entry.Parameters.Count; i++)
            {
                if (signature.Parameters[i].Named || entry.Parameters[i].Name is not { } parameterName || parameterName.StartsWith('?')) continue;
                signature.Parameters[i].Name = parameterName;
            }

            Preamble(preamble, indent, entry);
            declared = entry.Declaration;
        }

        if (_bodies is null) return $"{preamble}{indent}{declared ?? signature.Render() + signature.Tail} = external {{}}";

        // the body is rebuilt first, it is what names the parameters
        var decides = signature.Tail.Contains("<decides>", StringComparison.Ordinal);
        var body = _bodies.Write(function, indent, signature.Parameters, _owner, decides).TrimEnd('\n');
        return $"{preamble}{indent}{declared ?? signature.Render() + signature.Tail} =\n{body}";
    }

    /// <summary>
    /// the cooked return parameter, and the effect it implies
    /// a failable function lowers to an optional return, which is how &lt;decides&gt; survives cooking
    /// </summary>
    private (string Effects, string Type) ReturnOf(UFunction function)
    {
        var returnValue = (function.ChildProperties ?? [])
            .OfType<FProperty>()
            .FirstOrDefault(p => p.PropertyFlags.HasFlag(EPropertyFlags.ReturnParm));

        if (returnValue is null) return (string.Empty, "void");

        var type = _resolver.Resolve(returnValue);

        // a suspends function returns the task running it; what it yields is that task's _RetVal
        if (type == "task" || type.EndsWith(":)task", StringComparison.Ordinal))
        {
            var yielded = VerseBodyWriter.SuspendsTask(function)?.ChildProperties?
                .OfType<FProperty>().FirstOrDefault(p => p.Name.Text == "_RetVal");
            return ("<suspends>", yielded is null ? "void" : _resolver.Resolve(yielded));
        }

        if (!type.StartsWith('?')) return (string.Empty, type);

        // EVerseTrue is the cooked stand-in for a failable function that yields nothing
        var payload = type[1..];
        return ("<decides>", payload == "EVerseTrue" ? "void" : payload);
    }
}
