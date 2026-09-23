using System;
using System.Globalization;
using System.Text;

namespace FModel.Services.Verse;

/// <summary>
/// Decodes the identifiers the Verse compiler cooks into UE names.
///
/// Two schemes show up in a cooked package:
/// <list type="bullet">
/// <item>cased names, <c>__verse_0xAF266919_NewVersion</c>, which only carry a hash of the original casing</item>
/// <item>qualified names, <c>_L_2fVerse_2eorg_2fSimulation_N_Ragent_R</c>, where <c>_xx</c> is a hex escape
/// and a few uppercase letters stand for punctuation the UE name table cannot hold</item>
/// </list>
/// </summary>
public static class VerseMangling
{
    /// <summary>
    /// punctuation tokens, confirmed against the qualified names of a cooked island
    /// unknown tokens are left as-is rather than guessed, so nothing is silently lost
    /// </summary>
    private static string? Punctuation(char token) => token switch
    {
        'L' => "(",
        'R' => ")",
        'N' => ":",
        'M' => ",",
        'K' => "[]",
        'U' => string.Empty, // separates an operator from the name it qualifies
        _ => null
    };

    /// <summary>
    /// strips the <c>__verse_0xXXXXXXXX_</c> prefix a cased name carries
    /// </summary>
    public static string UnmangleCasedName(string name)
    {
        const string prefix = "__verse_0x";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return name;

        var underscore = name.IndexOf('_', prefix.Length);
        return underscore < 0 ? name : name[(underscore + 1)..];
    }

    /// <summary>
    /// decodes a qualified name such as a function or class path
    /// </summary>
    public static string Decode(string mangled)
    {
        if (string.IsNullOrEmpty(mangled) || !mangled.Contains('_')) return mangled;

        var builder = new StringBuilder(mangled.Length);
        for (var i = 0; i < mangled.Length; i++)
        {
            if (mangled[i] != '_')
            {
                builder.Append(mangled[i]);
                continue;
            }

            if (i + 1 >= mangled.Length)
            {
                builder.Append('_');
                break;
            }

            // "__" is how an underscore of the original identifier survives
            if (mangled[i + 1] == '_')
            {
                builder.Append('_');
                i++;
                continue;
            }

            if (i + 2 < mangled.Length &&
                byte.TryParse(mangled.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ascii))
            {
                builder.Append((char) ascii);
                i += 2;
                continue;
            }

            if (Punctuation(mangled[i + 1]) is { } punctuation)
            {
                builder.Append(punctuation);
                i++;
                continue;
            }

            builder.Append('_');
        }

        return builder.ToString();
    }

    /// <summary>
    /// a decoded function name looks like <c>(/pkg/path/owner:)Name(:argtype)&lt;transacts&gt;:ret</c>
    /// this keeps everything from <c>Name</c> onwards, which is how the declaration is written
    /// </summary>
    public static string StripOwnerQualifier(string decoded)
    {
        if (!decoded.StartsWith('(')) return decoded;

        var close = decoded.IndexOf(":)", StringComparison.Ordinal);
        return close < 0 ? decoded : decoded[(close + 2)..];
    }

    /// <summary>
    /// name of a function as it is declared, without its parameter list or effects
    /// </summary>
    public static string FunctionName(string mangled)
    {
        var name = StripOwnerQualifier(Decode(mangled));
        var paren = name.IndexOf('(');
        return paren < 0 ? name : name[..paren];
    }
}
