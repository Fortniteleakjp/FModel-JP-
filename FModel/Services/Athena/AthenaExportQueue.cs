using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.FileProvider.Objects;

namespace FModel.Services.Athena;

/// <summary>
/// Athena プロファイルの出力対象を溜めておくキュー。
/// 複数のフォルダを何回かに分けて右クリックしても、最後にまとめて 1 ファイルに出力できる。
/// </summary>
public static class AthenaExportQueue
{
    private static readonly object _lock = new();
    public static event Action<int> CountChanged;

    /// <summary>キーはアセットのフルパス。同じアセットを二重に積まないための辞書。</summary>
    private static readonly Dictionary<string, GameFile> _entries = new(StringComparer.OrdinalIgnoreCase);

    public static int Count
    {
        get
        {
            lock (_lock) return _entries.Count;
        }
    }

    /// <summary>キューに追加し、実際に新しく積まれた件数を返す。</summary>
    public static int Add(IEnumerable<GameFile> entries)
    {
        if (entries is null) return 0;

        var added = 0;
        int count;
        lock (_lock)
        {
            foreach (var entry in entries)
            {
                if (entry is null) continue;
                if (_entries.TryAdd(entry.Path, entry))
                    added++;
            }

            count = _entries.Count;
        }

        if (added > 0)
            CountChanged?.Invoke(count);

        return added;
    }

    /// <summary>ワーカースレッドから安全に読めるよう、追加順のコピーを返す。</summary>
    public static GameFile[] Snapshot()
    {
        lock (_lock) return _entries.Values.ToArray();
    }

    /// <summary>指定したアセットをキューから削除する。</summary>
    public static bool Remove(GameFile entry)
    {
        if (entry is null) return false;

        bool removed;
        int count;
        lock (_lock)
        {
            removed = _entries.Remove(entry.Path);
            count = _entries.Count;
        }

        if (removed)
            CountChanged?.Invoke(count);

        return removed;
    }

    public static void Clear()
    {
        lock (_lock)
        {
            if (_entries.Count == 0) return;
            _entries.Clear();
        }

        CountChanged?.Invoke(0);
    }
}
