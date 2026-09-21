using System;
using System.Collections.Generic;
using System.Threading;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;

namespace FModel.Services;

/// <summary>
/// Asset Explorer で再利用する UE パッケージの容量上限付き LRU キャッシュ。
/// 同じパッケージが同時に要求された場合も、実際のロードは 1 回だけ行う。
/// </summary>
public sealed class UePackageCache
{
    private sealed class CacheEntry
    {
        public required Lazy<IPackage> Package { get; init; }
        public required LinkedListNode<string> Node { get; init; }
    }

    private readonly object _lock = new();
    private readonly int _capacity;
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();

    public UePackageCache(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _capacity = capacity;
    }

    public IPackage GetOrLoad(GameFile file, Func<GameFile, IPackage> loader)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(loader);

        Lazy<IPackage> package;
        lock (_lock)
        {
            if (_entries.TryGetValue(file.Path, out var cached))
            {
                Touch(cached.Node);
                package = cached.Package;
            }
            else
            {
                var node = _lru.AddLast(file.Path);
                package = new Lazy<IPackage>(() => loader(file), LazyThreadSafetyMode.ExecutionAndPublication);
                _entries.Add(file.Path, new CacheEntry
                {
                    Package = package,
                    Node = node
                });

                Trim();
            }
        }

        try
        {
            return package.Value;
        }
        catch
        {
            RemoveFaultedEntry(file.Path, package);
            throw;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _lru.Clear();
        }
    }

    private void Touch(LinkedListNode<string> node)
    {
        if (ReferenceEquals(node, _lru.Last))
            return;

        _lru.Remove(node);
        _lru.AddLast(node);
    }

    private void Trim()
    {
        while (_entries.Count > _capacity && _lru.First is { } oldest)
        {
            _lru.RemoveFirst();
            _entries.Remove(oldest.Value);
        }
    }

    private void RemoveFaultedEntry(string path, Lazy<IPackage> package)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(path, out var cached) || !ReferenceEquals(cached.Package, package))
                return;

            _entries.Remove(path);
            _lru.Remove(cached.Node);
        }
    }
}
