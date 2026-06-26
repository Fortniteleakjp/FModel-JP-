using System;
using System.Collections.Generic; // List<> を使用するために追加
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AdonisUI.Controls;
using UAssetAPI;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using CUE4Parse.UE4.Assets;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Exceptions;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels;
using FModel.Views;
using FModel.Views.Resources.Controls;
using ICSharpCode.AvalonEdit.Editing;
using FModel.Framework; // RelayCommand を使用するためのやつ
using System.Collections.Specialized; // NotifyCollectionChangedEventArgs を使用するためのやつ
using Microsoft.Win32;
using FModel.Features.Athena;
using Serilog;
using Ookii.Dialogs.Wpf;
using UAssetAPI.PropertyTypes.Structs;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxButton = AdonisUI.Controls.MessageBoxButton;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;
using Newtonsoft.Json;

namespace FModel;

public partial class MainWindow
{
    private const string ExportTypeCacheMiss = "__EXPORT_TYPE_MISS__";
    private readonly object _assetExportTypeCacheLock = new();
    private readonly Dictionary<string, string> _assetExportTypeCache = new(StringComparer.OrdinalIgnoreCase);

    private bool IsFortWeaponRangedItemDefinitionAsset(GameFile file)
    {
        if (file == null)
            return false;

        if (!file.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
            !file.Path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryGetAssetExportType(file, out var exportType) &&
               string.Equals(exportType, "FortWeaponRangedItemDefinition", StringComparison.OrdinalIgnoreCase);
    }

    private List<GameFile> BuildSearchResultFiles(string query, bool searchWeaponDefinitionsOnly)
    {
        var provider = _applicationView.CUE4Parse.Provider;
        if (provider?.Files == null || provider.Files.Count == 0)
            return new List<GameFile>();

        var allFiles = provider.Files.Values
            .OfType<GameFile>()
            .DistinctBy(f => f.Path);

        if (searchWeaponDefinitionsOnly)
        {
            return allFiles
                .Where(f => f.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ||
                            f.Path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                .Where(IsFortWeaponRangedItemDefinitionAsset)
                .ToList();
        }

        return allFiles
            .Where(f => f.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private bool TryGetAssetExportType(GameFile file, out string exportType)
    {
        exportType = null;
        if (file == null)
            return false;

        lock (_assetExportTypeCacheLock)
        {
            if (_assetExportTypeCache.TryGetValue(file.Path, out exportType))
                return !string.IsNullOrWhiteSpace(exportType) && !string.Equals(exportType, ExportTypeCacheMiss, StringComparison.Ordinal);
        }

        try
        {
            if (!_applicationView.CUE4Parse.Provider.TryLoadPackage(file, out var package))
            {
                lock (_assetExportTypeCacheLock)
                {
                    _assetExportTypeCache[file.Path] = ExportTypeCacheMiss;
                }
                return false;
            }

            var resolvedType = package.GetExports().FirstOrDefault()?.ExportType;
            if (string.IsNullOrWhiteSpace(resolvedType))
            {
                lock (_assetExportTypeCacheLock)
                {
                    _assetExportTypeCache[file.Path] = ExportTypeCacheMiss;
                }
                return false;
            }

            lock (_assetExportTypeCacheLock)
            {
                _assetExportTypeCache[file.Path] = resolvedType;
            }

            exportType = resolvedType;
            return true;
        }
        catch
        {
            lock (_assetExportTypeCacheLock)
            {
                _assetExportTypeCache[file.Path] = ExportTypeCacheMiss;
            }
            return false;
        }
    }
}
