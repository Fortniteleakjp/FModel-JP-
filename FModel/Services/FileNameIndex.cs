using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;

namespace FModel.Services;

/// <summary>
/// the files of a provider by their name ("Foo.uasset"), for lookups that used to walk every path
/// one index per extension, built on first use and rebuilt when archives are mounted or unmounted
/// </summary>
/// <remarks>
/// only the hashes of the names are kept, sorted, next to the files (12 bytes per file)
/// Fortnite has about 2 million packages and a dictionary of their names took 8 times more
/// </remarks>
public static class FileNameIndex
{
    private sealed class Index
    {
        public int FileCount;
        public int[] Hashes;
        public GameFile[] Files;
    }

    private static readonly ConditionalWeakTable<FileProviderDictionary, ConcurrentDictionary<string, Lazy<Index>>> _indices = new();

    /// <summary>
    /// the files named <paramref name="fileName"/> in any directory, in the order <see cref="FileProviderDictionary.Values"/> lists them
    /// a path mounted by several archives is returned once, from the one that wins
    /// </summary>
    public static List<GameFile> FindByName(this FileProviderDictionary files, string fileName)
    {
        var result = new List<GameFile>();
        if (files is null || string.IsNullOrEmpty(fileName) || fileName.Contains('/')) return result;

        var dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1) return result;

        var index = GetIndex(files, fileName[(dot + 1)..]);
        var hash = HashOf(fileName);
        var i = LowerBound(index.Hashes, hash);
        HashSet<string> paths = null;
        for (; i < index.Hashes.Length && index.Hashes[i] == hash; i++)
        {
            var file = index.Files[i];
            if (!NameOf(file.Path).Equals(fileName, StringComparison.OrdinalIgnoreCase)) continue; // collision

            // the archives are listed from the highest read order, the first one wins
            paths ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (paths.Add(file.Path)) result.Add(file);
        }

        return result;
    }

    private static Index GetIndex(FileProviderDictionary files, string extension)
    {
        var byExtension = _indices.GetValue(files, _ => new ConcurrentDictionary<string, Lazy<Index>>(StringComparer.OrdinalIgnoreCase));
        var lazy = byExtension.GetOrAdd(extension, e => new Lazy<Index>(() => Build(files, e)));
        if (lazy.Value.FileCount == files.Count) return lazy.Value;

        // archives changed since it was built, rebuilt once (they may still be mounting)
        var rebuilt = new Lazy<Index>(() => Build(files, extension));
        return byExtension.TryUpdate(extension, rebuilt, lazy) ? rebuilt.Value : byExtension[extension].Value;
    }

    private static Index Build(FileProviderDictionary files, string extension)
    {
        var fileCount = files.Count;
        var suffix = "." + extension;
        var hashes = new List<int>();
        var matches = new List<GameFile>();

        // GameFile.Name and Extension are cached on the file, reading the path keeps the index the only cost
        foreach (var file in files.Values)
        {
            if (!file.Path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

            hashes.Add(HashOf(NameOf(file.Path)));
            matches.Add(file);
        }

        // hash in the high half, listing position in the low one: same hash keeps the listing order
        var keys = new long[matches.Count];
        for (var i = 0; i < keys.Length; i++) keys[i] = ((long) hashes[i] << 32) | (uint) i;
        Array.Sort(keys);

        var sortedHashes = new int[keys.Length];
        var sortedFiles = new GameFile[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            sortedHashes[i] = (int) (keys[i] >> 32);
            sortedFiles[i] = matches[(int) (uint) keys[i]];
        }

        return new Index { FileCount = fileCount, Hashes = sortedHashes, Files = sortedFiles };
    }

    private static ReadOnlySpan<char> NameOf(string path) => path.AsSpan(path.LastIndexOf('/') + 1);

    private static int HashOf(ReadOnlySpan<char> name) => string.GetHashCode(name, StringComparison.OrdinalIgnoreCase);

    private static int LowerBound(int[] hashes, int hash)
    {
        int low = 0, high = hashes.Length;
        while (low < high)
        {
            var mid = low + ((high - low) >> 1);
            if (hashes[mid] < hash) low = mid + 1;
            else high = mid;
        }
        return low;
    }
}
