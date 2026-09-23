using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;

namespace FModel.Services.Verse;

/// <summary>
/// Writes the Verse declarations of a cooked class, struct or enum.
///
/// Field types, inheritance, enum values, default values and @editable markers come straight out of
/// the cooked reflection data and are exact. Function bodies are not cooked, so they are written as
/// external {} - the bytecode level recovery is what the pseudo-C++ decompiler does instead.
/// Access specifiers other than &lt;public&gt; are not cooked at all and cannot be recovered.
/// </summary>
public class VerseDeclarationWriter
{
    private const uint SolClassFlagConcrete = 1 << 2;
    private const uint SolClassFlagModule = 1 << 3;

    private readonly VerseTypeResolver _resolver;
    private readonly VerseBodyWriter? _bodies;

    /// <param name="withBodies">
    /// also re-synthesise each function body from its Kismet bytecode, instead of writing external {}
    /// </param>
    public VerseDeclarationWriter(string package, bool withBodies = false)
    {
        _resolver = new VerseTypeResolver(package);
        if (withBodies) _bodies = new VerseBodyWriter(_resolver);
    }

    /// <summary>compiler generated members, they were never part of the original source</summary>
    private static bool IsCompilerGenerated(string name) =>
        name.StartsWith('$') || name.StartsWith("__verse_0x00000000_", StringComparison.Ordinal);

    public static string Header(string versePath, bool withBodies = false) =>
        "# ============================================================================\n" +
        $"# RECOVERED VERSE DECLARATIONS -- {versePath}\n" +
        "#\n" +
        "# Reconstructed by FModel-JP from the cooked reflection data of this package.\n" +
        "# Field types, inheritance, enum values, default values and @editable markers\n" +
        "# are read straight out of the cooked data and are exact.\n" +
        (withBodies
            ? "# Function bodies are RE-SYNTHESISED from the Kismet bytecode: the control flow and\n" +
              "# the calls are real, the expression-level source form is not. Local names are not\n" +
              "# cooked, so parameters read Arg0..ArgN and temporaries keep their cooked names.\n" +
              "# Anything that could not be reduced to a Verse construct says so on its own line.\n"
            : "# Bodies are not cooked, so they are written as `external {}`; use Decompile\n" +
              "# for the bytecode level recovery of the implementations.\n") +
        "# Access specifiers other than <public> are NOT cooked and cannot be recovered.\n" +
        "# Effect specifiers such as <transacts> only survive in the names the compiler\n" +
        "# mangled, so they appear where they survived and are left out where they did not.\n" +
        "# A field shown as `any` had its type erased by cooking (a Verse dynamic property).\n" +
        "# ============================================================================\n\n";

    /// <summary>
    /// declaration of one cooked Verse type, indented for the module level it sits at
    /// </summary>
    public string Write(UObject type, int indentLevel = 0, IReadOnlySet<string>? includedFunctions = null,
        bool includeFields = true, IReadOnlySet<string>? attributedFunctions = null)
    {
        var builder = new StringBuilder();
        var indent = new string(' ', indentLevel * 4);
        var name = NameOf(type);

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

    private static void WriteEnum(StringBuilder builder, string indent, string name, UEnum enumeration)
    {
        builder.AppendLine($"{indent}{name} := enum:");
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
        builder.AppendLine($"{indent}{name} := struct{Specifiers(type)}{Inherits(type)}:");
        if (WriteFields(builder, indent, type, null) == 0)
            builder.AppendLine($"{indent}    # no cooked members");
    }

    private void WriteClass(StringBuilder builder, string indent, string name, UClass @class,
        IReadOnlySet<string>? includedFunctions, bool includeFields, IReadOnlySet<string>? attributedFunctions)
    {
        var specifiers = Specifiers(@class);
        builder.AppendLine((@class.GetOrDefault<uint>("SolClassFlags") & SolClassFlagModule) != 0
            ? $"{indent}{name}{specifiers} := module:"
            : $"{indent}{name} := class{specifiers}{Inherits(@class)}:");

        var members = includeFields ? WriteFields(builder, indent, @class, @class.ClassDefaultObject.Load()) : 0;
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

            if (property.PropertyFlags.HasFlag(EPropertyFlags.Edit))
                builder.AppendLine($"{indent}    @editable");

            var name = VerseMangling.UnmangleCasedName(field.Name.Text);
            builder.AppendLine($"{indent}    {name}:{_resolver.Resolve(property)} = {DefaultOf(field.Name.Text, defaults)}");
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

            declarations.Add($"{indent}    {Signature(function.Name, function, indent + "    ")}");
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
            var parameters = (function.ChildProperties ?? [])
                .OfType<FProperty>()
                .Where(p => p.PropertyFlags.HasFlag(EPropertyFlags.Parm) && !p.PropertyFlags.HasFlag(EPropertyFlags.ReturnParm))
                .Select(p => $":{_resolver.Resolve(p)}");

            decoded = $"{decoded}({string.Join(",", parameters)})";
        }

        decoded = AsExtensionMethod(decoded);

        // the decoded name already ends in ":type" when the compiler needed the return type to
        // tell overloads apart, otherwise it comes off the cooked return parameter
        if (decoded.LastIndexOf(':') <= decoded.LastIndexOf(')'))
        {
            var (effects, returnType) = ReturnOf(function);
            decoded = $"{decoded}{effects}:{returnType}";
        }

        if (_bodies is null) return $"{decoded} = external {{}}";

        var body = _bodies.Write(function, indent).TrimEnd('\n');
        return $"{decoded} =\n{body}";
    }

    /// <summary>
    /// the compiler lowers an extension method to "operator.Name" taking the receiver as its first
    /// parameter, Verse spells that as "(:receiver).Name(rest)"
    /// </summary>
    private static string AsExtensionMethod(string decoded)
    {
        const string prefix = "operator.";
        if (!decoded.StartsWith(prefix, StringComparison.Ordinal)) return decoded;

        var open = decoded.IndexOf('(');
        if (open < 0) return decoded;

        var name = decoded[prefix.Length..open];
        var parameters = SplitParameters(decoded[(open + 1)..decoded.LastIndexOf(')')]);
        if (parameters.Count == 0) return decoded;

        var receiver = parameters[0];
        var rest = string.Join(",", parameters.Skip(1));
        return $"({receiver}).{name}({rest}){decoded[(decoded.LastIndexOf(')') + 1)..]}";
    }

    /// <summary>
    /// splits a parameter list on the commas that separate the parameters themselves, leaving the
    /// ones nested inside tuple, array and type literals alone
    /// </summary>
    private static List<string> SplitParameters(string parameterList)
    {
        var parameters = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < parameterList.Length; i++)
        {
            switch (parameterList[i])
            {
                case '(' or '{' or '[':
                    depth++;
                    break;
                case ')' or '}' or ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parameters.Add(parameterList[start..i]);
                    start = i + 1;
                    break;
            }
        }

        if (start < parameterList.Length) parameters.Add(parameterList[start..]);
        return parameters;
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
        if (!type.StartsWith('?')) return (string.Empty, type);

        // EVerseTrue is the cooked stand-in for a failable function that yields nothing
        var payload = type[1..];
        return ("<decides>", payload == "EVerseTrue" ? "void" : payload);
    }
}
