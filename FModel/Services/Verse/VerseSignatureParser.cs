using System;
using System.Collections.Generic;
using System.Linq;

namespace FModel.Services.Verse;

/// <summary>
/// A decoded function name, <c>Name(:t1,?Named:t2 = ...)&lt;effects&gt;:ret</c>, split into the parameter
/// slots the body reaches through its packed argument.
/// </summary>
internal sealed class VerseSignature
{
    public required string Name { get; init; }
    public bool IsExtension { get; init; }
    public required List<VerseParameter> Parameters { get; init; }
    public string? Where { get; init; }
    public required string Tail { get; set; }

    public string Render()
    {
        var where = Where is null ? string.Empty : $" where {Where}";
        if (IsExtension && Parameters.Count > 0)
        {
            var receiver = Parameters[0];
            var rest = string.Join(", ", Parameters.Skip(1).Select(Render));
            // with nothing after the receiver the where clause stays with the receiver that uses it
            return rest.Length == 0
                ? $"({Render(receiver)}{where}).{Name}()"
                : $"({Render(receiver)}).{Name}({rest}{where})";
        }

        return $"{Name}({string.Join(", ", Parameters.Select(Render))}{where})";
    }

    private static string Render(VerseParameter parameter) => parameter.Named
        ? $"?{parameter.Name}:{parameter.Type} = {parameter.Default ?? "..."}"
        : $"{parameter.Name}:{parameter.Type}";
}

internal static class VerseSignatureParser
{
    /// <summary>
    /// splits a parameter list on the commas that separate the parameters themselves, leaving the
    /// ones nested inside tuple, array and type literals alone
    /// </summary>
    public static List<string> SplitParameters(string parameterList)
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
                    parameters.Add(parameterList[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }

        if (start < parameterList.Length && parameterList[start..].Trim() is { Length: > 0 } last) parameters.Add(last);
        return parameters;
    }

    private static int TopLevelIndexOf(string text, string value)
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

    public static VerseSignature? Parse(string decoded)
    {
        var open = decoded.IndexOf('(');
        if (open < 0) return null;

        var depth = 0;
        var close = -1;
        for (var i = open; i < decoded.Length; i++)
        {
            if (decoded[i] == '(') depth++;
            else if (decoded[i] == ')' && --depth == 0)
            {
                close = i;
                break;
            }
        }

        if (close < 0) return null;

        var name = decoded[..open];
        var inside = decoded[(open + 1)..close];
        string? where = null;
        if (TopLevelIndexOf(inside, " where ") is var at and >= 0)
        {
            // "where t,k" lists type parameters with commas of its own, it is cut off before splitting
            where = inside[(at + " where ".Length)..];
            inside = inside[..at];
        }

        var raw = SplitParameters(inside);
        // a function taking the empty tuple takes nothing
        if (raw is [":tuple()"]) raw.Clear();

        var parameters = new List<VerseParameter>();
        var extension = name.StartsWith("operator.", StringComparison.Ordinal) && raw.Count >= 1;
        if (extension)
        {
            name = name["operator.".Length..].Trim('\'');
            parameters.Add(Parameter(raw[0], raw.Count > 1 ? "Elem0" : string.Empty));
            if (raw.Count > 1)
            {
                var rest = raw[1];
                if (rest.StartsWith(":tuple(", StringComparison.Ordinal) && rest.EndsWith(')'))
                {
                    var inner = SplitParameters(rest[":tuple(".Length..^1]);
                    for (var i = 0; i < inner.Count; i++)
                        parameters.Add(Parameter(inner[i], inner.Count == 1 ? "Elem1" : $"Elem1.Elem{i}"));
                }
                else
                {
                    parameters.Add(Parameter(rest, "Elem1"));
                }

                for (var i = 2; i < raw.Count; i++) parameters.Add(Parameter(raw[i], $"Elem{i}"));
            }
        }
        else
        {
            for (var i = 0; i < raw.Count; i++)
                parameters.Add(Parameter(raw[i], raw.Count == 1 ? string.Empty : $"Elem{i}"));
        }

        return new VerseSignature
        {
            Name = name, IsExtension = extension, Parameters = parameters, Where = where, Tail = decoded[(close + 1)..]
        };
    }

    /// <summary>
    /// a named parameter is cooked as ":?Name:type = ...", which an option typed positional one,
    /// ":?type", is told apart from by the name and colon right after the question mark
    /// </summary>
    public static bool IsNamed(string text)
    {
        var at = text.StartsWith(':') ? 1 : 0;
        if (at >= text.Length || text[at] != '?') return false;
        var i = at + 1;
        if (i >= text.Length || !(char.IsLetter(text[i]) || text[i] == '_')) return false;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
        return i < text.Length && text[i] == ':';
    }

    public static string NamedLabel(string text)
    {
        var start = text.IndexOf('?') + 1;
        return text[start..text.IndexOf(':', start)];
    }

    private static VerseParameter Parameter(string text, string path)
    {
        if (IsNamed(text))
        {
            text = text.TrimStart(':');
            var colon = text.IndexOf(':');
            var equals = text.IndexOf(" = ", StringComparison.Ordinal);
            var name = colon > 1 ? text[1..colon] : text[1..];
            var type = colon > 0 ? (equals > colon ? text[(colon + 1)..equals] : text[(colon + 1)..]) : "any";
            return new VerseParameter { Path = path, Type = type, Named = true, Name = name };
        }

        // the cooked name keeps only ":type" for a positional parameter
        return new VerseParameter { Path = path, Type = text.StartsWith(':') ? text[1..] : text };
    }
}
