using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Media;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using FModel.Services;
using FModel.ViewModels;

namespace FModel.Views;

public partial class SearchView
{
    private ThreadWorkerViewModel _threadWorkerView => ApplicationService.ThreadWorkerView;
    private ApplicationViewModel _applicationView => ApplicationService.ApplicationView;

    public static readonly DependencyProperty AssetTypeFilterProperty = DependencyProperty.Register(
        nameof(AssetTypeFilter), typeof(string), typeof(SearchView), new PropertyMetadata("All", OnAssetTypeFilterChanged));

    public string AssetTypeFilter
    {
        get => (string) GetValue(AssetTypeFilterProperty);
        set => SetValue(AssetTypeFilterProperty, value);
    }

    public SearchView()
    {
        DataContext = _applicationView;
        InitializeComponent();

        Activate();
        WpfSuckMyDick.Focus();
        WpfSuckMyDick.SelectAll();
    }

    private static void OnAssetTypeFilterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SearchView searchView)
            searchView.ApplyAssetTypeFilter();
    }

    private void OnDeleteSearchClick(object sender, RoutedEventArgs e)
    {
        _applicationView.CUE4Parse.SearchVm.FilterText = string.Empty;
        _applicationView.CUE4Parse.SearchVm.RefreshFilter();
    }

    private async void OnAssetDoubleClick(object sender, RoutedEventArgs e)
    {
        if (SearchListView.SelectedItem is not GameFile entry)
            return;

        WindowState = WindowState.Minimized;
        MainWindow.YesWeCats.AssetsListName.ItemsSource = null;
        var folder = _applicationView.CustomDirectories.GoToCommand.JumpTo(entry.Directory);
        if (folder == null) return;

        MainWindow.YesWeCats.Activate();

        do { await Task.Delay(100); } while (MainWindow.YesWeCats.AssetsListName.Items.Count < folder.AssetsList.Assets.Count);

        MainWindow.YesWeCats.LeftTabControl.SelectedIndex = 2; // assets tab
        do
        {
            await Task.Delay(100);
            MainWindow.YesWeCats.AssetsListName.SelectedItem = entry;
            MainWindow.YesWeCats.AssetsListName.ScrollIntoView(entry);
        } while (MainWindow.YesWeCats.AssetsListName.SelectedItem == null);
    }

    private async void OnAssetExtract(object sender, RoutedEventArgs e)
    {
        if (SearchListView.SelectedItem is not GameFile entry)
            return;

        WindowState = WindowState.Minimized;
        await _threadWorkerView.Begin(cancellationToken => _applicationView.CUE4Parse.Extract(cancellationToken, entry, true));

        MainWindow.YesWeCats.Activate();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _applicationView.CUE4Parse.SearchVm.RefreshFilter();
        ApplyAssetTypeFilter();
    }

    private void OnStateChanged(object sender, EventArgs e)
    {
        switch (WindowState)
        {
            case WindowState.Normal:
                Activate();
                WpfSuckMyDick.Focus();
                WpfSuckMyDick.SelectAll();
                return;
        }
    }

    private void ApplyAssetTypeFilter()
    {
        if (SearchListView.ItemsSource == null) return;
        var view = CollectionViewSource.GetDefaultView(SearchListView.ItemsSource);
        if (view == null) return;

        if (AssetTypeFilter == "All")
        {
            view.Filter = null;
        }
        else
        {
            view.Filter = item => item is GameFile gameFile && IsAssetTypeMatch(gameFile, AssetTypeFilter);
        }
    }

    private bool IsAssetTypeMatch(GameFile file, string filter)
    {
        if (filter == "All") return true;

        var provider = _applicationView.CUE4Parse.Provider;
        if (provider.TryLoadPackage(file.Path, out var package))
        {
            var className = GetPackageClassName(package);
            return IsTypeMatch(className, filter);
        }
        return false;
    }

    private bool IsTypeMatch(string className, string filter)
    {
        if (string.IsNullOrEmpty(className)) return false;
        if (filter == "All") return true;

        return filter switch
        {
            "Texture" => className.Contains("Texture") || className.Contains("RenderTarget"),
            "Material" => className.Contains("Material"),
            "Mesh" => className.Contains("StaticMesh") || className.Contains("SkeletalMesh"),
            "Animation" => className.Contains("Anim") || className.Contains("BlendSpace") || className.Contains("Skeleton"),
            "Sound" => className.Contains("Sound") || className.Contains("Audio"),
            "Blueprint" => className.Contains("Blueprint"),
            "Config (.ini)" => false,
            _ => true
        };
    }

    private string GetPackageClassName(IPackage ipackage)
    {
        if (ipackage == null) return null;

        if (ipackage is IoPackage ioPackage && ioPackage.ExportMap.Length > 0)
        {
            var entry = ioPackage.ExportMap[0];
            var resolved = ioPackage.ResolveObjectIndex(entry.ClassIndex);
            return resolved != null ? resolved.Name.Text : null;
        }
        
        if (ipackage is Package package && package.ExportMap.Length > 0)
        {
            return package.ExportMap[0].ClassName.ToString();
        }
        return null;
    }
}

public static class AssetMetadata
{
    private static readonly ConcurrentDictionary<string, string> _classCache = new();

    public static readonly DependencyProperty GameFileProperty = DependencyProperty.RegisterAttached(
        "GameFile", typeof(GameFile), typeof(AssetMetadata), new PropertyMetadata(null, OnGameFileChanged));

    public static void SetGameFile(DependencyObject element, GameFile value) => element.SetValue(GameFileProperty, value);
    public static GameFile GetGameFile(DependencyObject element) => (GameFile) element.GetValue(GameFileProperty);

    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(Geometry), typeof(AssetMetadata), new PropertyMetadata(null));

    public static Geometry GetIcon(DependencyObject element) => (Geometry) element.GetValue(IconProperty);

    public static readonly DependencyProperty TextColorProperty = DependencyProperty.RegisterAttached(
        "TextColor", typeof(Brush), typeof(AssetMetadata), new PropertyMetadata(Brushes.White));

    public static Brush GetTextColor(DependencyObject element) => (Brush) element.GetValue(TextColorProperty);

    private static async void OnGameFileChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        // Reset to defaults
        element.SetCurrentValue(IconProperty, GetGeometry("File"));
        element.SetCurrentValue(TextColorProperty, Application.Current.TryFindResource(AdonisUI.Brushes.ForegroundBrush) as Brush ?? Brushes.White);

        if (e.NewValue is not GameFile gameFile) return;

        string className;
        if (_classCache.TryGetValue(gameFile.Path, out var cached))
        {
            className = cached;
        }
        else
        {
            className = await Task.Run(() =>
            {
                var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
                if (provider.TryLoadPackage(gameFile.Path, out var package))
                {
                    return GetPackageClassName(package);
                }
                return null;
            });
            
            if (className != null)
                _classCache.TryAdd(gameFile.Path, className);
        }

        // Check if the element is still displaying the same GameFile (virtualization check)
        if (GetGameFile(element) == gameFile)
        {
            element.SetCurrentValue(IconProperty, GetIconForClass(className));
            element.SetCurrentValue(TextColorProperty, GetBrushForClass(className));
        }
    }

    private static string GetPackageClassName(IPackage ipackage)
    {
        if (ipackage == null) return null;

        if (ipackage is IoPackage ioPackage && ioPackage.ExportMap.Length > 0)
        {
            var entry = ioPackage.ExportMap[0];
            var resolved = ioPackage.ResolveObjectIndex(entry.ClassIndex);
            return resolved != null ? resolved.Name.Text : null;
        }
        
        if (ipackage is Package package && package.ExportMap.Length > 0)
        {
            return package.ExportMap[0].ClassName.ToString();
        }
        return null;
    }

    private static Geometry GetIconForClass(string className)
    {
        var key = className switch
        {
            "Texture2D" or "TextureCube" or "TextureRenderTarget2D" => "Texture",
            "Material" or "MaterialInstanceConstant" => "Texture",
            "StaticMesh" or "SkeletalMesh" => "Model",
            "Blueprint" or "BlueprintGeneratedClass" => "Note",
            "SoundWave" or "SoundCue" => "Audio",
            "AnimSequence" or "AnimMontage" or "BlendSpace" => "Animation",
            _ => "File"
        };
        return GetGeometry(key);
    }

    private static Geometry GetGeometry(string key) => 
        Application.Current.TryFindResource($"{key}Icon") as Geometry ?? Application.Current.TryFindResource("FileIcon") as Geometry;

    private static Brush GetBrushForClass(string className)
    {
        var brush = className switch
        {
            "Texture2D" or "TextureCube" or "TextureRenderTarget2D" => Brushes.SandyBrown,
            "Material" or "MaterialInstanceConstant" => Brushes.LightGreen,
            "StaticMesh" or "SkeletalMesh" => Brushes.Cyan,
            "Blueprint" or "BlueprintGeneratedClass" => Brushes.LightBlue,
            "SoundWave" or "SoundCue" => Brushes.Orange,
            "AnimSequence" or "AnimMontage" or "BlendSpace" => Brushes.Plum,
            _ => Application.Current.TryFindResource(AdonisUI.Brushes.ForegroundBrush) as Brush ?? Brushes.White
        };
        return brush;
    }
}
