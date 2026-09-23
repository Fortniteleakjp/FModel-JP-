using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Data;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.VirtualFileSystem;
using FModel.Framework;
using FModel.Services;
using Serilog;

namespace FModel.ViewModels;

public class SearchViewModel : ViewModel
{
    /// <summary>One checkbox of the asset type filter.</summary>
    public sealed class CategoryFilterOption : ViewModel
    {
        public EAssetCategory Category { get; init; }
        public string Header { get; init; }

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set => SetProperty(ref _isChecked, value);
        }
    }

    private sealed class SortCache
    {
        public required int CollectionVersion { get; init; }
        public required List<GameFile> Default { get; init; }
        public required List<GameFile> Ascending { get; init; }
        public required List<GameFile> Descending { get; init; }

        public List<GameFile> Get(ESortSizeMode mode) => mode switch
        {
            ESortSizeMode.Ascending => Ascending,
            ESortSizeMode.Descending => Descending,
            _ => Default
        };
    }

    public enum ESortSizeMode
    {
        None,
        Ascending,
        Descending
    }

    private string _filterText = string.Empty;
    public string FilterText
    {
        get => _filterText;
        set => SetProperty(ref _filterText, value);
    }

    private bool _hasRegexEnabled;
    public bool HasRegexEnabled
    {
        get => _hasRegexEnabled;
        set => SetProperty(ref _hasRegexEnabled, value);
    }

    private bool _hasMatchCaseEnabled;
    public bool HasMatchCaseEnabled
    {
        get => _hasMatchCaseEnabled;
        set => SetProperty(ref _hasMatchCaseEnabled, value);
    }

    private ESortSizeMode _currentSortSizeMode = ESortSizeMode.None;
    public ESortSizeMode CurrentSortSizeMode
    {
        get => _currentSortSizeMode;
        set => SetProperty(ref _currentSortSizeMode, value);
    }

    private int _resultsCount = 0;
    public int ResultsCount
    {
        get => _resultsCount;
        private set => SetProperty(ref _resultsCount, value);
    }

    private GameFile _refFile;
    public GameFile RefFile
    {
        get => _refFile;
        private set => SetProperty(ref _refFile, value);
    }

    private List<GameFile> _searchResults = [];
    public List<GameFile> SearchResults
    {
        get => _searchResults;
        private set => SetProperty(ref _searchResults, value);
    }
    private ListCollectionView _searchResultsView;
    private string[] _filters = [];
    private Regex _filterRegex;
    private bool _isRegexValid = true;
    private int _collectionVersion;
    private SortCache _sortCache;
    private uint _categoryMask;

    /// <summary>Asset type checkboxes, guessed from the path (see <see cref="AssetCategoryGuesser"/>). None checked = no filter.</summary>
    public IReadOnlyList<CategoryFilterOption> CategoryFilters { get; }

    private int _checkedCategoryCount;
    public int CheckedCategoryCount
    {
        get => _checkedCategoryCount;
        private set => SetProperty(ref _checkedCategoryCount, value);
    }

    // the list binds with IsAsync=True, so the view is first read on a pool thread while the search timer
    // refreshes it on the UI thread: without the lock both could build their own view, the list then kept
    // showing one while the filter was applied to the other (every search listed the whole collection)
    private readonly object _viewLock = new();

    public ListCollectionView SearchResultsView
    {
        get
        {
            lock (_viewLock)
            {
                if (_searchResultsView != null)
                    return _searchResultsView;

                var watch = Stopwatch.StartNew();
                PrepareFilter();
                _searchResultsView = new ListCollectionView(SearchResults)
                {
                    Filter = ItemFilter,
                };
                ResultsCount = _searchResultsView.Count;
                Log.Information("{Name}: list built, {Shown}/{Total} files for {Filter} in {Elapsed} ms (thread {Thread})",
                    _name, ResultsCount, SearchResults.Count, DescribeFilter(), watch.ElapsedMilliseconds, Environment.CurrentManagedThreadId);
                return _searchResultsView;
            }
        }
    }

    /// <summary>Which window tab this is (search, references), for the log.</summary>
    private readonly string _name;

    public SearchViewModel(string name = "Search")
    {
        _name = name;
        ResultsCount = 0;

        CategoryFilters = new[]
        {
            EAssetCategory.Texture, EAssetCategory.Materials, EAssetCategory.Mesh, EAssetCategory.Animation,
            EAssetCategory.Blueprints, EAssetCategory.Data, EAssetCategory.Media, EAssetCategory.Particle,
            EAssetCategory.Level, AssetCategoryGuesser.Unknown
        }.Select(c => new CategoryFilterOption
        {
            Category = c,
            Header = c == AssetCategoryGuesser.Unknown ? "Other" : c.ToString()
        }).ToArray();

        foreach (var option in CategoryFilters)
            option.PropertyChanged += OnCategoryFilterChanged;
    }

    private bool _isUpdatingCategories;

    private void OnCategoryFilterChanged(object sender, PropertyChangedEventArgs e)
    {
        if (_isUpdatingCategories || e.PropertyName != nameof(CategoryFilterOption.IsChecked))
            return;

        CheckedCategoryCount = CategoryFilters.Count(x => x.IsChecked);
        RefreshFilter();
    }

    public void ClearCategoryFilters()
    {
        if (CheckedCategoryCount == 0)
            return;

        _isUpdatingCategories = true;
        foreach (var option in CategoryFilters)
            option.IsChecked = false;
        _isUpdatingCategories = false;

        CheckedCategoryCount = 0;
        RefreshFilter();
    }

    // base categories are CategoryBase + (n << 16), so the high word is a small index
    private static uint CategoryBit(EAssetCategory category) => 1u << (int) ((uint) category >> 16);

    public void RefreshFilter()
    {
        var view = SearchResultsView;
        lock (_viewLock)
        {
            // the collection may have been replaced meanwhile, its new view already filters with the current text
            if (!ReferenceEquals(view, _searchResultsView))
            {
                Log.Information("{Name}: search for {Filter} skipped, the list was replaced meanwhile", _name, DescribeFilter());
                return;
            }

            var watch = Stopwatch.StartNew();
            PrepareFilter();
            view.Refresh();
            ResultsCount = view.Count;

            if (HasRegexEnabled && !_isRegexValid)
                Log.Warning("{Name}: invalid regular expression {Filter}, nothing matches", _name, DescribeFilter());
            else
                Log.Information("{Name}: search for {Filter} -> {Shown}/{Total} files in {Elapsed} ms",
                    _name, DescribeFilter(), ResultsCount, SearchResults.Count, watch.ElapsedMilliseconds);
        }
    }

    public void ChangeCollection(IEnumerable<GameFile> files, GameFile refFile = null)
    {
        var results = files as List<GameFile> ?? files.ToList();
        _collectionVersion++;
        _sortCache = null;
        ApplyCollection(results, refFile);

        if (refFile != null)
            Log.Information("{Name}: list replaced with {Total} files referencing '{RefFile}'", _name, results.Count, refFile.Path);
        else
            Log.Information("{Name}: list replaced with {Total} files", _name, results.Count);
    }

    private void ApplyCollection(List<GameFile> results, GameFile refFile)
    {
        lock (_viewLock)
        {
            _searchResultsView = null;
            _searchResults = results;
        }

        RaisePropertyChanged(nameof(SearchResults));
        RaisePropertyChanged(nameof(SearchResultsView));
        RefFile = refFile;
        ResultsCount = results.Count;
    }

    public void Clear() => ChangeCollection([]);

    public async Task CycleSortSizeMode()
    {
        CurrentSortSizeMode = CurrentSortSizeMode switch
        {
            ESortSizeMode.None => ESortSizeMode.Descending,
            ESortSizeMode.Descending => ESortSizeMode.Ascending,
            _ => ESortSizeMode.None
        };
        Log.Information("{Name}: sort by size {Mode}", _name, CurrentSortSizeMode);

        var collectionVersion = _collectionVersion;
        var refFile = RefFile;
        var sortCache = _sortCache;

        if (sortCache is null || sortCache.CollectionVersion != collectionVersion)
        {
            var source = SearchResults.ToArray();
            sortCache = await Task.Run(() =>
            {
                var archiveDict = source
                    .OfType<VfsEntry>()
                    .Select(f => f.Vfs.Name)
                    .Distinct()
                    .Select((name, idx) => (name, idx))
                    .ToDictionary(x => x.name, x => x.idx);

                var keyed = source.Select(f =>
                {
                    var archiveKey = f is VfsEntry ve && archiveDict.TryGetValue(ve.Vfs.Name, out var key) ? key : -1;
                    return (File: f, f.Size, ArchiveKey: archiveKey);
                }).ToArray();

                return new SortCache
                {
                    CollectionVersion = collectionVersion,
                    Ascending = keyed
                        .OrderBy(x => x.Size).ThenBy(x => x.ArchiveKey)
                        .Select(x => x.File).ToList(),
                    Descending = keyed
                        .OrderByDescending(x => x.Size).ThenBy(x => x.ArchiveKey)
                        .Select(x => x.File).ToList(),
                    Default = keyed
                        .OrderBy(x => x.ArchiveKey).ThenBy(x => x.File.Path, StringComparer.OrdinalIgnoreCase)
                        .Select(x => x.File).ToList()
                };
            });

            if (collectionVersion != _collectionVersion)
                return;

            _sortCache = sortCache;
        }

        ApplyCollection(sortCache.Get(CurrentSortSizeMode), refFile);
    }

    /// <summary>The search text and its options as the log shows them.</summary>
    private string DescribeFilter()
    {
        var options = new List<string>();
        if (HasRegexEnabled) options.Add("regex");
        if (HasMatchCaseEnabled) options.Add("match case");
        if (CategoryFilters.Any(x => x.IsChecked))
            options.Add("types: " + string.Join("/", CategoryFilters.Where(x => x.IsChecked).Select(x => x.Header)));
        return $"'{FilterText}'" + (options.Count > 0 ? $" ({string.Join(", ", options)})" : string.Empty);
    }

    private void PrepareFilter()
    {
        _filters = FilterText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _filterRegex = null;
        _isRegexValid = true;

        _categoryMask = 0;
        foreach (var option in CategoryFilters)
        {
            if (option.IsChecked)
                _categoryMask |= CategoryBit(option.Category);
        }

        if (!HasRegexEnabled)
            return;

        var options = RegexOptions.Compiled;
        if (!HasMatchCaseEnabled)
            options |= RegexOptions.IgnoreCase;

        try
        {
            _filterRegex = new Regex(FilterText, options);
        }
        catch (ArgumentException)
        {
            _isRegexValid = false;
        }
    }

    private bool ItemFilter(object item)
    {
        if (item is not GameFile entry)
            return true;

        var matchesText = HasRegexEnabled
            ? _isRegexValid && _filterRegex.IsMatch(entry.Path)
            : _filters.All(x => entry.Path.Contains(x,
                HasMatchCaseEnabled ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));

        // the text test is cheaper, so the type is only guessed for files that already match
        return matchesText && (_categoryMask == 0 || (_categoryMask & CategoryBit(AssetCategoryGuesser.Guess(entry.Path))) != 0);
    }
}
