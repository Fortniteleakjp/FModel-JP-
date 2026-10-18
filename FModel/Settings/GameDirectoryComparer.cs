using System;
using System.Collections.Generic;

namespace FModel.Settings;

/// <summary>
/// ゲームフォルダのパスを同一視する比較。大文字小文字と '/' '\' の違い、末尾の区切り文字を無視する。
/// <para>
/// Epic Games Launcher は LauncherInstalled.dat の InstallLocation を "C:/Program Files/..." や
/// "c:\program files\..." のように書き換えることがあり、完全一致で引くと同じ Fortnite フォルダが
/// 別のゲームとして扱われ、エンドポイントや AES キーなどの設定が初期状態に戻ってしまう。
/// </para>
/// </summary>
public sealed class GameDirectoryComparer : IEqualityComparer<string>
{
    public static readonly GameDirectoryComparer Instance = new();

    public static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;

        var normalized = path.Trim().Replace('/', '\\');
        // "Fortnite/" + "\FortniteGame" のような二重の区切り文字を 1 つにする (UNC の先頭 "\\" は残す)
        var prefix = normalized.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\" : string.Empty;
        var body = normalized[prefix.Length..];
        while (body.Contains(@"\\", StringComparison.Ordinal))
            body = body.Replace(@"\\", @"\", StringComparison.Ordinal);
        normalized = prefix + body;

        var trimmed = normalized.TrimEnd('\\');
        // "C:\" はルートなので区切り文字を残す
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? normalized : trimmed;
    }

    public bool Equals(string x, string y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        return string.Equals(Normalize(x), Normalize(y), StringComparison.OrdinalIgnoreCase);
    }

    public int GetHashCode(string obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(Normalize(obj));
}
