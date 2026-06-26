using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml;
using FModel.Framework;
using FModel.Settings;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace FModel.Extensions;

public static class AvalonExtensions
{
    private static readonly IHighlightingDefinition _iniHighlighter = LoadHighlighter("Ini.xshd");
    private static readonly IHighlightingDefinition _xmlHighlighter = LoadHighlighter("Xml.xshd");
    private static readonly IHighlightingDefinition _cppHighlighter = LoadHighlighter("Cpp.xshd");
    private static readonly IHighlightingDefinition _changelogHighlighter = LoadHighlighter("Changelog.xshd");
    private static readonly IHighlightingDefinition _verseHighlighter = LoadHighlighter("Verse.xshd");

    // 配色プリセットごとに生成した JSON ハイライタをキャッシュ（プリセット切替時は別インスタンスになる）。
    private static readonly ConcurrentDictionary<EJsonColorScheme, IHighlightingDefinition> _jsonHighlighters = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static IHighlightingDefinition LoadHighlighter(string resourceName)
    {
        var executingAssembly = Assembly.GetExecutingAssembly();
        using var stream = executingAssembly.GetManifestResourceStream($"{executingAssembly.GetName().Name}.Resources.{resourceName}");
        using var reader = new XmlTextReader(stream);
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    /// <summary>指定した配色プリセットの JSON ハイライタを取得（生成結果はキャッシュして再利用）。</summary>
    public static IHighlightingDefinition GetJsonHighlighter(EJsonColorScheme scheme)
        => _jsonHighlighters.GetOrAdd(scheme, BuildJsonHighlighter);

    private static IHighlightingDefinition BuildJsonHighlighter(EJsonColorScheme scheme)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream($"{asm.GetName().Name}.Resources.Json.xshd");
        using var sr = new StreamReader(stream);
        var xshd = sr.ReadToEnd();

        // Json.xshd 内の各ロールの <Color foreground="#..."> をプリセットの色へ置換する
        // （内部APIに依存せず堅牢。Default は現行値と同一なので実質無変更）。
        foreach (var (role, hex) in JsonColorPalettes.Get(scheme))
        {
            xshd = Regex.Replace(
                xshd,
                $"(name=\"{Regex.Escape(role)}\"\\s+foreground=\")#[0-9A-Fa-f]+(\")",
                $"${{1}}{hex}${{2}}");
        }

        using var reader = XmlReader.Create(new StringReader(xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IHighlightingDefinition HighlighterSelector(string ext)
    {
        switch (ext)
        {
            case "ini":
            case "csv":
                return _iniHighlighter;
            case "xml":
            case "tps":
                return _xmlHighlighter;
            case "h":
            case "cpp":
                return _cppHighlighter;
            case "changelog":
                return _changelogHighlighter;
            case "verse":
                return _verseHighlighter;
            case "bat":
            case "txt":
            case "pem":
            case "po":
                return null;
            default:
                return GetJsonHighlighter(UserSettings.Default.JsonColorScheme);
        }
    }
}
