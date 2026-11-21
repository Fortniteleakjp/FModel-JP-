using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FModel.ViewModels.WidgetDesigner;

namespace FModel.Views.Resources.Controls.Umg;

/// <summary>Where a child sits in its UMG parent, read by the panels below.</summary>
public static class UmgLayout
{
    public static readonly DependencyProperty SlotProperty = DependencyProperty.RegisterAttached(
        "Slot", typeof(UmgSlot), typeof(UmgLayout), new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static UmgSlot GetSlot(DependencyObject element) => (UmgSlot) element.GetValue(SlotProperty);
    public static void SetSlot(DependencyObject element, UmgSlot value) => element.SetValue(SlotProperty, value);
}

/// <summary>UCanvasPanel / SConstraintCanvas: anchors, offsets, alignment and auto size.</summary>
public sealed class UmgCanvasPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var desired = new Size();
        foreach (UIElement child in InternalChildren)
        {
            var slot = UmgLayout.GetSlot(child) ?? new UmgSlot { Kind = UmgSlotKind.Canvas };
            var stretchX = slot.AnchorMin.X != slot.AnchorMax.X;
            var stretchY = slot.AnchorMin.Y != slot.AnchorMax.Y;

            var constraint = new Size(
                slot.AutoSize ? double.PositiveInfinity : stretchX ? Finite(availableSize.Width) : Math.Max(0, slot.Right),
                slot.AutoSize ? double.PositiveInfinity : stretchY ? Finite(availableSize.Height) : Math.Max(0, slot.Bottom));
            child.Measure(constraint);

            // like SConstraintCanvas::ComputeDesiredSize: the extent of the children anchored to the top left
            if (!stretchX && slot.AnchorMin.X == 0)
                desired.Width = Math.Max(desired.Width, slot.Left + (slot.AutoSize ? child.DesiredSize.Width : slot.Right) * (1 - slot.Alignment.X));
            if (!stretchY && slot.AnchorMin.Y == 0)
                desired.Height = Math.Max(desired.Height, slot.Top + (slot.AutoSize ? child.DesiredSize.Height : slot.Bottom) * (1 - slot.Alignment.Y));
        }

        return desired;
    }

    private static double Finite(double value) => double.IsInfinity(value) ? 1920 : value;

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            var slot = UmgLayout.GetSlot(child) ?? new UmgSlot { Kind = UmgSlotKind.Canvas };
            child.Arrange(ComputeRect(slot, child.DesiredSize, finalSize));
        }

        return finalSize;
    }

    public static Rect ComputeRect(UmgSlot slot, Size desired, Size parent)
    {
        var size = slot.AutoSize ? desired : new Size(slot.Right, slot.Bottom);
        double x, y, width, height;

        if (slot.AnchorMin.X != slot.AnchorMax.X)
        {
            x = parent.Width * slot.AnchorMin.X + slot.Left;
            width = parent.Width * slot.AnchorMax.X - x - slot.Right;
        }
        else
        {
            x = parent.Width * slot.AnchorMin.X + slot.Left - size.Width * slot.Alignment.X;
            width = size.Width;
        }

        if (slot.AnchorMin.Y != slot.AnchorMax.Y)
        {
            y = parent.Height * slot.AnchorMin.Y + slot.Top;
            height = parent.Height * slot.AnchorMax.Y - y - slot.Bottom;
        }
        else
        {
            y = parent.Height * slot.AnchorMin.Y + slot.Top - size.Height * slot.Alignment.Y;
            height = size.Height;
        }

        return new Rect(x, y, Math.Max(0, width), Math.Max(0, height));
    }
}

/// <summary>UHorizontalBox / UVerticalBox: Automatic children get their desired size, Fill children share the rest.</summary>
public sealed class UmgBoxPanel : Panel
{
    public bool Horizontal { get; set; }

    private double Main(Size s) => Horizontal ? s.Width : s.Height;
    private double Cross(Size s) => Horizontal ? s.Height : s.Width;
    private Size Make(double main, double cross) => Horizontal ? new Size(main, cross) : new Size(cross, main);

    protected override Size MeasureOverride(Size availableSize)
    {
        double usedMain = 0, maxCross = 0, fillTotal = 0;
        var children = InternalChildren.Cast<UIElement>().ToArray();

        foreach (var child in children)
        {
            var slot = UmgLayout.GetSlot(child);
            if (slot is { Fill: true })
            {
                fillTotal += Math.Max(0, slot.FillValue);
                continue;
            }

            child.Measure(Make(double.PositiveInfinity, Cross(availableSize)));
            var main = Main(child.DesiredSize);
            if (slot is { MaxSize: > 0 }) main = Math.Min(main, slot.MaxSize);
            usedMain += main;
            maxCross = Math.Max(maxCross, Cross(child.DesiredSize));
        }

        var remaining = double.IsInfinity(Main(availableSize)) ? double.PositiveInfinity : Math.Max(0, Main(availableSize) - usedMain);
        foreach (var child in children)
        {
            var slot = UmgLayout.GetSlot(child);
            if (slot is not { Fill: true }) continue;

            var share = double.IsInfinity(remaining) || fillTotal <= 0 ? double.PositiveInfinity : remaining * Math.Max(0, slot.FillValue) / fillTotal;
            child.Measure(Make(share, Cross(availableSize)));
            usedMain += Main(child.DesiredSize);
            maxCross = Math.Max(maxCross, Cross(child.DesiredSize));
        }

        return Make(usedMain, maxCross);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren.Cast<UIElement>().ToArray();
        double autoTotal = 0, fillTotal = 0;
        foreach (var child in children)
        {
            var slot = UmgLayout.GetSlot(child);
            if (slot is { Fill: true }) fillTotal += Math.Max(0, slot.FillValue);
            else autoTotal += AutoMain(child, slot);
        }

        var remaining = Math.Max(0, Main(finalSize) - autoTotal);
        double offset = 0;
        foreach (var child in children)
        {
            var slot = UmgLayout.GetSlot(child);
            var main = slot is { Fill: true }
                ? fillTotal > 0 ? remaining * Math.Max(0, slot.FillValue) / fillTotal : 0
                : AutoMain(child, slot);

            var rect = Horizontal ? new Rect(offset, 0, main, finalSize.Height) : new Rect(0, offset, finalSize.Width, main);
            child.Arrange(rect);
            offset += main;
        }

        return finalSize;
    }

    private double AutoMain(UIElement child, UmgSlot slot)
    {
        var main = Main(child.DesiredSize);
        return slot is { MaxSize: > 0 } ? Math.Min(main, slot.MaxSize) : main;
    }
}

/// <summary>USizeBox: the overrides replace the desired size of the content, the content still fills what it's given.</summary>
public sealed class UmgSizeBox : Decorator
{
    public double? WidthOverride, HeightOverride, MinDesiredWidth, MinDesiredHeight, MaxDesiredWidth, MaxDesiredHeight;

    protected override Size MeasureOverride(Size constraint)
    {
        // overrides can be saved negative (or NaN), which WPF sizes refuse: Slate treats them as zero
        var widthOverride = Valid(WidthOverride);
        var heightOverride = Valid(HeightOverride);
        var available = new Size(widthOverride ?? Clamp(constraint.Width, null, MaxDesiredWidth), heightOverride ?? Clamp(constraint.Height, null, MaxDesiredHeight));
        var desired = new Size();
        if (Child != null)
        {
            Child.Measure(available);
            desired = Child.DesiredSize;
        }

        return new Size(
            widthOverride ?? Clamp(desired.Width, MinDesiredWidth, MaxDesiredWidth),
            heightOverride ?? Clamp(desired.Height, MinDesiredHeight, MaxDesiredHeight));
    }

    private static double? Valid(double? value) => value is { } v ? double.IsNaN(v) ? 0 : Math.Max(0, v) : null;

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(arrangeSize));
        return arrangeSize;
    }

    private static double Clamp(double value, double? min, double? max)
    {
        if (max is > 0) value = Math.Min(value, max.Value);
        if (min is > 0) value = Math.Max(value, min.Value);
        return value;
    }
}

/// <summary>Draws an <see cref="UmgBrush"/> over its whole area, desired size = the brush's image size when asked to.</summary>
public sealed class UmgBrushElement : FrameworkElement
{
    private static readonly Pen _materialPen = CreateMaterialPen();

    public UmgBrush Brush { get; }
    public bool UseImageSize { get; init; }

    public UmgBrushElement(UmgBrush brush)
    {
        Brush = brush;
        SnapsToDevicePixels = true;
        if (brush is { IsMaterial: true, ResourceName: { } name }) ToolTip = name;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        UseImageSize && Brush != null && Brush.DrawAs != UmgDrawAs.NoDrawType ? Brush.ImageSize : new Size();

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(RenderSize);
        if (Brush == null || !Brush.IsVisible || rect.Width <= 0 || rect.Height <= 0) return;
        Draw(dc, Brush, rect);
    }

    public static void Draw(DrawingContext dc, UmgBrush brush, Rect rect)
    {
        if (brush.Image is { } image)
        {
            switch (brush.DrawAs)
            {
                case UmgDrawAs.Box:
                case UmgDrawAs.Border:
                    DrawNineSlice(dc, brush, image, rect, brush.DrawAs == UmgDrawAs.Border);
                    return;
                case UmgDrawAs.RoundedBox:
                    dc.DrawGeometry(ImageBrushFor(brush, image), Outline(brush), RoundedRect(brush, rect));
                    return;
                default:
                    dc.DrawRectangle(ImageBrushFor(brush, image), null, rect);
                    return;
            }
        }

        var fill = new SolidColorBrush(FillColor(brush));
        fill.Freeze();
        switch (brush.DrawAs)
        {
            case UmgDrawAs.RoundedBox:
                dc.DrawGeometry(fill, Outline(brush), RoundedRect(brush, rect));
                break;
            case UmgDrawAs.Border:
            {
                var thickness = Math.Max(1, brush.Margin.Left * brush.ImageSize.Width);
                var pen = new Pen(brush.OutlineWidth > 0 ? new SolidColorBrush(brush.OutlineColor) : fill, thickness);
                pen.Freeze();
                var half = thickness / 2;
                if (rect.Width > thickness && rect.Height > thickness)
                    dc.DrawRectangle(brush.Image == null && brush.OutlineWidth > 0 ? fill : null, pen, new Rect(rect.X + half, rect.Y + half, rect.Width - thickness, rect.Height - thickness));
                break;
            }
            default:
                dc.DrawRectangle(fill, null, rect);
                if (brush.IsMaterial) dc.DrawRectangle(null, _materialPen, new Rect(rect.X + .5, rect.Y + .5, Math.Max(0, rect.Width - 1), Math.Max(0, rect.Height - 1)));
                break;
        }
    }

    /// <summary>
    /// A material can't be drawn here. One the designer colored is filled with that color (a tinted box material...),
    /// one left white is whatever the material makes of it (a radial gauge, a glow...): a faint fill so it doesn't hide what's under it.
    /// </summary>
    private static Color FillColor(UmgBrush brush)
    {
        var tint = brush.Tint;
        if (!brush.IsMaterial || tint.R < 0xF0 || tint.G < 0xF0 || tint.B < 0xF0) return tint;
        return Color.FromArgb((byte) (tint.A * 0.15), tint.R, tint.G, tint.B);
    }

    private static Pen CreateMaterialPen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 1) { DashStyle = new DashStyle([2, 3], 0) };
        pen.Freeze();
        return pen;
    }

    private static Brush ImageBrushFor(UmgBrush brush, ImageSource image)
    {
        var result = new ImageBrush(image) { Stretch = Stretch.Fill };
        if (brush.Tile && brush.ImageSize.Width > 0 && brush.ImageSize.Height > 0)
        {
            result.TileMode = TileMode.Tile;
            result.ViewportUnits = BrushMappingMode.Absolute;
            result.Viewport = new Rect(brush.ImageSize);
        }

        result.Freeze();
        return result;
    }

    private static Pen Outline(UmgBrush brush)
    {
        if (brush.OutlineWidth <= 0 || brush.OutlineColor.A == 0) return null;
        var pen = new Pen(new SolidColorBrush(brush.OutlineColor), brush.OutlineWidth);
        pen.Freeze();
        return pen;
    }

    private static Geometry RoundedRect(UmgBrush brush, Rect rect)
    {
        var r = brush.HalfHeightRadius
            ? new CornerRadius(Math.Min(rect.Width, rect.Height) / 2)
            : brush.CornerRadius;
        var max = Math.Min(rect.Width, rect.Height) / 2;
        double tl = Math.Min(r.TopLeft, max), tr = Math.Min(r.TopRight, max), br = Math.Min(r.BottomRight, max), bl = Math.Min(r.BottomLeft, max);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(rect.Left + tl, rect.Top), true, true);
            ctx.LineTo(new Point(rect.Right - tr, rect.Top), true, false);
            if (tr > 0) ctx.ArcTo(new Point(rect.Right, rect.Top + tr), new Size(tr, tr), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(rect.Right, rect.Bottom - br), true, false);
            if (br > 0) ctx.ArcTo(new Point(rect.Right - br, rect.Bottom), new Size(br, br), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(rect.Left + bl, rect.Bottom), true, false);
            if (bl > 0) ctx.ArcTo(new Point(rect.Left, rect.Bottom - bl), new Size(bl, bl), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(rect.Left, rect.Top + tl), true, false);
            if (tl > 0) ctx.ArcTo(new Point(rect.Left + tl, rect.Top), new Size(tl, tl), 0, false, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>Slate's Box/Border draw: the corners keep their size (margin × image size), the edges and center stretch.</summary>
    private static void DrawNineSlice(DrawingContext dc, UmgBrush brush, ImageSource image, Rect rect, bool skipCenter)
    {
        var m = brush.Margin;
        if (m is { Left: 0, Top: 0, Right: 0, Bottom: 0 })
        {
            if (!skipCenter) dc.DrawRectangle(ImageBrushFor(brush, image), null, rect);
            return;
        }

        double l = m.Left * brush.ImageSize.Width, t = m.Top * brush.ImageSize.Height;
        double r = m.Right * brush.ImageSize.Width, b = m.Bottom * brush.ImageSize.Height;
        // corners shrink when the area is smaller than them
        var sx = l + r > rect.Width && l + r > 0 ? rect.Width / (l + r) : 1;
        var sy = t + b > rect.Height && t + b > 0 ? rect.Height / (t + b) : 1;
        l *= sx; r *= sx; t *= sy; b *= sy;

        double[] dx = [rect.Left, rect.Left + l, rect.Right - r, rect.Right];
        double[] dy = [rect.Top, rect.Top + t, rect.Bottom - b, rect.Bottom];
        double[] ux = [0, m.Left, 1 - m.Right, 1];
        double[] uy = [0, m.Top, 1 - m.Bottom, 1];

        for (var row = 0; row < 3; row++)
        for (var col = 0; col < 3; col++)
        {
            if (skipCenter && row == 1 && col == 1) continue;
            var dest = new Rect(new Point(dx[col], dy[row]), new Point(dx[col + 1], dy[row + 1]));
            var src = new Rect(new Point(ux[col], uy[row]), new Point(ux[col + 1], uy[row + 1]));
            if (dest.Width <= 0 || dest.Height <= 0 || src.Width <= 0 || src.Height <= 0) continue;

            var part = new ImageBrush(image) { Viewbox = src, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Stretch = Stretch.Fill };
            part.Freeze();
            dc.DrawRectangle(part, null, dest);
        }
    }
}
