using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using FModel.Extensions;
using FModel.Framework;
using FModel.Services;
using FModel.Services.Verse;

namespace FModel.ViewModels;

public sealed class TreeItem : ViewModel
{
    public string Header { get; }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string Archive { get; }
    public string MountPoint { get; }
    public FPackageFileVersion Version { get; }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RefreshFilters();
            }
        }
    }

    private EAssetCategory _selectedCategory = EAssetCategory.All;
    public EAssetCategory SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                if (value == EAssetCategory.All)
                {
                    RefreshFilters();
                }
                else
                {
                    _ = OnSelectedCategoryChanged();
                }
            }
        }
    }

    public string PathAtThisPoint { get; }
    public AssetsListViewModel AssetsList { get; } = new();
    public RangeObservableCollection<TreeItem> Folders { get; } = [];

    private ICollectionView? _filteredFoldersView;
    public ICollectionView? FilteredFoldersView
    {
        get
        {
            // BulkPopulate sorts Folders before this view is created
            _filteredFoldersView ??= new ListCollectionView(Folders)
            {
                Filter = e => ItemFilter(e, SearchText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            };
            return _filteredFoldersView;
        }
    }

    private CompositeCollection _combinedEntries;
    public CompositeCollection CombinedEntries => _combinedEntries ??=
    [
        new CollectionContainer { Collection = FilteredFoldersView },
        new CollectionContainer { Collection = AssetsList.AssetsView }
    ];

    public TreeItem Parent { get; init; }
    public int Depth => Parent?.Depth + 1 ?? 0;
    public Thickness Indent => new(Depth * 16, 0, 0, 0); // For folder tree indentation

    public TreeItem(string header, GameFile entry, string pathHere)
    {
        Header = header;
        if (entry is VfsEntry vfsEntry)
        {
            Archive = vfsEntry.Vfs.Name;
            MountPoint = vfsEntry.Vfs.MountPoint;
            Version = vfsEntry.Vfs.Ver;
        }
        PathAtThisPoint = pathHere;

        AssetsList.SetFilter(o => ItemFilter(o, SearchText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)));
    }

    private void RefreshFilters()
    {
        AssetsList.RefreshView();
        FilteredFoldersView?.Refresh();
    }

    private bool ItemFilter(object item, IEnumerable<string> filters)
    {
        var f = filters.ToArray();
        switch (item)
        {
            case GameFileViewModel entry:
            {
                bool matchesSearch = f.Length == 0 || f.All(x => entry.Asset.Name.Contains(x, StringComparison.OrdinalIgnoreCase));
                bool matchesCategory = SelectedCategory == EAssetCategory.All || entry.AssetCategory.IsOfCategory(SelectedCategory);

                return matchesSearch && matchesCategory;
            }
            case TreeItem folder:
            {
                bool matchesSearch = f.Length == 0 || f.All(x => folder.Header.Contains(x, StringComparison.OrdinalIgnoreCase));
                bool matchesCategory = SelectedCategory == EAssetCategory.All;

                return matchesSearch && matchesCategory;
            }
        }
        return false;
    }

    private async Task OnSelectedCategoryChanged()
    {
        await Task.WhenAll(AssetsList.Assets.Select(asset => asset.ResolveAsync(EResolveCompute.Category)));
        RefreshFilters();
    }

    public override string ToString() => $"{Header} | {Folders.Count} Folders | {AssetsList.Count} Files";
}

public class AssetsFolderViewModel : ViewModel
{
    private Dictionary<string, TreeItem> _foldersByPath = new(StringComparer.Ordinal);

    public RangeObservableCollection<TreeItem> Folders { get; } = [];
    public RangeObservableCollection<TreeItem> VisibleFolders { get; } = [];

    private TreeItem? _selectedFolder;
    public TreeItem? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            var previous = _selectedFolder;
            if (!SetProperty(ref _selectedFolder, value))
                return;

            previous?.IsSelected = false;
            value?.IsSelected = true;
        }
    }

    public void Clear()
    {
        SelectedFolder = null;
        VisibleFolders.Clear();
        Folders.Clear();
        _foldersByPath = new Dictionary<string, TreeItem>(StringComparer.Ordinal);
    }

    public void CollapseAll()
    {
        static void CollapseChildren(TreeItem folder)
        {
            folder.IsExpanded = false;
            foreach (var child in folder.Folders)
            {
                CollapseChildren(child);
            }
        }

        var selected = SelectedFolder;
        while (selected?.Parent != null)
            selected = selected.Parent;

        foreach (var folder in Folders)
        {
            CollapseChildren(folder);
        }

        SelectedFolder = selected;
        for (var i = VisibleFolders.Count - 1; i >= 0; i--)
        {
            if (VisibleFolders[i].Parent != null)
            {
                VisibleFolders.RemoveAt(i);
            }
        }
    }

    public void Expand(TreeItem folder)
    {
        if (folder.Folders.Count == 0)
            return;

        var index = VisibleFolders.IndexOf(folder);
        if (index < 0)
            return;

        if (index + 1 < VisibleFolders.Count && ReferenceEquals(VisibleFolders[index + 1].Parent, folder))
        {
            folder.IsExpanded = true;
            return;
        }

        foreach (var child in EnumerateVisibleChildren(folder))
        {
            VisibleFolders.Insert(++index, child);
        }

        folder.IsExpanded = true;
    }

    public void Collapse(TreeItem folder)
    {
        if (!folder.IsExpanded)
            return;

        var index = VisibleFolders.IndexOf(folder);
        if (index < 0)
            return;

        var selected = SelectedFolder;
        var start = index + 1;
        var count = 0;
        while (start + count < VisibleFolders.Count && VisibleFolders[start + count].Depth > folder.Depth)
            count++;

        if (selected != null && IsDescendantOf(selected, folder))
        {
            SelectedFolder = folder;
        }

        for (var i = start + count - 1; i >= start; i--)
        {
            VisibleFolders.RemoveAt(i);
        }

        folder.IsExpanded = false;
    }

    public void Toggle(TreeItem folder)
    {
        if (folder.IsExpanded)
        {
            Collapse(folder);
        }
        else
        {
            Expand(folder);
        }
    }

    public void Reveal(TreeItem folder)
    {
        var parents = new Stack<TreeItem>();

        for (var parent = folder.Parent; parent != null; parent = parent.Parent)
            parents.Push(parent);

        while (parents.Count > 0)
            Expand(parents.Pop());

        SelectedFolder = folder;
    }

    private static bool IsDescendantOf(TreeItem item, TreeItem parent)
    {
        for (var current = item.Parent; current != null; current = current.Parent)
        {
            if (ReferenceEquals(current, parent))
                return true;
        }

        return false;
    }

    private static IEnumerable<TreeItem> EnumerateVisibleChildren(TreeItem folder)
    {
        foreach (var child in folder.Folders)
        {
            yield return child;

            if (!child.IsExpanded)
                continue;

            foreach (var descendant in EnumerateVisibleChildren(child))
            {
                yield return descendant;
            }
        }
    }

    private static void SortFolders(RangeObservableCollection<TreeItem> folders)
    {
        if (folders.Count > 1)
        {
            folders.ReplaceRange([.. folders.OrderBy(x => x.Header, StringComparer.OrdinalIgnoreCase)]);
        }

        foreach (var folder in folders)
        {
            SortFolders(folder.Folders);
        }
    }

    public bool TryGetFolder(string directory, out TreeItem folder)
    {
        folder = null;
        if (string.IsNullOrEmpty(directory))
            return false;

        directory = directory.TrimEnd(Path.AltDirectorySeparatorChar);
        if (_foldersByPath.TryGetValue(directory, out folder))
            return true;

        // Preserve the previous behavior where only the root segment was case-insensitive.
        var separator = directory.IndexOf(Path.AltDirectorySeparatorChar);
        var rootName = separator < 0 ? directory : directory[..separator];
        var root = Folders.FirstOrDefault(x => x.Header.Equals(rootName, StringComparison.OrdinalIgnoreCase));
        if (root == null)
            return false;

        if (separator < 0)
        {
            folder = root;
            return true;
        }

        return _foldersByPath.TryGetValue(string.Concat(root.Header, directory[separator..]), out folder);
    }

    public void BulkPopulate(IReadOnlyCollection<GameFile> entries)
    {
        if (entries == null || entries.Count == 0)
            return;

        var displayEntries = RecoveredVerseGameFile.AddRecoveredFiles(
            entries, ApplicationService.ApplicationView.CUE4Parse.Provider);

        var treeItems = new List<TreeItem>();
        var foldersByPath = new Dictionary<string, TreeItem>(StringComparer.Ordinal);
        var folderLookup = foldersByPath.GetAlternateLookup<ReadOnlySpan<char>>();
        TreeItem previousFolder = null;

        foreach (var entry in displayEntries)
        {
            var path = entry.Path.AsSpan();
            var pathEnd = path.Length;
            while (pathEnd > 0 && path[pathEnd - 1] == Path.AltDirectorySeparatorChar)
                pathEnd--;

            var lastSeparator = path[..pathEnd].LastIndexOf(Path.AltDirectorySeparatorChar);
            if (lastSeparator < 0)
            {
                previousFolder = GetOrAddContentFolder(foldersByPath, entry, treeItems);
                previousFolder.AssetsList.Add(entry);
                continue;
            }

            var directories = path[..lastSeparator];
            if (previousFolder != null && directories.SequenceEqual(previousFolder.PathAtThisPoint.AsSpan()))
            {
                previousFolder.AssetsList.Add(entry);
                continue;
            }

            if (folderLookup.TryGetValue(directories, out var leafFolder))
            {
                leafFolder.AssetsList.Add(entry);
                previousFolder = leafFolder;
                continue;
            }

            TreeItem parentNode = null;
            var segmentStart = 0;

            while (segmentStart < directories.Length)
            {
                while (segmentStart < directories.Length && directories[segmentStart] == Path.AltDirectorySeparatorChar)
                    segmentStart++;
                if (segmentStart == directories.Length)
                    break;

                var segmentEnd = directories[segmentStart..].IndexOf(Path.AltDirectorySeparatorChar);
                if (segmentEnd < 0)
                    segmentEnd = directories.Length;
                else
                    segmentEnd += segmentStart;

                var folderPath = directories[..segmentEnd];
                if (!folderLookup.TryGetValue(folderPath, out var node))
                {
                    var header = directories[segmentStart..segmentEnd].ToString();
                    var normalizedPath = parentNode == null
                        ? header
                        : string.Concat(parentNode.PathAtThisPoint, "/", header);
                    if (!foldersByPath.TryGetValue(normalizedPath, out node))
                    {
                        node = new TreeItem(header, entry, normalizedPath) { Parent = parentNode };
                        foldersByPath.Add(normalizedPath, node);

                        if (parentNode == null)
                            treeItems.Add(node);
                        else
                            parentNode.Folders.AddWithoutNotification(node);
                    }
                }

                parentNode = node;
                segmentStart = segmentEnd + 1;
            }

            if (parentNode == null)
                parentNode = GetOrAddContentFolder(foldersByPath, entry, treeItems);

            parentNode.AssetsList.Add(entry);
            previousFolder = parentNode;
        }

        // the folder tree is a flat list that inserts children in collection order, so they must be sorted up front
        treeItems.Sort((x, y) => StringComparer.OrdinalIgnoreCase.Compare(x.Header, y.Header));
        foreach (var root in treeItems)
            SortFolders(root.Folders);

        Application.Current.Dispatcher.Invoke(() =>
        {
            _foldersByPath = foldersByPath;
            Folders.AddRange(treeItems);
            VisibleFolders.AddRange(treeItems);

            if (treeItems.Count > 0)
            {
                var projectName = ApplicationService.ApplicationView.CUE4Parse.Provider.ProjectName;
                SelectedFolder = treeItems.FirstOrDefault(x => x.Header.Equals(projectName, StringComparison.OrdinalIgnoreCase)) ?? treeItems[0];
            }

            ApplicationService.ApplicationView.CUE4Parse.SearchVm.ChangeCollection(displayEntries);
        });
    }

    private static TreeItem GetOrAddContentFolder(Dictionary<string, TreeItem> foldersByPath, GameFile entry,
        List<TreeItem> roots)
    {
        const string content = "Content";
        if (foldersByPath.TryGetValue(content, out var node))
            return node;

        node = new TreeItem(content, entry, content);
        foldersByPath.Add(content, node);
        roots.Add(node);
        return node;
    }
}
