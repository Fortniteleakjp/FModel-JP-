using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CUE4Parse.FileProvider.Objects;
using FModel.Services;
using Serilog;

namespace FModel.Views.Resources.Controls;

public static class GameFileClassLoader
{
    private const string UnknownClassName = "不明";
    private static readonly SemaphoreSlim _semaphore = new(8);
    private static readonly object _cacheLock = new();
    private static readonly Dictionary<string, string> _classCache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly DependencyProperty GameFileProperty =
        DependencyProperty.RegisterAttached(
            "GameFile",
            typeof(GameFile),
            typeof(GameFileClassLoader),
            new PropertyMetadata(null, OnGameFileChanged));

    private static readonly DependencyPropertyKey ClassNamePropertyKey =
        DependencyProperty.RegisterAttachedReadOnly(
            "ClassName",
            typeof(string),
            typeof(GameFileClassLoader),
            new PropertyMetadata(UnknownClassName));

    public static readonly DependencyProperty ClassNameProperty = ClassNamePropertyKey.DependencyProperty;

    public static GameFile GetGameFile(DependencyObject obj) => (GameFile)obj.GetValue(GameFileProperty);

    public static void SetGameFile(DependencyObject obj, GameFile value) => obj.SetValue(GameFileProperty, value);

    public static string GetClassName(DependencyObject obj) => (string)obj.GetValue(ClassNameProperty);

    private static void SetClassName(DependencyObject obj, string value) => obj.SetValue(ClassNamePropertyKey, value);

    private static async void OnGameFileChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
            return;

        var file = e.NewValue as GameFile;
        if (file is null)
        {
            SetClassName(textBlock, string.Empty);
            return;
        }

        if (!file.Extension.Equals("uasset", StringComparison.OrdinalIgnoreCase) &&
            !file.Extension.Equals("umap", StringComparison.OrdinalIgnoreCase))
        {
            SetClassName(textBlock, file.Extension.ToUpperInvariant());
            return;
        }

        if (TryGetCachedClassName(file.Path, out var cachedClassName))
        {
            SetClassName(textBlock, cachedClassName);
            return;
        }

        SetClassName(textBlock, "...");

        await _semaphore.WaitAsync();
        try
        {
            if (!ReferenceEquals(GetGameFile(textBlock), file))
                return;

            var className = await Task.Run(() => ResolveClassName(file));
            if (!ReferenceEquals(GetGameFile(textBlock), file))
                return;

            var finalClassName = string.IsNullOrWhiteSpace(className) ? UnknownClassName : className;
            CacheClassName(file.Path, finalClassName);
            SetClassName(textBlock, finalClassName);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static string ResolveClassName(GameFile file)
    {
        try
        {
            var provider = ApplicationService.ApplicationView?.CUE4Parse?.Provider;
            if (provider is null)
                return UnknownClassName;

            if (!provider.TryLoadPackage(file, out var package))
                return UnknownClassName;

            return package.GetExports().FirstOrDefault()?.ExportType ?? UnknownClassName;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to resolve class name for {FilePath}", file.Path);
            return UnknownClassName;
        }
    }

    private static bool TryGetCachedClassName(string key, out string className)
    {
        lock (_cacheLock)
        {
            return _classCache.TryGetValue(key, out className);
        }
    }

    private static void CacheClassName(string key, string className)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(className))
            return;

        lock (_cacheLock)
        {
            _classCache[key] = className;
        }
    }
}
