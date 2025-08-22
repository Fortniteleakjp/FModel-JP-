using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    private ESortMode _sortMode;
    public ESortMode SortMode
    {
        get => _sortMode;
        set
        {
            if (SetProperty(ref _sortMode, value))
            {
                Sort(value);
            }
        }
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

    public int ResultsCount => SearchResults?.Count ?? 0;
    public RangeObservableCollection<GameFile> SearchResults { get; }
    public ICollectionView SearchResultsView { get; }

    public ICommand SortCommand { get; }

    public SearchViewModel()
    {
        SearchResults = new RangeObservableCollection<GameFile>();
        SearchResultsView = new ListCollectionView(SearchResults);
        SortCommand = new RelayCommand(Sort);
        SortMode = ESortMode.Asc;
    }

    private void Sort(object? mode)
    {
        var modeStr = mode?.ToString();
        if (string.IsNullOrEmpty(modeStr)) return;

        var direction = modeStr.Equals("Asc", StringComparison.OrdinalIgnoreCase) ? ListSortDirection.Ascending : ListSortDirection.Descending;
        SearchResultsView.SortDescriptions.Clear();
        SearchResultsView.SortDescriptions.Add(new SortDescription("Path", direction));
    }

    public void RefreshFilter()
    {
        if (SearchResultsView.Filter == null)
            SearchResultsView.Filter = e => ItemFilter(e, FilterText.Trim().Split(' '));
        else
            SearchResultsView.Refresh();
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
