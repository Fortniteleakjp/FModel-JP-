using System;
using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;
using FModel.Creator.Layout;
using FModel.Framework;
using FModel.Settings;
using Serilog;
using SkiaSharp;

namespace FModel.ViewModels;

public class IconLayoutEditorViewModel : ViewModel
{
    private readonly IconLayoutSettings _settings;

    /// <summary>プレビューに使うアイテムデータ（現在開いているアセット、無ければサンプル）。</summary>
    public LayoutRenderContext Context { get; }

    public string ContextInfo { get; }

    public Array Categories => Enum.GetValues(typeof(EIconLayoutCategory));
    public Array BackgroundModes => Enum.GetValues(typeof(EIconLayoutBackground));
    public Array Aligns => Enum.GetValues(typeof(EIconLayoutAlign));

    private EIconLayoutCategory _selectedCategory;
    public EIconLayoutCategory SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (_selectedCategory == value && CurrentTemplate != null) return;
            _selectedCategory = value;
            RaisePropertyChanged(nameof(SelectedCategory));
            SetCurrentTemplate(_settings.Get(value));
        }
    }

    private IconLayoutTemplate _currentTemplate;
    public IconLayoutTemplate CurrentTemplate
    {
        get => _currentTemplate;
        private set => SetProperty(ref _currentTemplate, value);
    }

    private BitmapImage _previewImage;
    public BitmapImage PreviewImage
    {
        get => _previewImage;
        private set => SetProperty(ref _previewImage, value);
    }

    public IconLayoutEditorViewModel()
    {
        _settings = UserSettings.Default.IconLayout ??= new IconLayoutSettings();
        Context = IconLayoutPreview.Current ?? LayoutRenderContext.Sample();
        ContextInfo = string.IsNullOrWhiteSpace(Context.DisplayName)
            ? "プレビュー対象: サンプル"
            : $"プレビュー対象: {Context.DisplayName}";

        _selectedCategory = Context.Category;
        SetCurrentTemplate(_settings.Get(_selectedCategory));
    }

    private void SetCurrentTemplate(IconLayoutTemplate template)
    {
        if (CurrentTemplate != null)
            Unsubscribe(CurrentTemplate);

        CurrentTemplate = template;

        if (CurrentTemplate != null)
            Subscribe(CurrentTemplate);

        Render();
    }

    private void Subscribe(IconLayoutTemplate t)
    {
        t.PropertyChanged += OnTemplateChanged;
        t.Preview.PropertyChanged += OnElementChanged;
        t.Name.PropertyChanged += OnElementChanged;
        t.Description.PropertyChanged += OnElementChanged;
    }

    private void Unsubscribe(IconLayoutTemplate t)
    {
        t.PropertyChanged -= OnTemplateChanged;
        t.Preview.PropertyChanged -= OnElementChanged;
        t.Name.PropertyChanged -= OnElementChanged;
        t.Description.PropertyChanged -= OnElementChanged;
    }

    private void OnTemplateChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IconLayoutTemplate.BackgroundImagePath))
            IconLayoutRenderer.InvalidateBackgroundCache();
        Render();
    }

    private void OnElementChanged(object sender, PropertyChangedEventArgs e) => Render();

    public void Render()
    {
        try
        {
            using var bmp = IconLayoutRenderer.Render(CurrentTemplate, Context);
            PreviewImage = ToBitmapImage(bmp);
        }
        catch (Exception e)
        {
            Log.Warning(e, "アイコンレイアウトのプレビュー描画に失敗しました");
        }
    }

    public void ResetCurrent()
    {
        _settings.ResetToDefault(_selectedCategory);
        SetCurrentTemplate(_settings.Get(_selectedCategory));
    }

    public void Save()
    {
        UserSettings.Save();
    }

    private static BitmapImage ToBitmapImage(SKBitmap bmp)
    {
        if (bmp == null) return null;
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream(data.ToArray(), false);
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        return img;
    }
}
