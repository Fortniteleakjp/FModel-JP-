using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using FModel.Framework;
using FModel.Services;

namespace FModel.ViewModels;

public sealed class TreeItem : ViewModel
{
    public string Header { get; }
    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    public string Archive { get; }
    public string MountPoint { get; }
    public FPackageFileVersion Version { get; }
    public string PathAtThisPoint { get; }
    public AssetsListViewModel AssetsList { get; } = new();
    public RangeObservableCollection<TreeItem> Folders { get; } = [];

    private ICollectionView _foldersView;
    public ICollectionView FoldersView => _foldersView ??= new ListCollectionView(Folders)
    {
        SortDescriptions = { new SortDescription(nameof(Header), ListSortDirection.Ascending) }
    };

    private ICollectionView _filteredFoldersView;
    public ICollectionView FilteredFoldersView => _filteredFoldersView ??= new ListCollectionView(Folders)
    {
        SortDescriptions = { new SortDescription(nameof(Header), ListSortDirection.Ascending) },
        Filter = x => ItemFilter(x, SearchText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
    };

    private CompositeCollection _combinedEntries;
    public CompositeCollection CombinedEntries => _combinedEntries ??= new CompositeCollection
    {
        new CollectionContainer { Collection = FoldersView },
        new CollectionContainer { Collection = AssetsList.AssetsView }
    };

    public TreeItem Parent { get; init; }
    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                AssetsList.SetFilter(x => ItemFilter(x, SearchText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)));
                AssetsList.RefreshView();
                FilteredFoldersView.Refresh();
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
                _ = OnSelectedCategoryChanged();
        }
    }

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
        AssetsList.SetFilter(x => ItemFilter(x, SearchText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)));
    }

    private System.Threading.Tasks.Task OnSelectedCategoryChanged()
    {
        AssetsList.RefreshView();
        FilteredFoldersView.Refresh();
        return System.Threading.Tasks.Task.CompletedTask;
    }

    private bool ItemFilter(object item, IEnumerable<string> filters)
    {
        var f = filters as string[] ?? filters.ToArray();
        return item switch
        {
            GameFile entry => f.Length == 0 || f.All(x => entry.Name.Contains(x, StringComparison.OrdinalIgnoreCase)),
            TreeItem folder => (f.Length == 0 || f.All(x => folder.Header.Contains(x, StringComparison.OrdinalIgnoreCase))) &&
                               SelectedCategory == EAssetCategory.All,
            _ => false
        };
    }

    public override string ToString() => $"{Header} | {Folders.Count} Folders | {AssetsList.Count} Files";
}

public sealed class AssetsFolderViewModel
{
    private Dictionary<string, TreeItem> _foldersByPath = new(StringComparer.Ordinal);
    public RangeObservableCollection<TreeItem> Folders { get; } = [];
    public ICollectionView FoldersView { get; }

    private CompositeCollection _combinedEntries;
    public CompositeCollection CombinedEntries => _combinedEntries ??= new CompositeCollection
    {
        new CollectionContainer { Collection = FoldersView }
    };

    public AssetsFolderViewModel() => FoldersView = new ListCollectionView(Folders)
    {
        SortDescriptions = { new SortDescription(nameof(TreeItem.Header), ListSortDirection.Ascending) }
    };

    public void Clear()
    {
        Folders.Clear();
        _foldersByPath = new Dictionary<string, TreeItem>(StringComparer.Ordinal);
    }

    public bool TryGetFolder(string directory, out TreeItem folder)
    {
        folder = null;
        if (string.IsNullOrWhiteSpace(directory))
            return false;

        directory = directory.Replace('\\', '/').Trim('/');
        if (_foldersByPath.TryGetValue(directory, out folder))
            return true;

        var separator = directory.IndexOf('/');
        var rootName = separator < 0 ? directory : directory[..separator];
        var root = Folders.FirstOrDefault(x => x.Header.Equals(rootName, StringComparison.OrdinalIgnoreCase));
        if (root == null)
            return false;

        return separator < 0 || _foldersByPath.TryGetValue(string.Concat(root.Header, directory[separator..]), out folder);
    }

    public void BulkPopulate(IReadOnlyCollection<GameFile> entries)
    {
        if (entries == null || entries.Count == 0)
            return;

        var roots = new List<TreeItem>();
        var foldersByPath = new Dictionary<string, TreeItem>(StringComparer.Ordinal);
        TreeItem previousFolder = null;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            .Replace(Path.DirectorySeparatorChar, '/');

        foreach (var entry in entries)
        {
            var path = entry.Path.Replace('\\', '/');
            if (path.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase))
                path = path[localAppData.Length..].Trim('/');

            var lastSeparator = path.LastIndexOf('/');
            if (lastSeparator < 0)
            {
                previousFolder = GetOrAddContentFolder(foldersByPath, entry, roots);
                previousFolder.AssetsList.Add(entry);
                continue;
            }

            var directory = path[..lastSeparator];
            if (previousFolder != null && directory.Equals(previousFolder.PathAtThisPoint, StringComparison.Ordinal))
            {
                previousFolder.AssetsList.Add(entry);
                continue;
            }

            if (foldersByPath.TryGetValue(directory, out var leafFolder))
            {
                leafFolder.AssetsList.Add(entry);
                previousFolder = leafFolder;
                continue;
            }

            TreeItem parent = null;
            var start = 0;
            while (start < directory.Length)
            {
                var end = directory.IndexOf('/', start);
                if (end < 0) end = directory.Length;
                var pathHere = directory[..end];
                if (!foldersByPath.TryGetValue(pathHere, out var node))
                {
                    var header = directory[start..end];
                    node = new TreeItem(header, entry, pathHere) { Parent = parent };
                    foldersByPath.Add(pathHere, node);
                    if (parent == null) roots.Add(node);
                    else parent.Folders.AddWithoutNotification(node);
                }

                parent = node;
                start = end + 1;
            }

            if (parent == null)
                parent = GetOrAddContentFolder(foldersByPath, entry, roots);

            parent.AssetsList.Add(entry);
            previousFolder = parent;
        }

        void Publish()
        {
            _foldersByPath = foldersByPath;
            Folders.AddRange(roots);
            if (roots.Count > 0)
            {
                var projectName = ApplicationService.ApplicationView.CUE4Parse.Provider.ProjectName;
                (roots.FirstOrDefault(x => x.Header.Equals(projectName, StringComparison.OrdinalIgnoreCase)) ?? roots[0]).IsSelected = true;
            }

            ApplicationService.ApplicationView.CUE4Parse.SearchVm.ChangeCollection(entries);
        }

        if (Application.Current?.Dispatcher.CheckAccess() == true)
            Publish();
        else
            Application.Current?.Dispatcher.Invoke(Publish);
    }

    private static TreeItem GetOrAddContentFolder(Dictionary<string, TreeItem> foldersByPath, GameFile entry, List<TreeItem> roots)
    {
        if (foldersByPath.TryGetValue("Content", out var node))
            return node;

        node = new TreeItem("Content", entry, "Content");
        foldersByPath.Add("Content", node);
        roots.Add(node);
        return node;
    }
}
