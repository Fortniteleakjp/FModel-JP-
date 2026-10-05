using System;
using System.IO;
using System.Linq;
using FModel.Settings;

namespace FModel.Services.Mcp;

/// <summary>
/// MCP 経由で触れるディスク上のパスの制約。
/// <list type="bullet">
/// <item>入力 (追加で開くゲーム): FModel に登録済みのゲームフォルダ (PerDirectory) の内側だけ。
/// AI に任意フォルダを開かせて、ルーズファイル (OsGameFile) 経由でディスクを読まれないようにする。</item>
/// <item>出力 (エクスポート): FModel の出力フォルダ配下の MCP/ に、ジョブごとの新規フォルダを掘ってその中だけ。
/// ゲーム内パスに ".." やデバイス名が混ざっていても外へ書かない。</item>
/// </list>
/// 参考: plu1337/fmodel-mcp の PathPolicy (inputRoots の代わりに FModel の保存済みゲームを許可リストにしている)。
/// </summary>
internal static class McpPathPolicy
{
    public static string OutputRoot => Path.Combine(UserSettings.Default.OutputDirectory, "MCP");

    /// <summary>FModel の設定に保存されているローカルのゲームフォルダ (ライブ配信のトリガーは除く)</summary>
    public static string[] KnownGameDirectories() => UserSettings.Default.PerDirectory.Keys
        .Where(d => d is not (Constants._FN_LIVE_TRIGGER or Constants._VAL_LIVE_TRIGGER) && Path.IsPathFullyQualified(d))
        .ToArray();

    public static string GameDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Game directories must be absolute OS paths.");

        var full = Path.GetFullPath(path);
        if (!KnownGameDirectories().Any(root => IsWithin(root, full)))
            throw new ArgumentException("Directory is not one of FModel's saved game directories. Add it in FModel's directory selector first (see fmodel_list_saved_games).");
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("Game directory does not exist.");
        return full;
    }

    public static string MappingsFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Mappings paths must be absolute.");

        var full = Path.GetFullPath(path);
        if (!(full.EndsWith(".usmap", StringComparison.OrdinalIgnoreCase) || full.EndsWith(".jmap", StringComparison.OrdinalIgnoreCase) ||
              full.EndsWith(".jmap.gz", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Mappings must be .usmap, .jmap or .jmap.gz.");
        if (!File.Exists(full)) throw new FileNotFoundException("Mappings file does not exist.");
        return full;
    }

    public static string NewOutputDirectory(string category)
    {
        var root = OutputRoot;
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"{category}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static bool IsWithin(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.GetFullPath(path);
        return string.Equals(root, path, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string OutputFile(string root, string relative)
    {
        ValidateRelative(relative);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsWithin(root, path)) throw new ArgumentException("Output path escapes its directory.");
        RejectLinksBelow(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>ゲーム内パスをディスク上の相対パスとして使ってよいか (".." やデバイス名、ドライブ指定を拒否)</summary>
    public static void ValidateRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Replace('\\', '/').Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                                                            p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsReservedName(p)))
            throw new ArgumentException("Invalid relative asset/output path.");
    }

    private static bool IsReservedName(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
               stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9';
    }

    /// <summary>
    /// <paramref name="root"/> より下にシンボリックリンク / ジャンクションが無いこと。
    /// 出力フォルダ自体 (ユーザーが選んだ場所) より上は問わない。
    /// </summary>
    public static void RejectLinksBelow(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (var item = new FileInfo(Path.GetFullPath(path));
             item is not null && IsWithin(root, item.FullName) && !string.Equals(item.FullName, root, StringComparison.OrdinalIgnoreCase);
             item = item.Directory is { } parent ? new FileInfo(parent.FullName) : null)
        {
            if ((File.Exists(item.FullName) || Directory.Exists(item.FullName)) &&
                (File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Symbolic links and junctions are not allowed inside MCP output directories.");
        }
    }
}
