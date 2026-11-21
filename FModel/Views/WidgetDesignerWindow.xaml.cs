using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AdonisUI.Controls;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Readers;
using FModel.Services;
using FModel.ViewModels.WidgetDesigner;
using FModel.Views.Resources.Controls;
using FModel.Views.Resources.Controls.Umg;
using Newtonsoft.Json;
using Serilog;

namespace FModel.Views;

/// <summary>
/// Interaction logic for WidgetDesignerWindow.xaml
/// Draws the widget tree of a widget blueprint (UMG, Editor Utility Widgets included) the way the UMG designer shows it:
/// hierarchy on the left, the laid out widgets in the middle, the selected widget's properties on the right.
/// </summary>
public partial class WidgetDesignerWindow : AdonisWindow
{
    private const double _MIN_ZOOM = 0.05;
    private const double _MAX_ZOOM = 10;
    private const double _FIT_MARGIN = 60;

    private static readonly (string Label, Size? Size)[] _presets =
    [
        ("1280 x 720", new Size(1280, 720)),
        ("1920 x 1080", new Size(1920, 1080)),
        ("2560 x 1440", new Size(2560, 1440)),
        ("3840 x 2160", new Size(3840, 2160)),
        ("1080 x 1920", new Size(1080, 1920)),
    ];

    private readonly UmgDocument _document;
    private readonly UmgElementFactory _factory = new();
    private readonly Dictionary<UmgWidget, UmgTreeItem> _items = new();
    private readonly ObservableCollection<UmgTreeItem> _roots = [];
    private readonly UmgOverlayElement _overlay;
    private UmgWidget _selected;
    private bool _syncingSelection;
    private bool _updatingPresets;

    private Point _mouseDown;
    private Point _panOrigin;
    private bool _mousePressed;
    private bool _panning;

    public WidgetDesignerWindow(UmgDocument document)
    {
        _document = document;
        InitializeComponent();

        Title = $"{TryFindResource("UI_WidgetDesigner") as string ?? "Widget Designer"} - {document.Name}";

        _overlay = new UmgOverlayElement(this);
        Overlay.Children.Add(_overlay);

        if (document.Root != null)
        {
            _roots.Add(CreateItem(document.Root, null));
            DesignHost.Child = _factory.Build(document.Root);
        }
        else
        {
            EmptyText.Visibility = Visibility.Visible;
        }

        HierarchyTree.ItemsSource = _roots;
        NoteText.Text = string.Join(" / ", document.Notes);
        SetupPresets();

        var status = new StringBuilder($"{document.WidgetCount} widgets");
        if (!string.IsNullOrEmpty(document.ParentClass)) status.Append($" - {document.ParentClass}");
        if (document.IsEditorUtility) status.Append(" - Editor Utility Widget");
        if (document.IsEditorData) status.Append(" - editor data");
        if (!string.IsNullOrEmpty(document.SizeMode)) status.Append($" - DesignSizeMode {document.SizeMode}");
        status.Append($" - {document.PackagePath}");
        Status.Text = status.ToString();

        ShowDetails(null);
        ContentRendered += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Fit);
        DesignHost.LayoutUpdated += OnDesignLayoutUpdated;
    }

    private int _layoutSignature;

    /// <summary>
    /// Redraws the overlay when the widgets moved. InvalidateVisual queues a layout pass of its own,
    /// so redrawing on every LayoutUpdated would loop forever.
    /// </summary>
    private void OnDesignLayoutUpdated(object sender, EventArgs e)
    {
        var hash = new HashCode();
        hash.Add(DesignHost.RenderSize);
        foreach (var element in _factory.Elements.Values)
        {
            if (TryGetBounds(element, DesignHost, out var bounds)) hash.Add(bounds);
        }

        var signature = hash.ToHashCode();
        if (signature == _layoutSignature) return;
        _layoutSignature = signature;
        _overlay.InvalidateVisual();
    }

    #region Loading

    /// <summary>Reads the widget blueprint of <paramref name="entry"/>, null when it holds none. Call off the UI thread.</summary>
    public static UmgDocument Load(GameFile entry, CancellationToken cancellationToken)
    {
        var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
        var package = provider.LoadPackage(entry);
        return UmgLoader.Load(provider, package, entry.Path, cancellationToken);
    }

    /// <summary>
    /// Reads a package saved on disk, outside of the game's archives (an editor saved Editor Utility Widget next to the game...).
    /// Its imports are resolved against the loaded game. Call off the UI thread.
    /// </summary>
    public static UmgDocument LoadFile(string filePath, CancellationToken cancellationToken)
    {
        var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
        var package = LoadLoosePackage(provider, filePath);
        return UmgLoader.Load(provider, package, filePath, cancellationToken);
    }

    /// <summary>Asks for a .uasset on disk and opens its widget blueprint.</summary>
    public static void OpenFromDisk()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Application.Current.TryFindResource("UI_WidgetDesigner") as string ?? "Widget Designer",
            Filter = "Unreal package (*.uasset)|*.uasset|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;

        var filePath = dialog.FileName;
        ApplicationService.ThreadWorkerView.Begin(cancellationToken =>
        {
            UmgDocument document;
            try
            {
                document = LoadFile(filePath, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.Error(e, "Failed to read {File}", filePath);
                FLogger.Append(ELog.Error, () => FLogger.Text($"{Path.GetFileName(filePath)} could not be read: {e.Message}", Constants.WHITE, true));
                return;
            }

            if (document == null)
            {
                FLogger.Append(ELog.Warning, () => FLogger.Text($"{Path.GetFileName(filePath)} is not a widget blueprint", Constants.WHITE, true));
                return;
            }

            Application.Current.Dispatcher.Invoke(() => new WidgetDesignerWindow(document).Show());
        });
    }

    public static IPackage LoadLoosePackage(IFileProvider provider, string filePath)
    {
        var name = PackageNameOf(filePath);
        var basePath = Path.ChangeExtension(filePath, null);

        FArchive Read(string extension)
        {
            var file = basePath + extension;
            return File.Exists(file) ? new FByteArchive($"{name}{extension}", File.ReadAllBytes(file), provider?.Versions) : null;
        }

        var uasset = new FByteArchive($"{name}{Path.GetExtension(filePath)}", File.ReadAllBytes(filePath), provider?.Versions);
        return new Package(uasset, Read(".uexp"), Read(".ubulk"), Read(".uptnl"), provider);
    }

    /// <summary>"…/FortniteGame/Content/UI/Foo.uasset" → "/Game/UI/Foo", "…/Plugins/Bar/Content/Foo.uasset" → "/Bar/Foo".</summary>
    private static string PackageNameOf(string filePath)
    {
        var path = Path.ChangeExtension(Path.GetFullPath(filePath), null).Replace('\\', '/');
        var index = path.LastIndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
        if (index < 0) return "/Game/" + Path.GetFileName(path);

        var before = path[..index];
        var root = before.Contains("/Plugins/", StringComparison.OrdinalIgnoreCase) ? "/" + before[(before.LastIndexOf('/') + 1)..] : "/Game";
        return root + path[(index + "/Content".Length)..];
    }

    #endregion

    #region Hierarchy

    private UmgTreeItem CreateItem(UmgWidget widget, UmgTreeItem parent)
    {
        var item = new UmgTreeItem(widget, parent) { IsExpanded = parent == null || !widget.IsInherited };
        _items[widget] = item;
        foreach (var child in widget.Children)
            item.Children.Add(CreateItem(child, item));
        return item;
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncingSelection) return;
        Select((e.NewValue as UmgTreeItem)?.Widget, fromTree: true);
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        var filter = FilterBox.Text.Trim();
        foreach (var root in _roots) Filter(root, filter);
    }

    private static bool Filter(UmgTreeItem item, string filter)
    {
        var any = false;
        foreach (var child in item.Children)
            any |= Filter(child, filter);

        var match = filter.Length == 0 ||
                    item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    item.ClassName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    item.Widget.Text?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;
        item.Visibility = match || any ? Visibility.Visible : Visibility.Collapsed;
        if (filter.Length > 0 && any) item.IsExpanded = true;
        return match || any;
    }

    #endregion

    #region Selection

    private void Select(UmgWidget widget, bool fromTree)
    {
        _selected = widget;
        if (!fromTree && widget != null && _items.TryGetValue(widget, out var item))
        {
            _syncingSelection = true;
            try
            {
                for (var parent = item.Parent; parent != null; parent = parent.Parent) parent.IsExpanded = true;
                item.IsSelected = true;
            }
            finally
            {
                _syncingSelection = false;
            }
        }

        ShowDetails(widget);
        _overlay.InvalidateVisual();
    }

    /// <summary>
    /// The widget drawn last under <paramref name="point"/> (DesignHost coordinates), like the UMG designer picks:
    /// the tree is walked in paint order (children after their parent, canvas children by ZOrder) and the last hit wins.
    /// </summary>
    private UmgWidget FindWidgetAt(Point point)
    {
        UmgWidget best = null;
        if (_document.Root != null) Visit(_document.Root);
        return best;

        void Visit(UmgWidget widget)
        {
            if (!_factory.Elements.TryGetValue(widget, out var element) || !element.IsVisible) return;
            if (TryGetBounds(element, DesignHost, out var bounds) && bounds.Contains(point)) best = widget;

            var children = widget.Kind == UmgKind.CanvasPanel
                ? widget.Children.OrderBy(c => c.Slot?.ZOrder ?? 0) // stable: same ZOrder keeps the slot order
                : (IEnumerable<UmgWidget>) widget.Children;
            foreach (var child in children) Visit(child);
        }
    }

    internal static bool TryGetBounds(FrameworkElement element, Visual ancestor, out Rect bounds)
    {
        bounds = Rect.Empty;
        if (!element.IsVisible || element.RenderSize is { Width: <= 0, Height: <= 0 }) return false;
        try
        {
            bounds = element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
            return true;
        }
        catch (InvalidOperationException)
        {
            return false; // not connected to the ancestor (yet)
        }
    }

    internal IEnumerable<(UmgWidget Widget, FrameworkElement Element)> Elements =>
        _factory.Elements.Select(kv => (kv.Key, kv.Value));

    internal UmgWidget Selected => _selected;
    internal FrameworkElement SelectedElement => _selected != null && _factory.Elements.TryGetValue(_selected, out var e) ? e : null;
    internal bool ShowOutlines => OutlinesCheck.IsChecked == true;
    internal Visual ViewportVisual => Viewport;

    #endregion

    #region Details

    private void ShowDetails(UmgWidget widget)
    {
        if (widget == null)
        {
            DetailName.Text = _document.Name;
            DetailClass.Text = _document.BlueprintClass ?? string.Empty;
            DetailSummary.Text = string.Join(Environment.NewLine, new[]
            {
                _document.ParentClass != null ? $"Parent: {_document.ParentClass}" : null,
                $"Widgets: {_document.WidgetCount}",
                _document.IsEditorUtility ? "Editor Utility Widget" : null,
            }.Where(s => s != null));
            DetailJson.Text = string.Empty;
            return;
        }

        DetailName.Text = widget.Name;
        DetailClass.Text = widget.Slot != null ? $"{widget.ClassName}  ·  {widget.Slot.ClassName}" : widget.ClassName;
        DetailSummary.Text = Summary(widget);

        var json = new StringBuilder();
        try
        {
            json.AppendLine(JsonConvert.SerializeObject(widget.Object, Formatting.Indented));
            if (widget.Slot?.Object != null)
            {
                json.AppendLine();
                json.AppendLine("// Slot");
                json.AppendLine(JsonConvert.SerializeObject(widget.Slot.Object, Formatting.Indented));
            }
        }
        catch (Exception e)
        {
            json.AppendLine($"// {e.Message}");
        }

        DetailJson.Text = json.ToString();
    }

    private string Summary(UmgWidget widget)
    {
        var lines = new List<string>();
        if (widget.Problem != null) lines.Add($"⚠ {widget.Problem}");
        if (widget.IsInherited && widget.SourcePackage != null) lines.Add($"From {widget.SourcePackage}");
        if (!string.IsNullOrEmpty(widget.Text)) lines.Add($"\"{widget.Text}\"");
        if (widget.TextOverriddenBy != null) lines.Add($"(text from {widget.TextOverriddenBy})");
        if (widget.Font is { } font) lines.Add($"Font: {font.Description} {font.Size.ToString("0.#", CultureInfo.InvariantCulture)}pt");
        if (widget.Foreground?.ResourceName is { } image) lines.Add($"Image: {image}");
        if (widget.Background?.ResourceName is { } background) lines.Add($"Background: {background}");
        if (widget.Visibility != Visibility.Visible) lines.Add($"Visibility: {widget.Visibility}");

        if (widget.Slot is { } slot)
        {
            lines.Add(slot.Kind switch
            {
                UmgSlotKind.Canvas => $"Anchors ({F(slot.AnchorMin.X)}, {F(slot.AnchorMin.Y)}) - ({F(slot.AnchorMax.X)}, {F(slot.AnchorMax.Y)})  " +
                                      $"Offsets {F(slot.Left)}, {F(slot.Top)}, {F(slot.Right)}, {F(slot.Bottom)}  " +
                                      $"Alignment ({F(slot.Alignment.X)}, {F(slot.Alignment.Y)}){(slot.AutoSize ? "  AutoSize" : string.Empty)}  Z {slot.ZOrder}",
                UmgSlotKind.Box => $"{(slot.Fill ? $"Fill {F(slot.FillValue)}" : "Auto")}  Padding {T(slot.Padding)}  {slot.HAlign}/{slot.VAlign}",
                UmgSlotKind.Grid or UmgSlotKind.UniformGrid => $"Row {slot.Row} Column {slot.Column}  Padding {T(slot.Padding)}  {slot.HAlign}/{slot.VAlign}",
                _ => $"Padding {T(slot.Padding)}  {slot.HAlign}/{slot.VAlign}"
            });
        }

        if (_factory.Elements.TryGetValue(widget, out var element) && TryGetBounds(element, DesignHost, out var bounds))
            lines.Add($"Position {F(bounds.X)}, {F(bounds.Y)}  Size {F(bounds.Width)} x {F(bounds.Height)}");

        return string.Join(Environment.NewLine, lines);
    }

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string T(Thickness t) => $"{F(t.Left)}, {F(t.Top)}, {F(t.Right)}, {F(t.Bottom)}";

    #endregion

    #region Design size

    private void SetupPresets()
    {
        _updatingPresets = true;
        try
        {
            SizePresets.Items.Clear();
            SizePresets.Items.Add(new ComboBoxItem { Content = TryFindResource("UI_WidgetDesigner_Desired") as string ?? "Desired", Tag = null });
            foreach (var (label, size) in _presets)
                SizePresets.Items.Add(new ComboBoxItem { Content = label, Tag = size });
            SizePresets.Items.Add(new ComboBoxItem { Content = "Custom", Tag = "Custom" });

            var initial = _document.DesignSize;
            ComboBoxItem selected;
            if (initial == null) selected = (ComboBoxItem) SizePresets.Items[0];
            else selected = SizePresets.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag is Size s && s == initial.Value) ??
                            (ComboBoxItem) SizePresets.Items[^1];
            SizePresets.SelectedItem = selected;
            ApplyDesignSize(initial);
        }
        finally
        {
            _updatingPresets = false;
        }
    }

    private void ApplyDesignSize(Size? size)
    {
        if (size is { } s)
        {
            DesignHost.Width = s.Width;
            DesignHost.Height = s.Height;
            WidthBox.Text = F(s.Width);
            HeightBox.Text = F(s.Height);
            WidthBox.IsEnabled = HeightBox.IsEnabled = true;
        }
        else
        {
            DesignHost.Width = DesignHost.Height = double.NaN;
            WidthBox.IsEnabled = HeightBox.IsEnabled = false;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (DesignHost.Width is double.NaN)
            {
                WidthBox.Text = F(DesignHost.ActualWidth);
                HeightBox.Text = F(DesignHost.ActualHeight);
            }

            SizeText.Text = $"{DesignHost.ActualWidth:N0} x {DesignHost.ActualHeight:N0}";
            _overlay.InvalidateVisual();
        });
    }

    private void OnSizePresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPresets || SizePresets.SelectedItem is not ComboBoxItem item) return;
        switch (item.Tag)
        {
            case Size size:
                ApplyDesignSize(size);
                break;
            case "Custom":
                ApplyDesignSize(TryReadSizeBoxes() ?? _document.DesignSize ?? new Size(1920, 1080));
                break;
            default:
                ApplyDesignSize(null);
                break;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Fit);
    }

    private Size? TryReadSizeBoxes()
    {
        if (double.TryParse(WidthBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) &&
            double.TryParse(HeightBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) &&
            w is > 0 and < 20000 && h is > 0 and < 20000)
            return new Size(w, h);
        return null;
    }

    private void CommitSizeBoxes()
    {
        if (TryReadSizeBoxes() is not { } size || DesignHost.Width is double.NaN) return;
        if (size.Width == DesignHost.Width && size.Height == DesignHost.Height) return;

        _updatingPresets = true;
        SizePresets.SelectedItem = SizePresets.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag is Size s && s == size) ?? SizePresets.Items[^1];
        _updatingPresets = false;
        ApplyDesignSize(size);
    }

    private void OnSizeBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitSizeBoxes();
        e.Handled = true;
    }

    private void OnSizeBoxLostFocus(object sender, RoutedEventArgs e) => CommitSizeBoxes();

    #endregion

    #region Zoom and pan

    private double Zoom => StageScale.ScaleX;

    private void SetZoom(double zoom, Point anchor)
    {
        zoom = Math.Clamp(zoom, _MIN_ZOOM, _MAX_ZOOM);
        var factor = zoom / Zoom;
        StageTranslate.X = anchor.X - (anchor.X - StageTranslate.X) * factor;
        StageTranslate.Y = anchor.Y - (anchor.Y - StageTranslate.Y) * factor;
        StageScale.ScaleX = StageScale.ScaleY = zoom;
        ZoomText.Text = $"{zoom * 100:0}%";
        _overlay.InvalidateVisual();
    }

    private void Fit()
    {
        var width = DesignFrame.ActualWidth;
        var height = DesignFrame.ActualHeight;
        if (width <= 0 || height <= 0 || Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) return;

        var zoom = Math.Clamp(Math.Min((Viewport.ActualWidth - _FIT_MARGIN * 2) / width, (Viewport.ActualHeight - _FIT_MARGIN * 2) / height), _MIN_ZOOM, 1);
        StageScale.ScaleX = StageScale.ScaleY = zoom;
        StageTranslate.X = (Viewport.ActualWidth - width * zoom) / 2;
        StageTranslate.Y = (Viewport.ActualHeight - height * zoom) / 2;
        ZoomText.Text = $"{zoom * 100:0}%";
        _overlay.InvalidateVisual();
    }

    private void OnFitClick(object sender, RoutedEventArgs e) => Fit();

    private void OnActualSizeClick(object sender, RoutedEventArgs e) =>
        SetZoom(1, new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));

    private void OnOutlinesChanged(object sender, RoutedEventArgs e) => _overlay?.InvalidateVisual();

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _overlay.Width = e.NewSize.Width;
        _overlay.Height = e.NewSize.Height;
        _overlay.InvalidateVisual();
    }

    private void OnViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        SetZoom(Zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), e.GetPosition(Viewport));
        e.Handled = true;
    }

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        Viewport.Focus();
        _mouseDown = e.GetPosition(Viewport);
        _panOrigin = new Point(StageTranslate.X, StageTranslate.Y);
        _mousePressed = true;
        _panning = e.ChangedButton is MouseButton.Middle or MouseButton.Right;
        Viewport.CaptureMouse();
        e.Handled = true;
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (!_mousePressed) return;
        if (e.LeftButton == MouseButtonState.Released && e.MiddleButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released)
        {
            // the button went up outside of the window (capture lost): stop dragging
            _mousePressed = _panning = false;
            Viewport.ReleaseMouseCapture();
            return;
        }

        var position = e.GetPosition(Viewport);
        var delta = position - _mouseDown;
        if (!_panning && delta.Length < 4) return;

        _panning = true;
        StageTranslate.X = _panOrigin.X + delta.X;
        StageTranslate.Y = _panOrigin.Y + delta.Y;
        _overlay.InvalidateVisual();
    }

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_mousePressed) return;
        _mousePressed = false;
        Viewport.ReleaseMouseCapture();

        if (!_panning && e.ChangedButton == MouseButton.Left)
        {
            var point = Viewport.TranslatePoint(e.GetPosition(Viewport), DesignHost);
            Select(FindWidgetAt(point), fromTree: false);
        }

        _panning = false;
    }

    #endregion
}

/// <summary>Draws the widget outlines and the selection over the designer, at a constant thickness whatever the zoom.</summary>
internal sealed class UmgOverlayElement : FrameworkElement
{
    private static readonly Pen _outlinePen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1) { DashStyle = new DashStyle([3, 3], 0) });
    private static readonly Pen _selectionPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00)), 2));
    private static readonly Brush _selectionFill = Freeze(new SolidColorBrush(Color.FromArgb(28, 0xFF, 0xA5, 0x00)));
    private static readonly Brush _labelBackground = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00)));
    private static readonly Typeface _typeface = new("Segoe UI");

    private readonly WidgetDesignerWindow _window;

    public UmgOverlayElement(WidgetDesignerWindow window)
    {
        _window = window;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var viewport = _window.ViewportVisual;
        if (_window.ShowOutlines)
        {
            foreach (var (_, element) in _window.Elements)
            {
                if (!WidgetDesignerWindow.TryGetBounds(element, viewport, out var bounds)) continue;
                dc.DrawRectangle(null, _outlinePen, Snap(bounds));
            }
        }

        if (_window.SelectedElement is { } selected && WidgetDesignerWindow.TryGetBounds(selected, viewport, out var rect))
        {
            rect = Snap(rect);
            dc.DrawRectangle(_selectionFill, _selectionPen, rect);

            var label = new FormattedText(_window.Selected.Name ?? string.Empty,System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                _typeface, 11, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var labelRect = new Rect(rect.Left - 1, Math.Max(0, rect.Top - label.Height - 3), label.Width + 8, label.Height + 2);
            dc.DrawRectangle(_labelBackground, null, labelRect);
            dc.DrawText(label, new Point(labelRect.Left + 4, labelRect.Top + 1));
        }
    }

    private static Rect Snap(Rect r) => new(Math.Round(r.X) + .5, Math.Round(r.Y) + .5, Math.Max(0, Math.Round(r.Width) - 1), Math.Max(0, Math.Round(r.Height) - 1));

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

/// <summary>A row of the hierarchy tree.</summary>
public sealed class UmgTreeItem : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;
    private Visibility _visibility = Visibility.Visible;

    public UmgTreeItem(UmgWidget widget, UmgTreeItem parent)
    {
        Widget = widget;
        Parent = parent;
        (KindGlyph, KindBrush) = Style(widget.Kind);
    }

    public UmgWidget Widget { get; }
    public UmgTreeItem Parent { get; }
    public ObservableCollection<UmgTreeItem> Children { get; } = [];

    public string Name => Widget.Name;
    public string ClassName => Widget.ClassName;
    public string KindGlyph { get; }
    public Brush KindBrush { get; }
    public FontStyle NameStyle => Widget.IsInherited ? FontStyles.Italic : FontStyles.Normal;
    public double NameOpacity => Widget.Visibility == Visibility.Visible ? 1 : 0.5;
    public string ToolTip => Widget.Problem ?? (Widget.IsInherited ? $"{Widget.ClassName} (from {Widget.SourcePackage})" : Widget.ClassName);

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public Visibility Visibility { get => _visibility; set => Set(ref _visibility, value); }

    private static readonly Dictionary<string, Brush> _brushes = new();

    private static (string, Brush) Style(UmgKind kind)
    {
        var (glyph, color) = kind switch
        {
            UmgKind.CanvasPanel or UmgKind.HorizontalBox or UmgKind.VerticalBox or UmgKind.Overlay or UmgKind.ScrollBox or UmgKind.WrapBox
                or UmgKind.GridPanel or UmgKind.UniformGridPanel or UmgKind.WidgetSwitcher => ("P", "#FF3D6FB6"),
            UmgKind.Border or UmgKind.SizeBox or UmgKind.ScaleBox or UmgKind.Content => ("C", "#FF4E7F9A"),
            UmgKind.Button => ("B", "#FFC27A1A"),
            UmgKind.Text => ("T", "#FF707070"),
            UmgKind.EditableText or UmgKind.ComboBox => ("E", "#FF6A6A9A"),
            UmgKind.Image => ("I", "#FF3E8E4F"),
            UmgKind.CheckBox => ("✓", "#FF7A5BA0"),
            UmgKind.ProgressBar or UmgKind.Slider => ("%", "#FF2B8AE2"),
            UmgKind.UserWidget => ("W", "#FF8E44AD"),
            UmgKind.Spacer => ("·", "#FF555555"),
            _ => ("?", "#FF8A5A2A")
        };

        if (!_brushes.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush((Color) ColorConverter.ConvertFromString(color));
            brush.Freeze();
            _brushes[color] = brush;
        }

        return (glyph, brush);
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
