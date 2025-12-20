﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Data;
using CUE4Parse.FileProvider.Objects;
using FModel.Framework;
using System.Windows.Input;

namespace FModel.ViewModels;

public enum ESortMode
{
    Asc,
    Desc
}

public class SearchViewModel : ViewModel
{
    public enum ESortSizeMode
    {
        None,
        Ascending,
        Descending
    }

    private ESortSizeMode _currentSortSizeMode = ESortSizeMode.None;
    public ESortSizeMode CurrentSortSizeMode
    {
        get => _currentSortSizeMode;
        set => SetProperty(ref _currentSortSizeMode, value);
    }

    private string _filterText;
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

    public RangeObservableCollection<GameFile> SearchResults { get; }
    public ListCollectionView SearchResultsView { get; }

    public SearchViewModel()
    {
        SearchResults = new RangeObservableCollection<GameFile>();
        SearchResultsView = new ListCollectionView(SearchResults)
        {
            Filter = e => ItemFilter(e, FilterText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)),
        };
        ResultsCount = SearchResultsView.Count;
    }


    public void ChangeCollection(IEnumerable<GameFile> files, GameFile refFile = null)
    {
        SearchResults.Clear();
        SearchResults.AddRange(files);
        RefFile = refFile;
        ResultsCount = SearchResultsView.Count;
    }

    public async Task CycleSortSizeMode()
    {
        CurrentSortSizeMode = CurrentSortSizeMode switch
        {
            ESortSizeMode.None => ESortSizeMode.Descending,
            ESortSizeMode.Descending => ESortSizeMode.Ascending,
            _ => ESortSizeMode.None
        };

        var sorted = await Task.Run(() =>
        {
            var archiveDict = SearchResults
                .OfType<CUE4Parse.UE4.VirtualFileSystem.VfsEntry>()
                .Select(f => f.Vfs.Name)
                .Distinct()
                .Select((name, idx) => (name, idx))
                .ToDictionary(x => x.name, x => x.idx);

            var keyed = SearchResults.Select(f =>
            {
                int archiveKey = f is CUE4Parse.UE4.VirtualFileSystem.VfsEntry ve && archiveDict.TryGetValue(ve.Vfs.Name, out var key) ? key : -1;
                return (File: f, f.Size, ArchiveKey: archiveKey);
            });

            return CurrentSortSizeMode switch
            {
                ESortSizeMode.Ascending => keyed
                    .OrderBy(x => x.Size).ThenBy(x => x.ArchiveKey)
                    .Select(x => x.File).ToList(),
                ESortSizeMode.Descending => keyed
                    .OrderByDescending(x => x.Size).ThenBy(x => x.ArchiveKey)
                    .Select(x => x.File).ToList(),
                _ => keyed
                    .OrderBy(x => x.ArchiveKey).ThenBy(x => x.File.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.File).ToList()
            };
        });

        SearchResults.Clear();
        SearchResults.AddRange(sorted);
    }

    public void RefreshFilter()
    {
        SearchResultsView.Refresh();
        ResultsCount = SearchResultsView.Count;
    }

    private bool ItemFilter(object item, IEnumerable<string> filters)
    {
        if (item is not GameFile entry)
            return true;

        if (!HasRegexEnabled)
            return filters.All(x => entry.Path.Contains(x, HasMatchCaseEnabled ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));

        var o = RegexOptions.None;
        if (!HasMatchCaseEnabled) o |= RegexOptions.IgnoreCase;
        return new Regex(FilterText, o).Match(entry.Path).Success;
    }
}
