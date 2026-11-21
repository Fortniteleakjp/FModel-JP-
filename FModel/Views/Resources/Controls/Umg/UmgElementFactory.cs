using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using FModel.ViewModels.WidgetDesigner;

namespace FModel.Views.Resources.Controls.Umg;

/// <summary>
/// Builds a WPF element tree from an <see cref="UmgDocument"/>, one element per widget (its Tag is the widget)
/// laid out with the UMG rules of the parent's slot. Must run on the UI thread.
/// </summary>
public sealed class UmgElementFactory
{
    private const double _POINTS_TO_PIXELS = 96.0 / 72.0; // Slate font sizes are points at 96 dpi

    public readonly Dictionary<UmgWidget, FrameworkElement> Elements = new();

    public FrameworkElement Build(UmgWidget widget)
    {
        FrameworkElement element;
        try
        {
            element = Create(widget);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            element = Placeholder(widget, $"{widget.ClassName}: {e.Message}");
        }

        element.Tag = widget;
        element.Visibility = widget.Visibility;
        element.Opacity = widget.RenderOpacity;
        ApplyRenderTransform(element, widget);
        Elements[widget] = element;
        return element;
    }

    private static void ApplyRenderTransform(FrameworkElement element, UmgWidget widget)
    {
        // FWidgetTransform: scale, shear, rotation then translation, around the pivot
        if (widget.Scale == new Vector(1, 1) && widget.Shear == default && widget.Angle == 0 && widget.Translation == default) return;

        var group = new TransformGroup();
        if (widget.Scale != new Vector(1, 1)) group.Children.Add(new ScaleTransform(widget.Scale.X, widget.Scale.Y));
        if (widget.Shear != default) group.Children.Add(new SkewTransform(widget.Shear.X, widget.Shear.Y));
        if (widget.Angle != 0) group.Children.Add(new RotateTransform(widget.Angle));
        if (widget.Translation != default) group.Children.Add(new TranslateTransform(widget.Translation.X, widget.Translation.Y));
        element.RenderTransform = group;
        element.RenderTransformOrigin = widget.Pivot;
    }

    /// <summary>The child laid out by the parent's slot: padding and alignment, the canvas reads the slot itself.</summary>
    private FrameworkElement Child(UmgWidget child, Thickness extraMargin = default)
    {
        var element = Build(child);
        var slot = child.Slot;
        if (slot == null) return element;

        UmgLayout.SetSlot(element, slot);
        if (slot.Kind == UmgSlotKind.Canvas)
        {
            Panel.SetZIndex(element, slot.ZOrder);
            return element;
        }

        element.Margin = new Thickness(slot.Padding.Left + extraMargin.Left + slot.Nudge.X, slot.Padding.Top + extraMargin.Top + slot.Nudge.Y,
            slot.Padding.Right + extraMargin.Right - slot.Nudge.X, slot.Padding.Bottom + extraMargin.Bottom - slot.Nudge.Y);
        element.HorizontalAlignment = slot.HAlign;
        element.VerticalAlignment = slot.VAlign;
        return element;
    }

    private FrameworkElement Create(UmgWidget widget)
    {
        switch (widget.Kind)
        {
            case UmgKind.CanvasPanel:
            {
                var panel = new UmgCanvasPanel();
                foreach (var child in widget.Children) panel.Children.Add(Child(child));
                return panel;
            }
            case UmgKind.HorizontalBox:
            case UmgKind.VerticalBox:
            {
                var panel = new UmgBoxPanel { Horizontal = widget.Kind == UmgKind.HorizontalBox };
                foreach (var child in widget.Children) panel.Children.Add(Child(child));
                return panel;
            }
            case UmgKind.Overlay:
            case UmgKind.Content:
                return Layered(widget.Children.Select(c => Child(c)));
            case UmgKind.Border:
                return Layered([new UmgBrushElement(widget.Background), ..widget.Children.Select(c => Child(c))]);
            case UmgKind.Button:
                return Layered([new UmgBrushElement(widget.Background), ..widget.Children.Select(c => Child(c, widget.Padding))]);
            case UmgKind.UserWidget:
            {
                if (widget.Problem != null && widget.Children.Count == 0) return Placeholder(widget, widget.Problem);
                var grid = Layered(widget.Children.Select(c => Child(c)));
                grid.Margin = widget.Padding;
                return grid;
            }
            case UmgKind.SizeBox:
            case UmgKind.Spacer:
                return new UmgSizeBox
                {
                    WidthOverride = widget.WidthOverride, HeightOverride = widget.HeightOverride,
                    MinDesiredWidth = widget.MinWidth, MinDesiredHeight = widget.MinHeight, MaxDesiredWidth = widget.MaxWidth, MaxDesiredHeight = widget.MaxHeight,
                    Child = widget.Children.Count > 0 ? Child(widget.Children[0]) : null
                };
            case UmgKind.ScaleBox:
                return ScaleBox(widget);
            case UmgKind.ScrollBox:
            {
                var stack = new StackPanel { Orientation = widget.Horizontal ? Orientation.Horizontal : Orientation.Vertical };
                foreach (var child in widget.Children) stack.Children.Add(Child(child));
                return new Border { ClipToBounds = true, Child = stack };
            }
            case UmgKind.WrapBox:
            {
                var wrap = new WrapPanel { Orientation = widget.Horizontal ? Orientation.Horizontal : Orientation.Vertical };
                var spacing = widget.Horizontal
                    ? new Thickness(0, 0, widget.InnerSlotPadding, widget.Padding.Bottom)
                    : new Thickness(0, 0, widget.Padding.Bottom, widget.InnerSlotPadding);
                foreach (var child in widget.Children) wrap.Children.Add(Child(child, spacing));
                return wrap;
            }
            case UmgKind.GridPanel:
                return GridPanel(widget);
            case UmgKind.UniformGridPanel:
                return UniformGrid(widget);
            case UmgKind.WidgetSwitcher:
            {
                var grid = new Grid();
                for (var i = 0; i < widget.Children.Count; i++)
                {
                    var element = Child(widget.Children[i]);
                    if (i != Math.Clamp(widget.ActiveIndex, 0, widget.Children.Count - 1)) element.Visibility = Visibility.Collapsed;
                    grid.Children.Add(element);
                }

                return grid;
            }
            case UmgKind.Text:
                return Text(widget);
            case UmgKind.Image:
                return new UmgBrushElement(widget.Foreground) { UseImageSize = true };
            case UmgKind.EditableText:
                return EditableText(widget);
            case UmgKind.CheckBox:
                return CheckBox(widget);
            case UmgKind.ProgressBar:
                return ProgressBar(widget);
            case UmgKind.Slider:
                return Slider(widget);
            case UmgKind.ComboBox:
                return ComboBox(widget);
            case UmgKind.Throbber:
                return Throbber();
            default:
                if (widget.Children.Count > 0) return Layered(widget.Children.Select(c => Child(c)));
                // lists get filled at runtime, worth a box; other native widgets (CommonActionWidget, a lazy image...) draw nothing in the designer
                return IsList(widget.ClassName) ? Placeholder(widget, widget.ClassName) : new Grid { ToolTip = widget.ClassName };
        }
    }

    private static bool IsList(string className) =>
        className.Contains("ListView", StringComparison.Ordinal) || className.Contains("TileView", StringComparison.Ordinal) ||
        className.Contains("TreeView", StringComparison.Ordinal) || className.Contains("DynamicEntryBox", StringComparison.Ordinal);

    private static Grid Layered(IEnumerable<UIElement> children)
    {
        var grid = new Grid();
        foreach (var child in children) grid.Children.Add(child);
        return grid;
    }

    private FrameworkElement ScaleBox(UmgWidget widget)
    {
        var child = widget.Children.Count > 0 ? Child(widget.Children[0]) : null;
        if (child == null) return new Grid();

        if (widget.Stretch == "UserSpecified")
            return new Decorator { Child = new Decorator { Child = child, LayoutTransform = new ScaleTransform(widget.UserScale, widget.UserScale) } };

        var viewbox = new Viewbox
        {
            ClipToBounds = true,
            Child = child,
            Stretch = widget.Stretch switch
            {
                "None" => Stretch.None,
                "Fill" => Stretch.Fill,
                "ScaleToFill" => Stretch.UniformToFill,
                _ => Stretch.Uniform
            }
        };
        return viewbox;
    }

    private FrameworkElement GridPanel(UmgWidget widget)
    {
        var grid = new Grid();
        var columns = Math.Max(widget.ColumnFill.Length, widget.Children.Select(c => (c.Slot?.Column ?? 0) + (c.Slot?.ColumnSpan ?? 1)).DefaultIfEmpty(1).Max());
        var rows = Math.Max(widget.RowFill.Length, widget.Children.Select(c => (c.Slot?.Row ?? 0) + (c.Slot?.RowSpan ?? 1)).DefaultIfEmpty(1).Max());
        for (var i = 0; i < columns; i++)
        {
            var fill = i < widget.ColumnFill.Length ? widget.ColumnFill[i] : 0;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = fill > 0 ? new GridLength(fill, GridUnitType.Star) : GridLength.Auto });
        }

        for (var i = 0; i < rows; i++)
        {
            var fill = i < widget.RowFill.Length ? widget.RowFill[i] : 0;
            grid.RowDefinitions.Add(new RowDefinition { Height = fill > 0 ? new GridLength(fill, GridUnitType.Star) : GridLength.Auto });
        }

        foreach (var child in widget.Children.OrderBy(c => c.Slot?.Layer ?? 0))
        {
            var element = Child(child);
            Grid.SetRow(element, Math.Clamp(child.Slot?.Row ?? 0, 0, rows - 1));
            Grid.SetColumn(element, Math.Clamp(child.Slot?.Column ?? 0, 0, columns - 1));
            Grid.SetRowSpan(element, Math.Max(1, child.Slot?.RowSpan ?? 1));
            Grid.SetColumnSpan(element, Math.Max(1, child.Slot?.ColumnSpan ?? 1));
            grid.Children.Add(element);
        }

        return grid;
    }

    private FrameworkElement UniformGrid(UmgWidget widget)
    {
        var grid = new Grid();
        var columns = widget.Children.Select(c => (c.Slot?.Column ?? 0) + 1).DefaultIfEmpty(1).Max();
        var rows = widget.Children.Select(c => (c.Slot?.Row ?? 0) + 1).DefaultIfEmpty(1).Max();
        for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = widget.MinSlotWidth });
        for (var i = 0; i < rows; i++) grid.RowDefinitions.Add(new RowDefinition { MinHeight = widget.MinSlotHeight });
        foreach (var child in widget.Children)
        {
            var element = Child(child, widget.SlotPadding);
            Grid.SetRow(element, Math.Max(0, child.Slot?.Row ?? 0));
            Grid.SetColumn(element, Math.Max(0, child.Slot?.Column ?? 0));
            grid.Children.Add(element);
        }

        // star sizing so every cell gets the same size, like SUniformGridPanel
        foreach (var column in grid.ColumnDefinitions) column.Width = new GridLength(1, GridUnitType.Star);
        foreach (var row in grid.RowDefinitions) row.Height = new GridLength(1, GridUnitType.Star);
        return grid;
    }

    private static TextBlock TextBlock(UmgWidget widget, string text, Color color)
    {
        var font = widget.Font ?? new UmgFont { Family = UmgFontCache.DefaultFamily };
        var block = new TextBlock
        {
            Text = text ?? string.Empty,
            FontFamily = font.Family ?? UmgFontCache.DefaultFamily,
            FontSize = Math.Max(1, font.Size * _POINTS_TO_PIXELS),
            FontWeight = font.Weight,
            FontStyle = font.Style,
            Foreground = Frozen(color),
            TextAlignment = widget.Justification,
            TextWrapping = widget.AutoWrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        TextOptions.SetTextFormattingMode(block, TextFormattingMode.Ideal);
        return block;
    }

    private FrameworkElement Text(UmgWidget widget)
    {
        var block = TextBlock(widget, widget.Text, widget.TextColor);
        block.ToolTip = widget.Font?.Description;
        if (widget.ShadowColor is not { A: > 0 } shadowColor) return block;

        var shadow = TextBlock(widget, widget.Text, shadowColor);
        shadow.RenderTransform = new TranslateTransform(widget.ShadowOffset.X, widget.ShadowOffset.Y);
        return Layered([shadow, block]);
    }

    private FrameworkElement EditableText(UmgWidget widget)
    {
        var empty = string.IsNullOrEmpty(widget.Text);
        var block = TextBlock(widget, empty ? widget.HintText : widget.Text, widget.TextColor);
        if (empty) block.Opacity = 0.5;
        block.VerticalAlignment = widget.MultiLine ? VerticalAlignment.Top : VerticalAlignment.Center;
        block.Margin = widget.Padding;
        block.TextTrimming = widget.MultiLine ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        return widget.Background != null ? Layered([new UmgBrushElement(widget.Background), block]) : block;
    }

    private FrameworkElement CheckBox(UmgWidget widget)
    {
        var content = widget.Children.Select(c => Child(c)).ToList();
        if (!widget.Horizontal)
        {
            // ToggleButton: the image is the background of the content
            var toggle = Layered([new UmgBrushElement(widget.Background) { UseImageSize = content.Count == 0 }, ..content]);
            foreach (var element in content) element.Margin = new Thickness(element.Margin.Left + widget.Padding.Left, element.Margin.Top + widget.Padding.Top,
                element.Margin.Right + widget.Padding.Right, element.Margin.Bottom + widget.Padding.Bottom);
            return toggle;
        }

        var box = new Grid { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        box.Children.Add(new UmgBrushElement(widget.Background));
        if (widget.IsChecked)
        {
            box.Children.Add(new Path
            {
                Data = Geometry.Parse("M3,8 L7,12 L13,4"), Stroke = Frozen(Color.FromRgb(0x20, 0x20, 0x20)), StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(box);
        foreach (var element in content)
        {
            element.Margin = new Thickness(element.Margin.Left + widget.Padding.Left + 4, element.Margin.Top, element.Margin.Right, element.Margin.Bottom);
            row.Children.Add(element);
        }

        return row;
    }

    private static FrameworkElement ProgressBar(UmgWidget widget)
    {
        var grid = new Grid { MinHeight = 4 };
        grid.Children.Add(new UmgBrushElement(widget.Background) { UseImageSize = true });
        var fill = new UmgBrushElement(widget.Foreground);
        var fillHost = new Grid();
        if (widget.Horizontal)
        {
            fillHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(widget.Percent, 0.0001), GridUnitType.Star) });
            fillHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - widget.Percent, 0.0001), GridUnitType.Star) });
        }
        else
        {
            fillHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Max(1 - widget.Percent, 0.0001), GridUnitType.Star) });
            fillHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Max(widget.Percent, 0.0001), GridUnitType.Star) });
            Grid.SetRow(fill, 1);
        }

        if (widget.Percent > 0) fillHost.Children.Add(fill);
        grid.Children.Add(fillHost);
        return grid;
    }

    private static FrameworkElement Slider(UmgWidget widget)
    {
        var grid = new Grid { MinWidth = widget.Horizontal ? 64 : 20, MinHeight = widget.Horizontal ? 20 : 64 };
        grid.Children.Add(new Rectangle
        {
            Fill = Frozen(widget.Background.Tint),
            Height = widget.Horizontal ? 4 : double.NaN, Width = widget.Horizontal ? double.NaN : 4,
            RadiusX = 2, RadiusY = 2,
            HorizontalAlignment = widget.Horizontal ? HorizontalAlignment.Stretch : HorizontalAlignment.Center,
            VerticalAlignment = widget.Horizontal ? VerticalAlignment.Center : VerticalAlignment.Stretch
        });

        var thumbHost = new Grid();
        var thumb = new Ellipse { Width = 14, Height = 14, Fill = Frozen(widget.Foreground.Tint) };
        if (widget.Horizontal)
        {
            thumbHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(widget.Percent, 0.0001), GridUnitType.Star) });
            thumbHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            thumbHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - widget.Percent, 0.0001), GridUnitType.Star) });
            Grid.SetColumn(thumb, 1);
        }
        else
        {
            thumbHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Max(1 - widget.Percent, 0.0001), GridUnitType.Star) });
            thumbHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            thumbHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Max(widget.Percent, 0.0001), GridUnitType.Star) });
            Grid.SetRow(thumb, 1);
        }

        thumbHost.Children.Add(thumb);
        grid.Children.Add(thumbHost);
        return grid;
    }

    private static FrameworkElement ComboBox(UmgWidget widget)
    {
        var grid = new Grid();
        grid.Children.Add(new UmgBrushElement(widget.Background));
        var text = TextBlock(widget, widget.Text, widget.TextColor);
        text.Margin = widget.Padding;
        text.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(text);
        grid.Children.Add(new Path
        {
            Data = Geometry.Parse("M0,0 L8,0 L4,5 Z"), Fill = Frozen(widget.TextColor), Margin = new Thickness(0, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
        });
        return grid;
    }

    private static FrameworkElement Throbber()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < 3; i++)
            row.Children.Add(new Ellipse { Width = 10, Height = 10, Margin = new Thickness(2), Fill = Frozen(Color.FromArgb((byte) (255 - i * 70), 255, 255, 255)) });
        return row;
    }

    /// <summary>A widget that can't be drawn here (ListView, a missing user widget...): a dashed box with its class name.</summary>
    private FrameworkElement Placeholder(UmgWidget widget, string label)
    {
        var grid = new Grid { MinWidth = 32, MinHeight = 24 };
        grid.Children.Add(new Rectangle
        {
            Stroke = Frozen(Color.FromArgb(160, 255, 170, 60)), StrokeThickness = 1, StrokeDashArray = [4, 3],
            Fill = Frozen(Color.FromArgb(28, 255, 170, 60))
        });
        grid.Children.Add(new TextBlock
        {
            Text = label, Foreground = Frozen(Color.FromArgb(220, 255, 200, 140)), FontSize = 11, Margin = new Thickness(4, 2, 4, 2),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
        });
        foreach (var child in widget.Children) grid.Children.Add(Child(child));
        grid.ToolTip = label;
        return grid;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
