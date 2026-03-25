using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using FModel.Services;
using Serilog;

namespace FModel.Views.Resources.Controls;

public static class WeaponCardNameLoader
{
    private const string TargetExportType = "FortWeaponRangedItemDefinition";
    private static readonly SemaphoreSlim _semaphore = new(8);
    private static readonly object _cacheLock = new();
    private static readonly Dictionary<string, string> _displayNameCache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly DependencyProperty GameFileProperty =
        DependencyProperty.RegisterAttached(
            "GameFile",
            typeof(GameFile),
            typeof(WeaponCardNameLoader),
            new PropertyMetadata(null, OnGameFileChanged));

    private static readonly DependencyPropertyKey DisplayNamePropertyKey =
        DependencyProperty.RegisterAttachedReadOnly(
            "DisplayName",
            typeof(string),
            typeof(WeaponCardNameLoader),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DisplayNameProperty = DisplayNamePropertyKey.DependencyProperty;

    public static GameFile GetGameFile(DependencyObject obj) => (GameFile)obj.GetValue(GameFileProperty);

    public static void SetGameFile(DependencyObject obj, GameFile value) => obj.SetValue(GameFileProperty, value);

    public static string GetDisplayName(DependencyObject obj) => (string)obj.GetValue(DisplayNameProperty);

    private static void SetDisplayName(DependencyObject obj, string value) => obj.SetValue(DisplayNamePropertyKey, value);

    private static async void OnGameFileChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
            return;

        var file = e.NewValue as GameFile;
        if (file == null)
        {
            SetDisplayName(textBlock, string.Empty);
            return;
        }

        SetDisplayName(textBlock, file.Name);

        if (!file.Extension.Equals("uasset", StringComparison.OrdinalIgnoreCase) &&
            !file.Extension.Equals("umap", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (TryGetCachedDisplayName(file.Path, out var cachedName))
        {
            SetDisplayName(textBlock, cachedName);
            return;
        }

        try
        {
            // デバウンス処理：セマフォに並ぶ前に、アイテムがまだ有効か確認
            await Task.Delay(50).ConfigureAwait(true);
            if (!ReferenceEquals(GetGameFile(textBlock), file)) return;

            await _semaphore.WaitAsync();
            if (!ReferenceEquals(GetGameFile(textBlock), file))
                return;

            var displayName = await Task.Run(() => ResolveDisplayName(file));
            if (!ReferenceEquals(GetGameFile(textBlock), file))
                return;

            var finalDisplayName = string.IsNullOrWhiteSpace(displayName) ? file.Name : displayName;
            CacheDisplayName(file.Path, finalDisplayName);
            SetDisplayName(textBlock, finalDisplayName);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static string ResolveDisplayName(GameFile file)
    {
        try
        {
            var provider = ApplicationService.ApplicationView?.CUE4Parse?.Provider;
            if (provider == null)
                return file.Name;

            if (!provider.TryLoadPackage(file, out var package))
                return file.Name;

            var export = package.GetExports().FirstOrDefault();
            if (export == null || !string.Equals(export.ExportType, TargetExportType, StringComparison.OrdinalIgnoreCase))
                return file.Name;

            if (export.TryGetValue(out FText itemName, "ItemName") && !string.IsNullOrWhiteSpace(itemName.Text))
                return itemName.Text;

            if (export.TryGetValue(out FStructFallback itemNameStruct, "ItemName"))
            {
                if (itemNameStruct.TryGetValue(out string localizedString, "LocalizedString") && !string.IsNullOrWhiteSpace(localizedString))
                    return localizedString;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to resolve card display name for {FilePath}", file.Path);
        }

        return file.Name;
    }

    private static bool TryGetCachedDisplayName(string key, out string displayName)
    {
        lock (_cacheLock)
        {
            return _displayNameCache.TryGetValue(key, out displayName);
        }
    }

    private static void CacheDisplayName(string key, string displayName)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(displayName))
            return;

        lock (_cacheLock)
        {
            _displayNameCache[key] = displayName;
        }
    }
}