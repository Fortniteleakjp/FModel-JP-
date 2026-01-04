using System;
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

public class AssetIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is GameFile gameFile)
        {
            var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
            if (provider.TryLoadPackage(gameFile.Path, out var package))
            {
                var className = GetPackageClassName(package);
                return GetIconForClass(className);
            }
        }
        return GetIcon("File");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();

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

    private Geometry GetIconForClass(string className)
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
        return GetIcon(key);
    }

    private Geometry GetIcon(string key) => 
        Application.Current.TryFindResource($"{key}Icon") as Geometry ?? Application.Current.TryFindResource("FileIcon") as Geometry;
}
