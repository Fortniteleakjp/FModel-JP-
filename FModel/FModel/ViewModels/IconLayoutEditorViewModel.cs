using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FModel.Creator.Layout;
using FModel.Framework;
using FModel.Settings;
using Newtonsoft.Json;
using Serilog;

namespace FModel.ViewModels;

public class IconLayoutEditorViewModel : ViewModel
{
    private IconLayoutSettings _settings;

    /// <summary>プレビューに使うアイテムデータ（現在開いているアセット、無ければサンプル）。</summary>
    public LayoutRenderContext Context { get; }

    public string ContextInfo { get; }

    /// <summary>画像タイプ(CosmeticStyle)が「オリジナル」かどうか。ONにするとこのレイアウトで生成される。</summary>
    public bool UseOriginal
    {
        get => UserSettings.Default.CosmeticStyle == EIconStyle.Original;
        set
        {
            UserSettings.Default.CosmeticStyle = value ? EIconStyle.Original : EIconStyle.Default;
            RaisePropertyChanged(nameof(UseOriginal));
        }
    }

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

    private BitmapSource _previewImage;
    public BitmapSource PreviewImage
    {
        get => _previewImage;
        private set => SetProperty(ref _previewImage, value);
    }

    /// <summary>再描画完了時に発火（ウインドウ側でハンドル位置を更新するため）。</summary>
    public event Action PreviewRendered;

    private WriteableBitmap _wb;
    private bool _renderQueued;

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
        QueueRender();
    }

    private void OnElementChanged(object sender, PropertyChangedEventArgs e) => QueueRender();

    /// <summary>連続変更（ドラッグ等）の再描画をまとめ、UIの応答性を保つ。</summary>
    public void QueueRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            _renderQueued = false;
            Render();
        }), DispatcherPriority.Background);
    }

    public void Render()
    {
        try
        {
            var bmp = IconLayoutRenderer.Render(CurrentTemplate, Context);
            if (bmp == null) return;

            using (bmp)
            {
                int w = bmp.Width, h = bmp.Height;
                if (_wb == null || _wb.PixelWidth != w || _wb.PixelHeight != h)
                {
                    _wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
                    PreviewImage = _wb;
                }

                _wb.Lock();
                _wb.WritePixels(new Int32Rect(0, 0, w, h), bmp.GetPixels(), bmp.RowBytes * h, bmp.RowBytes);
                _wb.AddDirtyRect(new Int32Rect(0, 0, w, h));
                _wb.Unlock();
            }

            PreviewRendered?.Invoke();
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

    /// <summary>現在のレイアウト設定をJSON文字列にして返す（書き出し用）。</summary>
    public string ExportJson()
        => JsonConvert.SerializeObject(_settings, Formatting.Indented, JsonNetSerializer.SerializerSettings);

    /// <summary>JSON文字列からレイアウト設定を読み込んで差し替える（読み込み用）。</summary>
    public bool ImportJson(string json)
    {
        var imported = JsonConvert.DeserializeObject<IconLayoutSettings>(json, JsonNetSerializer.SerializerSettings);
        if (imported == null) return false;

        UserSettings.Default.IconLayout = imported;
        _settings = imported;
        IconLayoutRenderer.InvalidateBackgroundCache();
        SetCurrentTemplate(_settings.Get(_selectedCategory));
        return true;
    }
}
