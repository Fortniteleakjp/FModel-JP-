﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Data;
using CUE4Parse.FileProvider.Objects;
using FModel.Framework;
using System.Windows.Input;
using System.Threading;
using System.Threading.Tasks;

namespace FModel.ViewModels;

public enum ESortMode
{
    Asc,
    Desc
}

public enum EAssetTypeFilter
{
    All,
    UAsset,
    UMap,
    UExp,
    UBulk,
    UPTNL,
    Texture,
    Audio,
    Model,
    Animation,
    Other
}

public class SearchViewModel : ViewModel
{
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new();
    private CancellationTokenSource? _filterCts;
    private const int FilterDelayMs = 150;

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

    private string _filterText = string.Empty;
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                DebouncedRefreshFilter();
            }
        }
    }

    private string _excludeText = string.Empty;
    public string ExcludeText
    {
        get => _excludeText;
        set
        {
            if (SetProperty(ref _excludeText, value))
            {
                DebouncedRefreshFilter();
            }
        }
    }

    private EAssetTypeFilter _assetTypeFilter = EAssetTypeFilter.All;
    public EAssetTypeFilter AssetTypeFilter
    {
        get => _assetTypeFilter;
        set
        {
            if (SetProperty(ref _assetTypeFilter, value))
            {
                RefreshFilter();
            }
        }
    }

    private bool _hasRegexEnabled;
    public bool HasRegexEnabled
    {
        get => _hasRegexEnabled;
        set
        {
            if (SetProperty(ref _hasRegexEnabled, value))
            {
                RegexCache.Clear();
                RefreshFilter();
            }
        }
    }

    private bool _hasMatchCaseEnabled;
    public bool HasMatchCaseEnabled
    {
        get => _hasMatchCaseEnabled;
        set
        {
            if (SetProperty(ref _hasMatchCaseEnabled, value))
            {
                RegexCache.Clear();
                RefreshFilter();
            }
        }
    }

    public int ResultsCount => SearchResults?.Count ?? 0;
    public RangeObservableCollection<GameFile> SearchResults { get; }
    public ICollectionView SearchResultsView { get; }
    public RangeObservableCollection<GameFile> ContentSearchResults { get; }

    public ICommand SortCommand { get; }

    public Array AssetTypeFilters => Enum.GetValues(typeof(EAssetTypeFilter));

    // Pre-computed filter sets for performance
    private static readonly HashSet<string> TextureExtensions = new(StringComparer.OrdinalIgnoreCase) { "png", "jpg", "jpeg", "bmp", "tga", "dds", "hdr" };
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase) { "wem", "bnk", "ogg", "mp3", "wav" };
    private static readonly HashSet<string> ModelExtensions = new(StringComparer.OrdinalIgnoreCase) { "psk", "pskx", "fbx", "gltf", "glb" };
    private static readonly HashSet<string> AnimationExtensions = new(StringComparer.OrdinalIgnoreCase) { "uasset", "ase", "asex" };

    public SearchViewModel()
    {
        SearchResults = new RangeObservableCollection<GameFile>();
        SearchResultsView = new ListCollectionView(SearchResults);
        ContentSearchResults = new RangeObservableCollection<GameFile>();
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

    private void DebouncedRefreshFilter()
    {
        _filterCts?.Cancel();
        _filterCts = new CancellationTokenSource();
        var token = _filterCts.Token;

        Task.Delay(FilterDelayMs, token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(RefreshFilter);
            }
        }, TaskScheduler.Default);
    }

    public void RefreshFilter()
    {
        if (SearchResultsView.Filter == null)
            SearchResultsView.Filter = e => ItemFilter(e, FilterText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        else
            SearchResultsView.Refresh();
    }

    private bool ItemFilter(object item, IEnumerable<string> filters)
    {
        if (item is not GameFile entry)
            return true;

        // Asset type filter - optimized with early exit
        if (!AssetTypeFilter.Equals(EAssetTypeFilter.All))
        {
            var ext = entry.Extension;
            var passesTypeFilter = AssetTypeFilter switch
            {
                EAssetTypeFilter.UAsset => string.Equals(ext, "uasset", StringComparison.OrdinalIgnoreCase),
                EAssetTypeFilter.UMap => string.Equals(ext, "umap", StringComparison.OrdinalIgnoreCase),
                EAssetTypeFilter.UExp => string.Equals(ext, "uexp", StringComparison.OrdinalIgnoreCase),
                EAssetTypeFilter.UBulk => string.Equals(ext, "ubulk", StringComparison.OrdinalIgnoreCase),
                EAssetTypeFilter.UPTNL => string.Equals(ext, "uptnl", StringComparison.OrdinalIgnoreCase),
                EAssetTypeFilter.Texture => TextureExtensions.Contains(ext),
                EAssetTypeFilter.Audio => AudioExtensions.Contains(ext),
                EAssetTypeFilter.Model => ModelExtensions.Contains(ext),
                EAssetTypeFilter.Animation => AnimationExtensions.Contains(ext),
                EAssetTypeFilter.Other => !GameFile.UeKnownExtensionsSet.Contains(ext),
                _ => true
            };

            if (!passesTypeFilter)
                return false;
        }

        // Exclusion filter - optimized with early exit
        if (!string.IsNullOrWhiteSpace(ExcludeText))
        {
            var excludeFilters = ExcludeText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var pathLower = entry.Path;
            foreach (var exclude in excludeFilters)
            {
                if (pathLower.Contains(exclude, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }

        // Main filter
        if (!HasRegexEnabled)
        {
            var path = entry.Path;
            foreach (var filter in filters)
            {
                if (!path.Contains(filter, HasMatchCaseEnabled ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        // Regex with cache
        var cacheKey = $"{FilterText}_{HasMatchCaseEnabled}";
        var regex = RegexCache.GetOrAdd(cacheKey, _ =>
        {
            var options = RegexOptions.None;
            if (!HasMatchCaseEnabled) options |= RegexOptions.IgnoreCase;
            return new Regex(FilterText, options, TimeSpan.FromSeconds(1));
        });

        try
        {
            return regex.IsMatch(entry.Path);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
