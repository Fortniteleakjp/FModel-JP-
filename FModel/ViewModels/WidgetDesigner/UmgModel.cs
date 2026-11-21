using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using CUE4Parse.UE4.Assets.Exports;

namespace FModel.ViewModels.WidgetDesigner;

/// <summary>What a widget is drawn as, the UMG classes collapsed to the layouts FModel knows how to draw.</summary>
public enum UmgKind
{
    Unknown,
    CanvasPanel,
    HorizontalBox,
    VerticalBox,
    Overlay,
    Border,
    Button,
    SizeBox,
    ScaleBox,
    ScrollBox,
    WrapBox,
    GridPanel,
    UniformGridPanel,
    WidgetSwitcher,
    /// <summary>A panel with a single child and no visuals of its own (SafeZone, InvalidationBox, RetainerBox, NamedSlot, ...).</summary>
    Content,
    Text,
    Image,
    EditableText,
    CheckBox,
    ProgressBar,
    Slider,
    ComboBox,
    Spacer,
    Throbber,
    /// <summary>An instance of another widget blueprint, drawn from that blueprint's own tree.</summary>
    UserWidget
}

public enum UmgSlotKind
{
    None,
    Canvas,
    Box,
    Overlay,
    Grid,
    UniformGrid,
    Plain
}

public enum UmgDrawAs
{
    NoDrawType,
    Image,
    Box,
    Border,
    RoundedBox
}

/// <summary>An FSlateBrush resolved to what WPF needs: a color, an optional image and the shape to fill.</summary>
public sealed class UmgBrush
{
    public Color Tint = Colors.White;
    public UmgDrawAs DrawAs = UmgDrawAs.Image;
    public ImageSource Image;
    /// <summary>The brush points at a material (or any non texture resource), which can't be rendered here.</summary>
    public bool IsMaterial;
    public string ResourceName;
    public Size ImageSize = new(32, 32);
    public Thickness Margin;
    public CornerRadius CornerRadius;
    /// <summary>RoundedBox with the default rounding type: half the height on every corner.</summary>
    public bool HalfHeightRadius;
    public Color OutlineColor = Colors.Transparent;
    public double OutlineWidth;
    public bool Tile;

    public bool IsVisible => DrawAs != UmgDrawAs.NoDrawType && Tint.A > 0;
}

public sealed class UmgFont
{
    public FontFamily Family;
    public double Size = 24; // points, like Slate
    public FontWeight Weight = FontWeights.Normal;
    public FontStyle Style = FontStyles.Normal;
    public double LetterSpacing;
    public string Description;
}

/// <summary>The slot that places a widget in its parent panel.</summary>
public sealed class UmgSlot
{
    public UmgSlotKind Kind;
    public string ClassName;
    public UObject Object;

    public Thickness Padding;
    public HorizontalAlignment HAlign = HorizontalAlignment.Stretch;
    public VerticalAlignment VAlign = VerticalAlignment.Stretch;

    // box slots
    public bool Fill;
    public double FillValue = 1;
    public double MaxSize;

    // canvas slots
    public Point AnchorMin;
    public Point AnchorMax;
    public double Left;
    public double Top;
    public double Right = 100;
    public double Bottom = 30;
    public Point Alignment;
    public bool AutoSize;
    public int ZOrder;

    // grid slots
    public int Row;
    public int Column;
    public int RowSpan = 1;
    public int ColumnSpan = 1;
    public int Layer;
    public Vector Nudge;
}

public sealed class UmgWidget
{
    public string Name;
    public string ClassName;
    public UmgKind Kind;
    public UObject Object;
    public UmgSlot Slot;
    public UmgWidget Parent;
    public readonly List<UmgWidget> Children = [];

    /// <summary>The widget comes from the tree of a nested user widget, not from the asset itself.</summary>
    public bool IsInherited;
    /// <summary>Package the widget was read from, for nested user widgets.</summary>
    public string SourcePackage;
    /// <summary>The nested widget blueprint could not be found or read.</summary>
    public string Problem;

    public Visibility Visibility = Visibility.Visible;
    public double RenderOpacity = 1;
    public Vector Translation;
    public Vector Scale = new(1, 1);
    public Vector Shear;
    public double Angle;
    public Point Pivot = new(0.5, 0.5);
    public Color ColorAndOpacity = Colors.White;

    // brushes
    public UmgBrush Background;
    public UmgBrush Foreground; // image of an Image, fill of a progress bar, checked mark...
    public UmgBrush Hovered;

    // container
    public Thickness Padding;
    public HorizontalAlignment ContentHAlign = HorizontalAlignment.Stretch;
    public VerticalAlignment ContentVAlign = VerticalAlignment.Stretch;
    public Color ContentColor = Colors.White;
    public bool Horizontal;

    // SizeBox
    public double? WidthOverride, HeightOverride, MinWidth, MinHeight, MaxWidth, MaxHeight, MinAspect, MaxAspect;

    // ScaleBox
    public string Stretch = "ScaleToFit";
    public double UserScale = 1;

    // switcher / grids
    public int ActiveIndex;
    public double[] ColumnFill = [];
    public double[] RowFill = [];
    public Thickness SlotPadding;
    public double MinSlotWidth, MinSlotHeight;
    public double InnerSlotPadding;

    // text
    public string Text;
    public string HintText;
    public UmgFont Font;
    public Color TextColor = Colors.White;
    public TextAlignment Justification = TextAlignment.Left;
    public bool AutoWrap;
    public bool MultiLine;
    public string TransformPolicy;
    /// <summary>The text was set from a text variable of the nested user widget instance named here.</summary>
    public string TextOverriddenBy;
    public Color? ShadowColor;
    public Vector ShadowOffset;

    // value widgets
    public double Percent;
    public bool IsChecked;

    public bool IsPanel => Kind is UmgKind.CanvasPanel or UmgKind.HorizontalBox or UmgKind.VerticalBox or UmgKind.Overlay
        or UmgKind.ScrollBox or UmgKind.WrapBox or UmgKind.GridPanel or UmgKind.UniformGridPanel or UmgKind.WidgetSwitcher;

    public string DisplayName => IsInherited ? Name : Name;
    public string Subtitle => ClassName;

    public IEnumerable<UmgWidget> Descendants()
    {
        yield return this;
        foreach (var child in Children)
        foreach (var d in child.Descendants())
            yield return d;
    }

    public override string ToString() => $"{Name} ({ClassName})";
}

public sealed class UmgDocument
{
    public string Name;
    public string PackagePath;
    public string BlueprintClass;
    public string ParentClass;
    public UmgWidget Root;
    /// <summary>Design size, null when the widget should be shown at its desired size.</summary>
    public Size? DesignSize;
    public string SizeMode;
    public bool IsEditorUtility;
    public bool IsEditorData;
    public int WidgetCount;
    public readonly List<string> Notes = [];
}
