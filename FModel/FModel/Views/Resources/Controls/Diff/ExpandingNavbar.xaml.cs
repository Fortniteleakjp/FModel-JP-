using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DiffPlex.DiffBuilder.Model;
using static FModel.Views.Resources.Controls.Diff.DataDiffViewer;

namespace FModel.Views.Resources.Controls.Diff;

public partial class ExpandingNavbar
{
    public event Action<int> LineClicked;
    private bool _isExpanded;
    private const double NavExpandedWidth = 200, NavCollapsedWidth = 36;
    private readonly List<LineMeta> _meta = [];
    private HashSet<string> _movedStrings = [];
    private ScrollViewer _editorScroll;
    public int TotalLines { get; private set; }

    private bool _isDraggingIndicator;
    private double _indicatorDragOffset;

    public ExpandingNavbar()
    {
        InitializeComponent();
        NavContent.Width = NavCollapsedWidth;
        MarkerCanvas.MouseLeftButtonUp += OnCanvasClick;
        MarkerCanvas.PreviewMouseLeftButtonDown += MarkerCanvas_MouseDown;
        MarkerCanvas.PreviewMouseMove += MarkerCanvas_MouseMove;
        MarkerCanvas.PreviewMouseLeftButtonUp += MarkerCanvas_MouseUp;
        MarkerCanvas.PreviewMouseWheel += MarkerCanvas_MouseWheel;
        MarkerCanvas.SizeChanged += MarkerCanvas_SizeChanged;
    }

    public void Attach(ScrollViewer editorScroll, List<LineMeta> meta, HashSet<string> movedStrings)
    {
        _editorScroll = editorScroll;
        _editorScroll.ScrollChanged += EditorScrollChanged;
        UpdateNavbar(meta, movedStrings);
    }

    public void UpdateNavbar(List<LineMeta> meta, HashSet<string> movedStrings, bool updateScrollIndicator = false)
    {
        _meta.Clear();
        _meta.AddRange(meta);
        _movedStrings = movedStrings;
        TotalLines = meta.Count;
        BuildMarkers();
        if (updateScrollIndicator)
            UpdateScrollIndicator();
    }

    private void MarkerCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (MarkerCanvas.ActualHeight <= 0 || ScrollIndicator.ActualHeight <= 0)
            return;

        _isDraggingIndicator = true;
        var pos = e.GetPosition(MarkerCanvas);

        double newTop = pos.Y - ScrollIndicator.ActualHeight / 2;
        double maxTop = MarkerCanvas.ActualHeight - ScrollIndicator.ActualHeight;
        newTop = Math.Max(0, Math.Min(newTop, maxTop));
        Canvas.SetTop(ScrollIndicator, newTop);

        _indicatorDragOffset = pos.Y - newTop;

        UpdateEditorScrollFromIndicator();

        MarkerCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void MarkerCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_editorScroll == null)
            return;

        const double scrollPercent = 0.01;

        double scrollableHeight = _editorScroll.ExtentHeight - _editorScroll.ViewportHeight;
        if (scrollableHeight <= 0)
            return;

        double deltaFactor = Math.Sign(e.Delta);
        double scrollAmount = scrollableHeight * scrollPercent * deltaFactor;
        _editorScroll.ScrollToVerticalOffset(_editorScroll.VerticalOffset - scrollAmount);
        e.Handled = true;
    }

    private void MarkerCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingIndicator)
            return;

        var pos = e.GetPosition(MarkerCanvas);
        double newTop = pos.Y - _indicatorDragOffset;
        double maxTop = MarkerCanvas.ActualHeight - ScrollIndicator.ActualHeight;
        newTop = Math.Max(0, Math.Min(newTop, maxTop));
        Canvas.SetTop(ScrollIndicator, newTop);

        UpdateEditorScrollFromIndicator();

        e.Handled = true;
    }

    private void MarkerCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double tolerance = 0.5;
        if (Math.Abs(e.PreviousSize.Height - e.NewSize.Height) < tolerance)
            return;

        BuildMarkers();
        UpdateScrollIndicator();
    }

    private void MarkerCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingIndicator)
            return;

        _isDraggingIndicator = false;
        MarkerCanvas.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void EditorScrollChanged(object s, ScrollChangedEventArgs e)
    {
        if (_isDraggingIndicator)
            return;

        UpdateScrollIndicator();
    }

    private void UpdateScrollIndicator()
    {
        if (TotalLines == 0 || MarkerCanvas.ActualHeight <= 0 || _editorScroll == null)
            return;

        double canvasHeight = MarkerCanvas.ActualHeight;
        double extentHeight = _editorScroll.ExtentHeight;
        double viewportHeight = _editorScroll.ViewportHeight;
        double verticalOffset = _editorScroll.VerticalOffset;

        double visibleRatio = viewportHeight / extentHeight;
        double centerOffset = verticalOffset + viewportHeight / 2;
        double centerRatio = centerOffset / extentHeight;

        double indicatorHeight = canvasHeight * visibleRatio;
        double indicatorTop = canvasHeight * centerRatio - indicatorHeight / 2;
        indicatorTop = Math.Max(0, Math.Min(indicatorTop, canvasHeight - indicatorHeight));

        ScrollIndicator.Height = Math.Max(10, indicatorHeight);
        Canvas.SetTop(ScrollIndicator, indicatorTop);
        ScrollIndicator.Visibility = Visibility.Visible;
    }

    private void UpdateEditorScrollFromIndicator()
    {
        if (_editorScroll == null || !(_editorScroll.ExtentHeight > _editorScroll.ViewportHeight))
            return;

        double indicatorTop = Canvas.GetTop(ScrollIndicator);
        double maxTop = MarkerCanvas.ActualHeight - ScrollIndicator.ActualHeight;
        double ratio = indicatorTop / maxTop;
        double newOffset = ratio * (_editorScroll.ExtentHeight - _editorScroll.ViewportHeight);
        _editorScroll.ScrollToVerticalOffset(newOffset);
    }

    private void BuildMarkers()
    {
        MarkerCanvas.Children.Clear();
        if (_editorScroll == null || TotalLines == 0)
            return;

        double h = MarkerCanvas.ActualHeight;

        for (int i = 0; i < _meta.Count; i++)
        {
            // Prioritize any type other than imaginary one
            var piece = _meta[i].New?.Type != ChangeType.Imaginary
                ? _meta[i].New
                : _meta[i].Old;
            if (piece == null || piece.Type == ChangeType.Unchanged || piece.Type == ChangeType.Imaginary)
                continue;

            var rect = new Rectangle
            {
                Width = NavExpandedWidth,
                Height = 2,
                Tag = i,
                Fill = piece.Type switch
                {
                    ChangeType.Inserted => DiffColors.Insert,
                    ChangeType.Deleted => DiffColors.Delete,
                    ChangeType.Modified => DiffColors.Modify,
                    _ => Brushes.Gray
                }
            };

            if (piece.Type == ChangeType.Inserted && _movedStrings.Contains(piece.Text) && !string.IsNullOrEmpty(piece.Text))
                rect.Fill = DiffColors.Move;

            Canvas.SetTop(rect, i * h / TotalLines);
            MarkerCanvas.Children.Add(rect);
        }

        MarkerCanvas.Children.Add(ScrollIndicator); // Important because I clear children
        Panel.SetZIndex(ScrollIndicator, int.MaxValue);
    }

    private void OnCanvasClick(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(MarkerCanvas);
        int line = (int) (pos.Y / MarkerCanvas.ActualHeight * TotalLines);
        LineClicked?.Invoke(line);
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleNavbar();
    }

    private void ToggleNavbar()
    {
        double from = _isExpanded ? NavExpandedWidth : NavCollapsedWidth;
        double to = _isExpanded ? NavCollapsedWidth : NavExpandedWidth;

        var anim = new DoubleAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(300)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        NavContent.BeginAnimation(WidthProperty, anim);

        double spacerFrom = _isExpanded ? 0 : 16;
        double spacerTo = _isExpanded ? 16 : 0;

        var spacerAnim = new DoubleAnimation(spacerFrom, spacerTo, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Spacer.BeginAnimation(WidthProperty, spacerAnim);

        double fromAngle = _isExpanded ? 180 : 0;
        double toAngle = _isExpanded ? 0 : 180;

        var rotateAnim = new DoubleAnimation(fromAngle, toAngle, new Duration(TimeSpan.FromMilliseconds(300)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        ArrowRotateTransform.BeginAnimation(RotateTransform.AngleProperty, rotateAnim);

        _isExpanded = !_isExpanded;
    }
}