using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CUE4Parse.UE4.Assets.Exports;
using FModel.Creator;
using FModel.Creator.Layout;
using FModel.Framework;
using FModel.Settings;
using Newtonsoft.Json;
using Serilog;
using SkiaSharp;

namespace FModel.ViewModels;

public class IconLayoutEditorViewModel : ViewModel
{
    private IconLayoutSettings _settings;

    // プレビュー元アセット（画像タイプを変えて組み立て直すために保持）
    private readonly string _pkgName;
    private readonly string _exportType;
    private readonly Lazy<UObject> _object;

    private LayoutRenderContext _context;   // オリジナル(自作レイアウト)描画用のデータ
    private SKBitmap _builtinBitmap;         // オリジナル以外のときの「組み込みスタイル」描画結果

    public string ContextInfo { get; }

    public Array Categories => Enum.GetValues(typeof(EIconLayoutCategory));
    public Array BackgroundModes => Enum.GetValues(typeof(EIconLayoutBackground));
    public Array Aligns => Enum.GetValues(typeof(EIconLayoutAlign));
    public Array Styles => Enum.GetValues(typeof(EIconStyle));

    /// <summary>選択中の画像タイプが「オリジナル」か（=自作レイアウトを編集/反映できる状態か）。</summary>
    public bool IsOriginal => UserSettings.Default.CosmeticStyle == EIconStyle.Original;

    private EIconStyle _selectedStyle;
    /// <summary>プレビュー＆生成に使う画像タイプ。変更すると現在のアセットをそのタイプで組み立て直す。</summary>
    public EIconStyle SelectedStyle
    {
        get => _selectedStyle;
        set
        {
            if (_selectedStyle == value) return;
            _selectedStyle = value;
            UserSettings.Default.CosmeticStyle = value;
            RaisePropertyChanged(nameof(SelectedStyle));
            RaisePropertyChanged(nameof(IsOriginal));
            RebuildContext();
            Render();
        }
    }

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
        _pkgName = IconLayoutPreview.PackageName;
        _exportType = IconLayoutPreview.ExportType;
        _object = IconLayoutPreview.Object;
        _selectedStyle = UserSettings.Default.CosmeticStyle;

        var baseCtx = IconLayoutPreview.Current ?? LayoutRenderContext.Sample();
        _context = baseCtx;
        ContextInfo = string.IsNullOrWhiteSpace(baseCtx.DisplayName)
            ? "プレビュー対象: サンプル"
            : $"プレビュー対象: {baseCtx.DisplayName}";

        _selectedCategory = baseCtx.Category;
        RebuildContext();
        SetCurrentTemplate(_settings.Get(_selectedCategory));
    }

    /// <summary>選択中の画像タイプで、保持しているアセットからプレビュー用データを組み立て直す。</summary>
    private void RebuildContext()
    {
        var style = UserSettings.Default.CosmeticStyle;
        _builtinBitmap = null;

        if (_object != null && !string.IsNullOrEmpty(_exportType))
        {
            try
            {
                using var pkg = new CreatorPackage(_pkgName ?? string.Empty, _exportType, _object, style);
                if (pkg.TryConstructCreator(out var creator))
                {
                    creator.ParseForInfo();
                    _context = LayoutRenderContext.FromCreator(creator, _exportType);
                    if (style != EIconStyle.Original)
                    {
                        var bmps = creator.Draw();
                        if (bmps is { Length: > 0 }) _builtinBitmap = bmps[0];
                    }
                    return;
                }
            }
            catch (Exception e)
            {
                Log.Warning(e, "プレビュー用コンテキストの再構築に失敗しました");
            }
        }

        // 元アセットが無い/失敗時はキャプチャ済み or サンプルにフォールバック
        _context = IconLayoutPreview.Current ?? LayoutRenderContext.Sample();
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
            if (UserSettings.Default.CosmeticStyle == EIconStyle.Original)
            {
                using var bmp = IconLayoutRenderer.Render(CurrentTemplate, _context);
                if (bmp == null) return;
                WriteToWriteableBitmap(bmp);
            }
            else
            {
                // オリジナル以外は、そのタイプの組み込み描画結果をそのまま表示
                PreviewImage = _builtinBitmap != null ? Encode(_builtinBitmap) : null;
            }

            PreviewRendered?.Invoke();
        }
        catch (Exception e)
        {
            Log.Warning(e, "アイコンレイアウトのプレビュー描画に失敗しました");
        }
    }

    private void WriteToWriteableBitmap(SKBitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        if (_wb == null || _wb.PixelWidth != w || _wb.PixelHeight != h)
            _wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);

        _wb.Lock();
        _wb.WritePixels(new Int32Rect(0, 0, w, h), bmp.GetPixels(), bmp.RowBytes * h, bmp.RowBytes);
        _wb.AddDirtyRect(new Int32Rect(0, 0, w, h));
        _wb.Unlock();
        PreviewImage = _wb;
    }

    private static BitmapSource Encode(SKBitmap bmp)
    {
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
