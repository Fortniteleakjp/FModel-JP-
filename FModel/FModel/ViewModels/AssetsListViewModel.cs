using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Data;
using CUE4Parse.FileProvider.Objects;
using FModel.Framework;

namespace FModel.ViewModels;

/// <summary>
/// フォルダが大量のパッケージを含む場合でも、実際に表示するまで ViewModel を生成しない一覧です。
/// </summary>
public sealed class AssetsListViewModel : ViewModel
{
    private List<GameFile> _pendingAssets;
    private RangeObservableCollection<GameFile> _assets;
    private Predicate<object> _filter;
    private ICollectionView _assetsView;

    public RangeObservableCollection<GameFile> Assets
    {
        get
        {
            if (_assets != null)
                return _assets;

            _assets = [];
            if (_pendingAssets == null)
                return _assets;

            foreach (var asset in _pendingAssets)
                _assets.AddWithoutNotification(asset);

            _pendingAssets = null;
            return _assets;
        }
    }

    public int Count => _assets?.Count ?? _pendingAssets?.Count ?? 0;

    public ICollectionView AssetsView => _assetsView ??= new ListCollectionView(Assets)
    {
        SortDescriptions = { new SortDescription(nameof(GameFile.Path), ListSortDirection.Ascending) },
        Filter = _filter
    };

    public void Add(GameFile gameFile)
    {
        if (_assets == null)
            (_pendingAssets ??= []).Add(gameFile);
        else
            _assets.Add(gameFile);

        RaisePropertyChanged(nameof(Count));
    }

    public void SetFilter(Predicate<object> filter)
    {
        _filter = filter;
        if (_assetsView != null)
            _assetsView.Filter = filter;
    }

    public void RefreshView() => _assetsView?.Refresh();
}
