using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;
using Serilog;

namespace FModel.ViewModels.WidgetDesigner;

/// <summary>
/// Reads the widget tree of a widget blueprint (UMG) into <see cref="UmgDocument"/>, the slots, brushes, texts and fonts
/// already resolved so the window only has to build WPF elements out of it.
/// Works on cooked packages (unversioned, needs mappings) as well as editor saved ones (Editor Utility Widgets...).
/// </summary>
public sealed partial class UmgLoader
{
    private const int _MAX_NESTING = 8;
    private const int _MAX_WIDGETS = 20000;

    private readonly IFileProvider _provider;
    private readonly CancellationToken _cancellationToken;
    private readonly UmgImageCache _images;
    private readonly UmgFontCache _fonts;
    private readonly Stack<string> _classStack = new();
    private readonly Dictionary<string, string[]> _lineageCache = new(StringComparer.OrdinalIgnoreCase);
    private UmgDocument _document;

    private UmgLoader(IFileProvider provider, CancellationToken cancellationToken)
    {
        _provider = provider;
        _cancellationToken = cancellationToken;
        _images = new UmgImageCache(cancellationToken);
        _fonts = new UmgFontCache(provider);
    }

    /// <summary>Whether the package holds a widget blueprint, without reading the widgets.</summary>
    public static bool IsWidgetBlueprint(IPackage package) => FindWidgetTree(package, out _, out _) != null;

    /// <summary>
    /// Reads the widget tree of <paramref name="package"/>, null when it holds no widget blueprint.
    /// Slow (textures, fonts and nested widget blueprints get loaded), call off the UI thread.
    /// </summary>
    public static UmgDocument Load(IFileProvider provider, IPackage package, string path, CancellationToken cancellationToken)
    {
        var tree = FindWidgetTree(package, out var generatedClass, out var treeOwner);
        if (tree == null) return null;

        var loader = new UmgLoader(provider, cancellationToken);
        return loader.Read(package, path, tree, generatedClass, treeOwner);
    }

    private UmgDocument Read(IPackage package, string path, UObject tree, UObject generatedClass, UObject treeOwner)
    {
        _document = new UmgDocument
        {
            Name = package.Name.SubstringAfterLast('/'),
            PackagePath = path,
            BlueprintClass = generatedClass?.Name,
            IsEditorData = package.GetExports().Any(e => e.ExportType is "EdGraph" or "WidgetBlueprint" or "EditorUtilityWidgetBlueprint"),
        };

        if (generatedClass is UStruct { SuperStruct: { IsNull: false } super })
            _document.ParentClass = super.Name;
        _document.IsEditorUtility = package.GetExports().Any(e => e.ExportType == "EditorUtilityWidgetBlueprint") ||
                                    SuperNames(generatedClass).Any(n => n.StartsWith("EditorUtilityWidget", StringComparison.Ordinal));

        if (treeOwner != null && generatedClass != null && treeOwner != generatedClass)
            _document.Notes.Add($"The widget tree is inherited from {treeOwner.Name}");

        if (generatedClass != null) _classStack.Push(generatedClass.GetPathName());

        var root = tree.GetOrDefault<UObject>("RootWidget");
        if (root != null)
        {
            _document.Root = Build(root, null, Colors.White, inherited: false, namedSlots: null, depth: 0);
        }
        else
        {
            _document.Notes.Add("The widget tree has no root widget");
        }

        ReadDesignSize(package, generatedClass);
        _document.WidgetCount = _document.Root?.Descendants().Count() ?? 0;
        return _document;
    }

    private void ReadDesignSize(IPackage package, UObject generatedClass)
    {
        // editor only (UUserWidget::DesignTimeSize), so only editor saved packages carry it
        var cdo = generatedClass is UStruct ? package.GetExports().FirstOrDefault(e => e.Name.StartsWith("Default__", StringComparison.Ordinal) && e.ExportType == generatedClass.Name) : null;
        var mode = cdo != null ? Enum(cdo, "DesignSizeMode") : null;
        _document.SizeMode = mode;
        if (cdo != null && cdo.TryGetValue(out FVector2D size, "DesignTimeSize") && mode is "Custom" or "CustomOnScreen")
        {
            _document.DesignSize = new Size(size.X, size.Y);
            return;
        }

        if (mode is "Desired" or "DesiredOnScreen")
        {
            _document.DesignSize = null;
            return;
        }

        // FillScreen (the default): a full screen layout when the root lays things out on a canvas, the desired size otherwise
        _document.DesignSize = mode == "FillScreen" || _document.Root?.Kind == UmgKind.CanvasPanel ? new Size(1920, 1080) : null;
    }

    /// <summary>
    /// The widget tree of the blueprint generated class (walking up to the parent blueprint when the class doesn't have one),
    /// falling back to any WidgetTree export, the editor's copy of an uncompiled blueprint included.
    /// </summary>
    private static UObject FindWidgetTree(IPackage package, out UObject generatedClass, out UObject treeOwner)
    {
        generatedClass = null;
        treeOwner = null;

        var exports = package.GetExports().ToList();
        generatedClass = exports.FirstOrDefault(e => e.ExportType.EndsWith("WidgetBlueprintGeneratedClass", StringComparison.Ordinal));
        if (generatedClass != null)
        {
            var tree = FindClassWidgetTree(generatedClass, out treeOwner);
            if (tree != null) return tree;
        }

        var any = exports.FirstOrDefault(e => e.ExportType == "WidgetTree");
        treeOwner = any?.Outer?.Load() as UObject;
        return any;
    }

    /// <summary>The names of the classes <paramref name="cls"/> derives from, blueprint ones then the native one they end on.</summary>
    private static IEnumerable<string> SuperNames(UObject cls)
    {
        for (var i = 0; cls is UStruct { SuperStruct: { IsNull: false } super } && i < 16; i++)
        {
            yield return super.Name;
            if (!TryLoadObject(super, out cls)) yield break;
        }
    }

    private static UObject FindClassWidgetTree(UObject cls, out UObject owner)
    {
        for (var i = 0; cls != null && i < 16; i++)
        {
            // a child blueprint that doesn't override the designer cooks an empty tree of its own: the parent's is the one drawn
            if (cls.GetOrDefault<UObject>("WidgetTree") is { } tree && tree.GetOrDefault<UObject>("RootWidget") != null)
            {
                owner = cls;
                return tree;
            }

            if (cls is not UStruct { SuperStruct: { IsNull: false } super }) break;
            if (!TryLoadObject(super, out var next) || !next.ExportType.EndsWith("BlueprintGeneratedClass", StringComparison.Ordinal)) break;
            cls = next;
        }

        owner = null;
        return null;
    }

    private UmgWidget Build(UObject obj, UObject slotObject, Color tint, bool inherited, Dictionary<string, UObject> namedSlots, int depth)
    {
        _cancellationToken.ThrowIfCancellationRequested();

        var widget = new UmgWidget
        {
            Name = obj.Name,
            ClassName = obj.ExportType,
            Object = obj,
            IsInherited = inherited,
            SourcePackage = obj.Owner?.Name,
        };

        var lineage = Lineage(obj);
        widget.Kind = Classify(obj, lineage);
        if (slotObject != null) widget.Slot = ReadSlot(slotObject);

        ReadCommon(widget, obj);
        var childTint = tint;
        ReadVisuals(widget, obj, lineage, tint, ref childTint);

        if (++_widgetCount > _MAX_WIDGETS) return widget;

        switch (widget.Kind)
        {
            case UmgKind.UserWidget:
                BuildUserWidget(widget, obj, childTint, depth);
                break;
            default:
            {
                // NamedSlot of a nested tree: shows what the outer widget put in it
                if (namedSlots != null && (lineage.Contains("NamedSlot") || widget.ClassName == "NamedSlot") && namedSlots.TryGetValue(obj.Name, out var content) && content != null)
                {
                    // the content belongs to the outer tree: the nested class isn't being read for it (it may hold more of the same widget)
                    var nested = _classStack.Count > 1 ? _classStack.Pop() : null;
                    try
                    {
                        var child = Build(content, content.GetOrDefault<UObject>("Slot"), childTint, inherited: false, namedSlots: null, Math.Max(0, depth - 1));
                        child.Parent = widget;
                        widget.Children.Add(child);
                    }
                    finally
                    {
                        if (nested != null) _classStack.Push(nested);
                    }

                    break;
                }

                foreach (var slot in obj.GetOrDefault<UObject[]>("Slots") ?? [])
                {
                    var content2 = slot?.GetOrDefault<UObject>("Content");
                    if (content2 == null) continue;
                    var child = Build(content2, slot, childTint, inherited, namedSlots, depth);
                    child.Parent = widget;
                    widget.Children.Add(child);
                }

                break;
            }
        }

        return widget;
    }

    private int _widgetCount;

    private void BuildUserWidget(UmgWidget widget, UObject obj, Color tint, int depth)
    {
        var cls = obj.Class?.Load() as UObject;
        if (cls == null)
        {
            widget.Problem = $"{widget.ClassName} could not be found";
            return;
        }

        var path = cls.GetPathName();
        if (depth >= _MAX_NESTING || _classStack.Contains(path))
        {
            widget.Problem = "Nested too deep";
            return;
        }

        var tree = FindClassWidgetTree(cls, out _);
        var root = tree?.GetOrDefault<UObject>("RootWidget");
        if (root == null)
        {
            widget.Problem = tree == null ? $"{widget.ClassName} has no widget tree" : $"{widget.ClassName} is empty";
            return;
        }

        var namedSlots = new Dictionary<string, UObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in obj.GetOrDefault<FStructFallback[]>("NamedSlotBindings") ?? [])
        {
            var name = binding.GetOrDefault<FName>("Name").Text;
            if (!string.IsNullOrEmpty(name)) namedSlots[name] = binding.GetOrDefault<UObject>("Content");
        }

        _classStack.Push(path);
        try
        {
            var child = Build(root, null, tint, inherited: true, namedSlots, depth + 1);
            child.Parent = widget;
            widget.Children.Add(child);
            ApplyTextOverride(widget, obj, child);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Failed to read the widget tree of {Class}", path);
            widget.Problem = $"{widget.ClassName} could not be read: {e.Message}";
        }
        finally
        {
            _classStack.Pop();
        }
    }

    /// <summary>
    /// Buttons and labels usually expose a text variable ("ButtonText"...) their PreConstruct copies into the inner text block,
    /// which the designer shows. That graph isn't run here: when the instance sets exactly one text and its tree has exactly one
    /// text block of its own, the text goes there.
    /// </summary>
    private static void ApplyTextOverride(UmgWidget widget, UObject obj, UmgWidget root)
    {
        string text = null;
        var count = 0;
        foreach (var property in obj.Properties)
        {
            if (property.Tag?.GenericValue is not FText value || property.Name.Text is "ToolTipText") continue;
            text = value.Text;
            count++;
        }

        if (count != 1 || string.IsNullOrEmpty(text)) return;

        var targets = OwnTexts(root).Take(2).ToList();
        if (targets.Count != 1) return;

        var target = targets[0];
        target.Text = target.TransformPolicy switch
        {
            "ToUpper" => text.ToUpperInvariant(),
            "ToLower" => text.ToLowerInvariant(),
            _ => text
        };
        target.TextOverriddenBy = widget.Name;
    }

    /// <summary>The text blocks of a tree, those of the user widgets nested in it excluded.</summary>
    private static IEnumerable<UmgWidget> OwnTexts(UmgWidget widget)
    {
        if (widget.Kind == UmgKind.Text) yield return widget;
        if (widget.Kind == UmgKind.UserWidget) yield break;
        foreach (var child in widget.Children)
        foreach (var text in OwnTexts(child))
            yield return text;
    }

    #region Classification

    private static readonly (string Class, UmgKind Kind)[] _nativeKinds =
    [
        ("CanvasPanel", UmgKind.CanvasPanel),
        ("ConstraintCanvas", UmgKind.CanvasPanel),
        ("HorizontalBox", UmgKind.HorizontalBox),
        ("VerticalBox", UmgKind.VerticalBox),
        ("StackBox", UmgKind.VerticalBox),
        ("Overlay", UmgKind.Overlay),
        ("Border", UmgKind.Border),
        ("Button", UmgKind.Button),
        ("SizeBox", UmgKind.SizeBox),
        ("ScaleBox", UmgKind.ScaleBox),
        ("ScrollBox", UmgKind.ScrollBox),
        ("WrapBox", UmgKind.WrapBox),
        ("GridPanel", UmgKind.GridPanel),
        ("UniformGridPanel", UmgKind.UniformGridPanel),
        ("WidgetSwitcher", UmgKind.WidgetSwitcher),
        ("RichTextBlock", UmgKind.Text),
        ("TextBlock", UmgKind.Text),
        ("TextLayoutWidget", UmgKind.Text),
        ("Image", UmgKind.Image),
        ("MultiLineEditableTextBox", UmgKind.EditableText),
        ("MultiLineEditableText", UmgKind.EditableText),
        ("EditableTextBox", UmgKind.EditableText),
        ("EditableText", UmgKind.EditableText),
        ("SpinBox", UmgKind.EditableText),
        ("CheckBox", UmgKind.CheckBox),
        ("ProgressBar", UmgKind.ProgressBar),
        ("RadialSlider", UmgKind.Slider),
        ("Slider", UmgKind.Slider),
        ("ComboBoxString", UmgKind.ComboBox),
        ("ComboBoxKey", UmgKind.ComboBox),
        ("ComboBox", UmgKind.ComboBox),
        ("Spacer", UmgKind.Spacer),
        ("CircularThrobber", UmgKind.Throbber),
        ("Throbber", UmgKind.Throbber),
        ("NamedSlot", UmgKind.Content),
        ("SafeZone", UmgKind.Content),
        ("InvalidationBox", UmgKind.Content),
        ("RetainerBox", UmgKind.Content),
        ("BackgroundBlur", UmgKind.Border),
        ("MenuAnchor", UmgKind.Content),
        ("ContentWidget", UmgKind.Content),
        ("UserWidget", UmgKind.UserWidget),
    ];

    private UmgKind Classify(UObject obj, string[] lineage)
    {
        // an instance of a widget blueprint draws the tree of that blueprint
        if (obj.Class?.Object?.Value is { } cls && cls.ExportType.EndsWith("BlueprintGeneratedClass", StringComparison.Ordinal) ||
            obj.ExportType.EndsWith("_C", StringComparison.Ordinal))
        {
            // ...unless it's a blueprint of a plain widget (a styled CommonTextBlock_C...)
            var native = lineage.FirstOrDefault(n => !n.EndsWith("_C", StringComparison.Ordinal));
            var kind = native != null ? ClassifyNative([native]) : UmgKind.UserWidget;
            return kind is UmgKind.Unknown or UmgKind.Content or UmgKind.Button ? UmgKind.UserWidget : kind;
        }

        return ClassifyNative(lineage);
    }

    private static UmgKind ClassifyNative(IEnumerable<string> lineage)
    {
        foreach (var name in lineage)
        {
            foreach (var (cls, kind) in _nativeKinds)
                if (name.Equals(cls, StringComparison.Ordinal)) return kind;
        }

        // no mappings for the class (editor only widgets aren't in the game's usmap): EditorUtilityCheckBox, CommonTextBlock...
        foreach (var name in lineage)
        {
            foreach (var (cls, kind) in _nativeKinds)
                if (name.EndsWith(cls, StringComparison.Ordinal)) return kind;
        }

        return UmgKind.Unknown;
    }

    /// <summary>The class names of <paramref name="obj"/>, most derived first: blueprint classes, then the native ones from the mappings.</summary>
    private string[] Lineage(UObject obj)
    {
        if (obj == null) return [];
        var name = obj.ExportType;
        if (_lineageCache.TryGetValue(name, out var cached)) return cached;

        var result = new List<string>();
        UObject cls = null;
        try { cls = obj.Class?.Load() as UObject; }
        catch (Exception e) { Log.Debug(e, "Failed to load the class of {Name}", obj.Name); }

        // blueprint classes
        for (var i = 0; cls != null && i < 16; i++)
        {
            result.Add(cls.Name);
            if (cls is not UStruct { SuperStruct: { IsNull: false } super }) { cls = null; break; }
            if (!TryLoadObject(super, out var next))
            {
                result.Add(super.Name);
                cls = null;
                break;
            }

            cls = next;
        }

        if (result.Count == 0) result.Add(name);

        // native classes
        var types = _provider.MappingsForGame?.Types;
        var last = result[^1];
        for (var i = 0; types != null && i < 32 && types.TryGetValue(last, out var type) && !string.IsNullOrEmpty(type.SuperType); i++)
        {
            last = type.SuperType;
            result.Add(last);
        }

        var array = result.Distinct().ToArray();
        _lineageCache[name] = array;
        return array;
    }

    #endregion

    #region Slots

    private UmgSlot ReadSlot(UObject slot)
    {
        var lineage = Lineage(slot);
        var cls = lineage.FirstOrDefault(n => n.EndsWith("Slot", StringComparison.Ordinal)) ?? slot.ExportType;
        var result = new UmgSlot { ClassName = slot.ExportType, Object = slot };

        bool Is(string name) => lineage.Contains(name) || cls.EndsWith(name, StringComparison.Ordinal);

        if (Is("CanvasPanelSlot"))
        {
            result.Kind = UmgSlotKind.Canvas;
            var layout = slot.GetOrDefault<FStructFallback>("LayoutData");
            if (layout?.GetOrDefault<FStructFallback>("Offsets") is { } offsets)
            {
                result.Left = Number(offsets, "Left", 0);
                result.Top = Number(offsets, "Top", 0);
                result.Right = Number(offsets, "Right", 100);
                result.Bottom = Number(offsets, "Bottom", 30);
            }

            if (layout?.GetOrDefault<FStructFallback>("Anchors") is { } anchors)
            {
                result.AnchorMin = Vector(anchors, "Minimum", default);
                result.AnchorMax = Vector(anchors, "Maximum", default);
            }

            if (layout != null) result.Alignment = Vector(layout, "Alignment", default);
            result.AutoSize = Bool(slot, "bAutoSize", false);
            result.ZOrder = (int) Number(slot, "ZOrder", 0);
            return result;
        }

        // the alignment a slot class gets when its properties are left alone
        (HorizontalAlignment H, VerticalAlignment V, Thickness Padding) defaults = cls switch
        {
            _ when Is("OverlaySlot") => (HorizontalAlignment.Left, VerticalAlignment.Top, default),
            _ when Is("ButtonSlot") => (HorizontalAlignment.Center, VerticalAlignment.Center, new Thickness(4, 2, 4, 2)),
            _ when Is("BorderSlot") => (HorizontalAlignment.Stretch, VerticalAlignment.Stretch, new Thickness(4, 2, 4, 2)),
            _ when Is("ScaleBoxSlot") => (HorizontalAlignment.Center, VerticalAlignment.Center, default),
            _ when Is("WrapBoxSlot") || Is("UniformGridSlot") => (HorizontalAlignment.Left, VerticalAlignment.Top, default),
            _ => (HorizontalAlignment.Stretch, VerticalAlignment.Stretch, default)
        };

        result.Kind = cls switch
        {
            _ when Is("HorizontalBoxSlot") || Is("VerticalBoxSlot") || Is("StackBoxSlot") => UmgSlotKind.Box,
            _ when Is("OverlaySlot") => UmgSlotKind.Overlay,
            _ when Is("UniformGridSlot") => UmgSlotKind.UniformGrid,
            _ when Is("GridSlot") => UmgSlotKind.Grid,
            _ => UmgSlotKind.Plain
        };

        result.Padding = Margin(slot, "Padding", defaults.Padding);
        result.HAlign = HAlign(Enum(slot, "HorizontalAlignment"), defaults.H);
        result.VAlign = VAlign(Enum(slot, "VerticalAlignment"), defaults.V);

        if (result.Kind == UmgSlotKind.Box)
        {
            // box slots default to Automatic, FSlateChildSize to Fill
            var size = slot.GetOrDefault<FStructFallback>("Size");
            result.Fill = size != null && Enum(size, "SizeRule") is "Fill";
            result.FillValue = size != null ? Number(size, "Value", 1) : 1;
            result.MaxSize = Number(slot, "MaxSize", 0);
        }

        if (result.Kind is UmgSlotKind.Grid or UmgSlotKind.UniformGrid)
        {
            result.Row = (int) Number(slot, "Row", 0);
            result.Column = (int) Number(slot, "Column", 0);
            result.RowSpan = Math.Max(1, (int) Number(slot, "RowSpan", 1));
            result.ColumnSpan = Math.Max(1, (int) Number(slot, "ColumnSpan", 1));
            result.Layer = (int) Number(slot, "Layer", 0);
            var nudge = Vector(slot, "Nudge", default);
            result.Nudge = new Vector(nudge.X, nudge.Y);
        }

        return result;
    }

    private static HorizontalAlignment HAlign(string value, HorizontalAlignment fallback) => value switch
    {
        "HAlign_Left" => HorizontalAlignment.Left,
        "HAlign_Center" => HorizontalAlignment.Center,
        "HAlign_Right" => HorizontalAlignment.Right,
        "HAlign_Fill" => HorizontalAlignment.Stretch,
        _ => fallback
    };

    private static VerticalAlignment VAlign(string value, VerticalAlignment fallback) => value switch
    {
        "VAlign_Top" => VerticalAlignment.Top,
        "VAlign_Center" => VerticalAlignment.Center,
        "VAlign_Bottom" => VerticalAlignment.Bottom,
        "VAlign_Fill" => VerticalAlignment.Stretch,
        _ => fallback
    };

    #endregion

    #region Widgets

    private void ReadCommon(UmgWidget widget, UObject obj)
    {
        widget.Visibility = Enum(obj, "Visibility") switch
        {
            "Collapsed" => Visibility.Collapsed,
            "Hidden" => Visibility.Hidden,
            _ => Visibility.Visible
        };
        widget.RenderOpacity = Math.Clamp(Number(obj, "RenderOpacity", 1), 0, 1);

        if (obj.GetOrDefault<FStructFallback>("RenderTransform") is { } transform)
        {
            var translation = Vector(transform, "Translation", default);
            var scale = Vector(transform, "Scale", new Point(1, 1));
            var shear = Vector(transform, "Shear", default);
            widget.Translation = new Vector(translation.X, translation.Y);
            widget.Scale = new Vector(scale.X, scale.Y);
            widget.Shear = new Vector(shear.X, shear.Y);
            widget.Angle = Number(transform, "Angle", 0);
        }

        widget.Pivot = Vector(obj, "RenderTransformPivot", new Point(0.5, 0.5));
    }

    private void ReadVisuals(UmgWidget widget, UObject obj, string[] lineage, Color tint, ref Color childTint)
    {
        switch (widget.Kind)
        {
            case UmgKind.Border:
            {
                var brushColor = LinearColor(obj, "BrushColor", Colors.White);
                widget.Background = ReadBrush(obj.GetOrDefault<FStructFallback>("Background"), Multiply(tint, brushColor), new UmgBrush());
                if (lineage.Contains("BackgroundBlur") || widget.ClassName.Contains("BackgroundBlur"))
                    widget.Background = new UmgBrush { Tint = Color.FromArgb(48, 255, 255, 255) };
                widget.Padding = Margin(obj, "Padding", new Thickness(4, 2, 4, 2));
                widget.ContentHAlign = HAlign(Enum(obj, "HorizontalAlignment"), HorizontalAlignment.Stretch);
                widget.ContentVAlign = VAlign(Enum(obj, "VerticalAlignment"), VerticalAlignment.Stretch);
                childTint = Multiply(tint, LinearColor(obj, "ContentColorAndOpacity", Colors.White));
                break;
            }
            case UmgKind.Button:
            {
                var style = obj.GetOrDefault<FStructFallback>("WidgetStyle");
                var background = LinearColor(obj, "BackgroundColor", Colors.White);
                widget.Background = ReadBrush(style?.GetOrDefault<FStructFallback>("Normal"), Multiply(tint, background), DefaultButtonBrush());
                widget.Padding = style != null ? Margin(style, "NormalPadding", new Thickness(12, 1.5, 12, 1.5)) : new Thickness(12, 1.5, 12, 1.5);
                childTint = Multiply(tint, LinearColor(obj, "ColorAndOpacity", Colors.White));
                break;
            }
            case UmgKind.Image:
            {
                var color = LinearColor(obj, "ColorAndOpacity", Colors.White);
                widget.Foreground = ReadBrush(obj.GetOrDefault<FStructFallback>("Brush"), Multiply(tint, color), new UmgBrush());
                break;
            }
            case UmgKind.Text:
                ReadText(widget, obj, lineage, tint);
                break;
            case UmgKind.EditableText:
                ReadEditableText(widget, obj, lineage, tint);
                break;
            case UmgKind.CheckBox:
            {
                var style = obj.GetOrDefault<FStructFallback>("WidgetStyle");
                widget.IsChecked = Enum(obj, "CheckedState") is "Checked" or "Undetermined";
                var toggle = style != null && Enum(style, "CheckBoxType") is "ToggleButton";
                widget.Horizontal = !toggle; // a plain check box draws the box left of its content
                var imageName = widget.IsChecked ? "CheckedImage" : "UncheckedImage";
                // a brush the designer touched starts over from FSlateBrush's defaults: white, 32x32, an image (RoundedBox → half height radius)
                var fallback = style?.GetOrDefault<FStructFallback>(imageName) != null
                    ? new UmgBrush { Tint = Colors.White, ImageSize = toggle ? new Size(32, 32) : new Size(16, 16) }
                    : toggle
                    ? new UmgBrush { Tint = Color.FromRgb(0x3A, 0x3A, 0x3A), DrawAs = UmgDrawAs.RoundedBox, CornerRadius = new CornerRadius(4) }
                    : new UmgBrush { Tint = Colors.White, DrawAs = UmgDrawAs.Border, ImageSize = new Size(16, 16), OutlineColor = Color.FromRgb(0x80, 0x80, 0x80), OutlineWidth = 1 };
                widget.Background = ReadBrush(style?.GetOrDefault<FStructFallback>(imageName), tint, fallback);
                widget.Padding = style != null ? Margin(style, "Padding", new Thickness(2)) : new Thickness(2);
                break;
            }
            case UmgKind.ProgressBar:
            {
                var style = obj.GetOrDefault<FStructFallback>("WidgetStyle");
                widget.Percent = Math.Clamp(Number(obj, "Percent", 0), 0, 1);
                var fillType = Enum(obj, "BarFillType") ?? string.Empty;
                widget.Horizontal = !fillType.Contains("Top", StringComparison.Ordinal);
                widget.Background = ReadBrush(style?.GetOrDefault<FStructFallback>("BackgroundImage"), tint,
                    new UmgBrush { Tint = Color.FromRgb(0x2F, 0x2F, 0x2F), ImageSize = new Size(32, 14) });
                var fillColor = LinearColor(obj, "FillColorAndOpacity", Colors.White);
                widget.Foreground = ReadBrush(style?.GetOrDefault<FStructFallback>("FillImage"), Multiply(tint, fillColor),
                    new UmgBrush { Tint = Color.FromRgb(0x2B, 0x8A, 0xE2) });
                break;
            }
            case UmgKind.Slider:
            {
                widget.Percent = Math.Clamp(Number(obj, "Value", 0), 0, 1);
                widget.Horizontal = Enum(obj, "Orientation") is not "Orient_Vertical";
                widget.Background = new UmgBrush { Tint = Multiply(tint, LinearColor(obj, "SliderBarColor", Color.FromRgb(0x60, 0x60, 0x60))) };
                widget.Foreground = new UmgBrush { Tint = Multiply(tint, LinearColor(obj, "SliderHandleColor", Colors.White)), DrawAs = UmgDrawAs.RoundedBox, HalfHeightRadius = true };
                break;
            }
            case UmgKind.ComboBox:
            {
                widget.Text = obj.GetOrDefault<string>("SelectedOption") ?? (obj.GetOrDefault<string[]>("DefaultOptions") ?? []).FirstOrDefault() ?? string.Empty;
                widget.Font = ReadFont(obj.GetOrDefault<FStructFallback>("Font"), 10);
                widget.TextColor = Multiply(tint, SlateColor(obj, "ForegroundColor", Colors.White));
                widget.Background = new UmgBrush { Tint = Multiply(tint, Color.FromRgb(0x24, 0x24, 0x24)), DrawAs = UmgDrawAs.RoundedBox, CornerRadius = new CornerRadius(4), OutlineColor = Color.FromRgb(0x48, 0x48, 0x48), OutlineWidth = 1 };
                widget.Padding = new Thickness(8, 3, 22, 3);
                break;
            }
            case UmgKind.SizeBox:
                widget.WidthOverride = Override(obj, "WidthOverride");
                widget.HeightOverride = Override(obj, "HeightOverride");
                widget.MinWidth = Override(obj, "MinDesiredWidth");
                widget.MinHeight = Override(obj, "MinDesiredHeight");
                widget.MaxWidth = Override(obj, "MaxDesiredWidth");
                widget.MaxHeight = Override(obj, "MaxDesiredHeight");
                widget.MinAspect = Override(obj, "MinAspectRatio");
                widget.MaxAspect = Override(obj, "MaxAspectRatio");
                break;
            case UmgKind.ScaleBox:
                widget.Stretch = Enum(obj, "Stretch") ?? "ScaleToFit";
                widget.UserScale = Number(obj, "UserSpecifiedScale", 1);
                break;
            case UmgKind.ScrollBox:
                widget.Horizontal = Enum(obj, "Orientation") is "Orient_Horizontal";
                break;
            case UmgKind.VerticalBox when lineage.Contains("StackBox") || widget.ClassName.EndsWith("StackBox", StringComparison.Ordinal):
                if (Enum(obj, "Orientation") is "Orient_Horizontal") widget.Kind = UmgKind.HorizontalBox;
                break;
            case UmgKind.WrapBox:
            {
                widget.Horizontal = Enum(obj, "Orientation") is not "Orient_Vertical";
                var inner = Vector(obj, "InnerSlotPadding", default);
                widget.InnerSlotPadding = widget.Horizontal ? inner.X : inner.Y;
                widget.Padding = new Thickness(0, 0, 0, widget.Horizontal ? inner.Y : inner.X); // between lines
                break;
            }
            case UmgKind.GridPanel:
                widget.ColumnFill = Numbers(obj, "ColumnFill");
                widget.RowFill = Numbers(obj, "RowFill");
                break;
            case UmgKind.UniformGridPanel:
                widget.SlotPadding = Margin(obj, "SlotPadding", default);
                widget.MinSlotWidth = Number(obj, "MinDesiredSlotWidth", 0);
                widget.MinSlotHeight = Number(obj, "MinDesiredSlotHeight", 0);
                break;
            case UmgKind.WidgetSwitcher:
                widget.ActiveIndex = (int) Number(obj, "ActiveWidgetIndex", 0);
                break;
            case UmgKind.Spacer:
            {
                var size = Vector(obj, "Size", new Point(1, 1));
                widget.WidthOverride = size.X;
                widget.HeightOverride = size.Y;
                break;
            }
            case UmgKind.UserWidget:
                widget.Padding = Margin(obj, "Padding", default);
                widget.ColorAndOpacity = LinearColor(obj, "ColorAndOpacity", Colors.White);
                childTint = Multiply(tint, widget.ColorAndOpacity);
                break;
        }
    }

    private static UmgBrush DefaultButtonBrush() => new()
    {
        Tint = Color.FromRgb(0xD8, 0xD8, 0xD8),
        DrawAs = UmgDrawAs.RoundedBox,
        CornerRadius = new CornerRadius(4),
        OutlineColor = Color.FromRgb(0xA0, 0xA0, 0xA0),
        OutlineWidth = 1
    };

    private static double? Override(UObject obj, string name)
    {
        // WidthOverride is only used with its bOverride_WidthOverride flag
        if (!Bool(obj, "bOverride_" + name, false)) return null;
        return Number(obj, name, 0);
    }

    private void ReadText(UmgWidget widget, UObject obj, string[] lineage, Color tint)
    {
        var text = obj.GetOrDefault<FText>("Text")?.Text ?? string.Empty;
        var isRich = lineage.Contains("RichTextBlock") || widget.ClassName.EndsWith("RichTextBlock", StringComparison.Ordinal);

        widget.Font = ReadFont(obj.GetOrDefault<FStructFallback>("Font"), 24);
        var color = SlateColor(obj, "ColorAndOpacity", Colors.White);
        widget.Justification = Justification(Enum(obj, "Justification"));
        widget.AutoWrap = Bool(obj, "AutoWrapText", false);
        widget.TransformPolicy = Enum(obj, "TextTransformPolicy");
        ReadShadow(widget, obj, tint);

        if (isRich)
        {
            // markup like <Bold>text</> is drawn as plain text in the default style of the style table
            text = RichTextMarkup().Replace(text, string.Empty);
            if (obj.GetOrDefault<UObject>("TextStyleSet") is UDataTable table &&
                table.RowMap.FirstOrDefault(r => r.Key.Text == "Default").Value is { } row &&
                row.GetOrDefault<FStructFallback>("TextStyle") is { } style)
            {
                widget.Font = ReadFont(style.GetOrDefault<FStructFallback>("Font"), 24);
                color = SlateColor(style, "ColorAndOpacity", color);
            }
        }

        // CommonUI: the style class wins over the text block's own font and color
        if (obj.GetOrDefault<UObject>("Style") is { } styleClass && ClassDefaults(styleClass) is { } styleDefaults)
        {
            if (styleDefaults.GetOrDefault<FStructFallback>("Font") is { } font) widget.Font = ReadFont(font, widget.Font.Size);
            if (styleDefaults.TryGetValue(out FLinearColor styleColor, "Color")) color = ToColor(styleColor);
            if (Bool(styleDefaults, "bUsesDropShadow", false))
            {
                widget.ShadowColor = Multiply(tint, LinearColor(styleDefaults, "ShadowColor", Colors.Black));
                var offset = Vector(styleDefaults, "ShadowOffset", new Point(1, 1));
                widget.ShadowOffset = new Vector(offset.X, offset.Y);
            }
        }

        widget.TextColor = Multiply(tint, color);
        widget.Text = widget.TransformPolicy switch
        {
            "ToUpper" => text.ToUpperInvariant(),
            "ToLower" => text.ToLowerInvariant(),
            _ => text
        };
    }

    private void ReadEditableText(UmgWidget widget, UObject obj, string[] lineage, Color tint)
    {
        var isBox = lineage.Any(n => n.EndsWith("TextBox", StringComparison.Ordinal)) || widget.ClassName.EndsWith("TextBox", StringComparison.Ordinal) ||
                    lineage.Contains("SpinBox");
        widget.MultiLine = lineage.Any(n => n.StartsWith("MultiLine", StringComparison.Ordinal)) || widget.ClassName.Contains("MultiLine");
        widget.Text = obj.GetOrDefault<FText>("Text")?.Text ?? (lineage.Contains("SpinBox") ? Number(obj, "Value", 0).ToString("0.##") : string.Empty);
        widget.HintText = obj.GetOrDefault<FText>("HintText")?.Text;
        widget.Justification = Justification(Enum(obj, "Justification"));
        widget.AutoWrap = widget.MultiLine;

        var style = obj.GetOrDefault<FStructFallback>("WidgetStyle");
        var textStyle = style?.GetOrDefault<FStructFallback>("TextStyle") ?? obj.GetOrDefault<FStructFallback>("TextStyle");
        var font = style?.GetOrDefault<FStructFallback>("Font") ?? textStyle?.GetOrDefault<FStructFallback>("Font") ?? obj.GetOrDefault<FStructFallback>("Font");
        widget.Font = ReadFont(font, 9);

        if (isBox)
        {
            widget.Background = ReadBrush(style?.GetOrDefault<FStructFallback>("BackgroundImageNormal"), tint,
                new UmgBrush { Tint = Color.FromRgb(0xF2, 0xF2, 0xF2), DrawAs = UmgDrawAs.RoundedBox, CornerRadius = new CornerRadius(2), OutlineColor = Color.FromRgb(0x9A, 0x9A, 0x9A), OutlineWidth = 1 });
            widget.Padding = style != null ? Margin(style, "Padding", new Thickness(4, 2, 4, 2)) : new Thickness(4, 2, 4, 2);
            var foreground = style != null ? SlateColor(style, "ForegroundColor", Color.FromRgb(0x10, 0x10, 0x10)) : Color.FromRgb(0x10, 0x10, 0x10);
            widget.TextColor = Multiply(tint, foreground);
        }
        else
        {
            widget.TextColor = Multiply(tint, textStyle != null ? SlateColor(textStyle, "ColorAndOpacity", Colors.White) : Colors.White);
        }
    }

    private void ReadShadow(UmgWidget widget, IPropertyHolder obj, Color tint)
    {
        var shadow = LinearColor(obj, "ShadowColorAndOpacity", Colors.Transparent);
        if (shadow.A == 0) return;
        widget.ShadowColor = Multiply(tint, shadow);
        var offset = Vector(obj, "ShadowOffset", new Point(1, 1));
        widget.ShadowOffset = new Vector(offset.X, offset.Y);
    }

    private static TextAlignment Justification(string value) => value switch
    {
        "Center" => TextAlignment.Center,
        "Right" => TextAlignment.Right,
        _ => TextAlignment.Left
    };

    [GeneratedRegex(@"<[^<>]*>")]
    private static partial Regex RichTextMarkup();

    private UmgFont ReadFont(FStructFallback font, double defaultSize)
    {
        var result = new UmgFont { Size = defaultSize };
        if (font == null)
        {
            result.Family = UmgFontCache.DefaultFamily;
            return result;
        }

        result.Size = Number(font, "Size", defaultSize);
        result.LetterSpacing = Number(font, "LetterSpacing", 0);
        var typeface = font.GetOrDefault<FName>("TypefaceFontName").Text;
        var fontObject = font.GetOrDefault<UObject>("FontObject");
        _fonts.Apply(result, fontObject, typeface);
        return result;
    }

    private UObject ClassDefaults(UObject cls)
    {
        try
        {
            if (cls is UStruct { } s && s.Owner != null)
                return s.Owner.GetExports().FirstOrDefault(e => e.Name == "Default__" + s.Name);
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Debug(e, "Failed to read the defaults of {Class}", cls.Name);
            return null;
        }
    }

    #endregion

    #region Brushes

    /// <summary>
    /// <paramref name="brush"/> holds only what differs from the property's default, so it's read over <paramref name="fallback"/>.
    /// </summary>
    private UmgBrush ReadBrush(FStructFallback brush, Color tint, UmgBrush fallback)
    {
        var result = new UmgBrush
        {
            Tint = Multiply(tint, fallback.Tint),
            DrawAs = fallback.DrawAs,
            ImageSize = fallback.ImageSize,
            Margin = fallback.Margin,
            CornerRadius = fallback.CornerRadius,
            HalfHeightRadius = fallback.HalfHeightRadius,
            OutlineColor = fallback.OutlineColor,
            OutlineWidth = fallback.OutlineWidth,
        };
        if (brush == null) return result;

        if (brush.TryGetValue(out FStructFallback tintColor, "TintColor"))
            result.Tint = Multiply(tint, SlateColor(tintColor, Colors.White));

        var size = Vector(brush, "ImageSize", new Point(result.ImageSize.Width, result.ImageSize.Height));
        result.ImageSize = new Size(Math.Max(0, size.X), Math.Max(0, size.Y));
        result.Margin = Margin(brush, "Margin", result.Margin);
        result.Tile = Enum(brush, "Tiling") is { } tiling && tiling != "NoTile";

        result.DrawAs = Enum(brush, "DrawAs") switch
        {
            "NoDrawType" => UmgDrawAs.NoDrawType,
            "Box" => UmgDrawAs.Box,
            "Border" => UmgDrawAs.Border,
            "RoundedBox" => UmgDrawAs.RoundedBox,
            "Image" => UmgDrawAs.Image,
            _ => result.DrawAs
        };

        if (brush.GetOrDefault<FStructFallback>("OutlineSettings") is { } outline)
        {
            if (outline.TryGetValue(out FVector4 radii, "CornerRadii"))
                result.CornerRadius = new CornerRadius(radii.X, radii.Y, radii.Z, radii.W);
            if (outline.TryGetValue(out FStructFallback outlineColor, "Color"))
                result.OutlineColor = Multiply(tint, SlateColor(outlineColor, Colors.Transparent));
            result.OutlineWidth = Number(outline, "Width", result.OutlineWidth);
            result.HalfHeightRadius = Enum(outline, "RoundingType") is not "FixedRadius" && result.DrawAs == UmgDrawAs.RoundedBox;
        }
        else if (result.DrawAs == UmgDrawAs.RoundedBox && fallback.DrawAs != UmgDrawAs.RoundedBox)
        {
            result.HalfHeightRadius = true; // FSlateBrushOutlineSettings defaults to ESlateBrushRoundingType::HalfHeightRadius
        }

        var resource = brush.GetOrDefault<UObject>("ResourceObject");
        if (resource != null)
        {
            result.ResourceName = resource.GetPathName();
            result.Image = _images.Get(resource, result.Tint);
            result.IsMaterial = result.Image == null;
        }

        return result;
    }

    #endregion

    #region Property helpers

    private static bool TryLoadObject(FPackageIndex index, out UObject result)
    {
        result = null;
        try
        {
            if (index.TryLoad(out var export)) result = export as UObject;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Debug(e, "Failed to load {Index}", index.Name);
        }

        return result != null;
    }

    private static FPropertyTag Find(IPropertyHolder holder, string name)
    {
        foreach (var property in holder.Properties)
            if (property.Name.Text.Equals(name, StringComparison.Ordinal)) return property;
        return null;
    }

    internal static double Number(IPropertyHolder holder, string name, double fallback) => Find(holder, name)?.Tag?.GenericValue switch
    {
        float f => f,
        double d => d,
        int i => i,
        long l => l,
        uint u => u,
        short s => s,
        ushort us => us,
        byte b => b,
        sbyte sb => sb,
        _ => fallback
    };

    private static double[] Numbers(IPropertyHolder holder, string name)
    {
        if (holder.GetOrDefault<float[]>(name) is { } floats) return floats.Select(f => (double) f).ToArray();
        if (holder.GetOrDefault<double[]>(name) is { } doubles) return doubles;
        return [];
    }

    internal static bool Bool(IPropertyHolder holder, string name, bool fallback) => Find(holder, name)?.Tag?.GenericValue is bool b ? b : fallback;

    /// <summary>The value of an enum property without its type ("EHorizontalAlignment::HAlign_Left" → "HAlign_Left").</summary>
    internal static string Enum(IPropertyHolder holder, string name)
    {
        var value = Find(holder, name)?.Tag?.GenericValue?.ToString();
        if (string.IsNullOrEmpty(value)) return null;
        var index = value.LastIndexOf("::", StringComparison.Ordinal);
        return index >= 0 ? value[(index + 2)..] : value;
    }

    private static Point Vector(IPropertyHolder holder, string name, Point fallback)
    {
        switch (Find(holder, name)?.Tag?.GenericValue)
        {
            case FScriptStruct { StructType: FVector2D v }:
                return new Point(v.X, v.Y);
            case FScriptStruct { StructType: FStructFallback s }:
                return new Point(Number(s, "X", fallback.X), Number(s, "Y", fallback.Y));
            case FScriptStruct { StructType: { } other }:
            {
                // TIntVector2<float> and friends
                var type = other.GetType();
                var x = type.GetField("X")?.GetValue(other) ?? type.GetProperty("X")?.GetValue(other);
                var y = type.GetField("Y")?.GetValue(other) ?? type.GetProperty("Y")?.GetValue(other);
                return x != null && y != null ? new Point(Convert.ToDouble(x), Convert.ToDouble(y)) : fallback;
            }
            default:
                return fallback;
        }
    }

    private static Thickness Margin(IPropertyHolder holder, string name, Thickness fallback)
    {
        if (holder.GetOrDefault<FStructFallback>(name) is not { } margin) return fallback;
        return new Thickness(Number(margin, "Left", fallback.Left), Number(margin, "Top", fallback.Top),
            Number(margin, "Right", fallback.Right), Number(margin, "Bottom", fallback.Bottom));
    }

    private static Color LinearColor(IPropertyHolder holder, string name, Color fallback)
    {
        return Find(holder, name)?.Tag?.GenericValue switch
        {
            FScriptStruct { StructType: FLinearColor c } => ToColor(c),
            FScriptStruct { StructType: FStructFallback s } => SlateColor(s, fallback), // FSlateColor where a color is expected
            _ => fallback
        };
    }

    private static Color SlateColor(IPropertyHolder holder, string name, Color fallback)
    {
        return Find(holder, name)?.Tag?.GenericValue switch
        {
            FScriptStruct { StructType: FStructFallback s } => SlateColor(s, fallback),
            FScriptStruct { StructType: FLinearColor c } => ToColor(c),
            _ => fallback
        };
    }

    private static Color SlateColor(FStructFallback slateColor, Color fallback)
    {
        // UseColor_Foreground & co. inherit the color of the parent, drawn as is
        var rule = Enum(slateColor, "ColorUseRule");
        if (rule is not null and not "UseColor_Specified" and not "UseColor_Specified_Link") return fallback == Colors.Transparent ? Colors.White : fallback;
        return slateColor.TryGetValue(out FLinearColor color, "SpecifiedColor") ? ToColor(color) : fallback;
    }

    internal static Color ToColor(FLinearColor color)
    {
        var srgb = color.ToFColor(true);
        return Color.FromArgb(srgb.A, srgb.R, srgb.G, srgb.B);
    }

    internal static Color Multiply(Color a, Color b) => Color.FromArgb(
        (byte) (a.A * b.A / 255), (byte) (a.R * b.R / 255), (byte) (a.G * b.G / 255), (byte) (a.B * b.B / 255));

    #endregion
}
