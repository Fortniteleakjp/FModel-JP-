using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdonisUI.Controls;
using CUE4Parse.FileProvider.Objects;
using FModel.Services;
using FModel.ViewModels;

namespace FModel.Views;

/// <summary>
/// Interaction logic for BlueprintGraphWindow.xaml
/// Shows every function of a blueprint as a node graph rebuilt from its bytecode, the EventGraph first.
/// </summary>
public partial class BlueprintGraphWindow : AdonisWindow
{
    private const double _MIN_ZOOM = 0.1;
    private const double _MAX_ZOOM = 2.5;

    private readonly BlueprintGraph _graph;
    private BlueprintFunctionGraph _current;
    private BlueprintGraphNode _selected;
    private List<BlueprintGraphNode> _matches = [];
    private int _matchIndex = -1;

    private Point? _panOrigin;
    private Point _panScroll;

    public BlueprintGraphWindow(BlueprintGraph graph)
    {
        _graph = graph;

        InitializeComponent();

        Title = $"{TryFindResource("UI_BlueprintGraph") as string ?? "Blueprint Graph"} - {graph.Name}";
        ClassText.Text = string.IsNullOrEmpty(graph.SuperName) ? graph.Name : $"{graph.Name} : {graph.SuperName}";
        ClassText.ToolTip = graph.PackagePath;

        FunctionList.ItemsSource = graph.Functions;
        if (graph.Functions.Count > 0) FunctionList.SelectedIndex = 0;
        else NoteText.Text = graph.Note;

        SetStatus(graph.Note);
    }

    /// <summary>
    /// Reads the blueprint class of <paramref name="entry"/>. Returns null when the asset holds none.
    /// Call off the UI thread, the window itself must then be created on it.
    /// </summary>
    public static BlueprintGraph Load(GameFile entry, CancellationToken cancellationToken)
    {
        var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;

        // the editor only twin carries no bytecode, the graph is built from the regular package
        if (entry.Path.EndsWith(".o.uasset", StringComparison.OrdinalIgnoreCase) &&
            provider.Files.TryGetValue($"{entry.PathWithoutExtension[..^2]}.uasset", out var runtimeTwin))
            entry = runtimeTwin;

        var package = provider.LoadPackage(entry);
        var blueprint = BlueprintGraphBuilder.FindClass(package);
        return blueprint == null
            ? null
            : BlueprintGraphBuilder.Build(blueprint, entry.Path, provider.ReadScriptData, EditorNames(provider, entry.PathWithoutExtension), cancellationToken,
                parent => parent.Owner?.Name is { } parentPackage ? EditorNames(provider, parentPackage) : null);
    }

    /// <summary>
    /// Display names of the blueprint's own functions and variables, kept in the CookedClassMetaData of its .o.uasset.
    /// </summary>
    private static BlueprintEditorNames EditorNames(CUE4Parse.FileProvider.IFileProvider provider, string pathWithoutExtension)
    {
        try
        {
            if (provider.TryLoadPackage($"{pathWithoutExtension}.o.uasset", out var editorPackage) &&
                editorPackage.GetExportOrNull("CookedClassMetaData") is CUE4Parse.UE4.Objects.UObject.Editor.UClassCookedMetaData metaData)
                return BlueprintEditorNames.From(metaData);
        }
        catch (Exception)
        {
            // no optional segment for this package
        }

        return new BlueprintEditorNames();
    }

    private void OnFunctionFilterChanged(object sender, TextChangedEventArgs e)
    {
        var filter = FunctionFilter.Text.Trim();
        FunctionList.ItemsSource = filter.Length == 0
            ? _graph.Functions
            : _graph.Functions.Where(f => f.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void OnFunctionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (FunctionList.SelectedItem is BlueprintFunctionGraph function && function != _current)
            ShowFunction(function);
    }

    private void ShowFunction(BlueprintFunctionGraph function)
    {
        _current = function;
        Select(null);
        ClearMatches();

        GraphRoot.Width = function.CanvasWidth;
        GraphRoot.Height = function.CanvasHeight;
        EdgeItems.ItemsSource = function.Edges;
        NodeItems.ItemsSource = function.Nodes;

        NoteText.Text = $"{function.Signature}  |  {function.Note}";
        CodeText.Text = function.Signature;
        GraphScroll.ScrollToHome();

        if (!string.IsNullOrWhiteSpace(NodeSearch.Text)) FindNodes(NodeSearch.Text);
        SetStatus($"{function.Nodes.Count} nodes, {function.Edges.Count} wires  |  {_graph.Note}");
    }

    /// <summary>
    /// Opens a function by name and, when given, centers the statement at <paramref name="offset"/>.
    /// </summary>
    private void JumpTo(string functionName, int offset)
    {
        // a parent blueprint's graph calls into its own functions first, the child's otherwise
        var function = _graph.Functions.Where(f => f.Name == functionName)
            .OrderByDescending(f => f.InheritedFrom == _current?.InheritedFrom).FirstOrDefault();
        if (function == null)
        {
            SetStatus($"{functionName} is not a function of {_graph.Name}");
            return;
        }

        if (!FunctionList.Items.Contains(function)) FunctionFilter.Text = string.Empty;
        FunctionList.SelectedItem = function;
        FunctionList.ScrollIntoView(function);
        if (function != _current) ShowFunction(function);

        var target = offset >= 0 ? function.FindByOffset(offset) : null;
        if (target == null) return;

        Select(target);
        Dispatcher.BeginInvoke(() => CenterOn(target), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnNodeClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BlueprintGraphNode node }) return;

        e.Handled = true; // no panning from a card
        Select(node);

        if (e.ClickCount >= 2 && node.CanJump)
            JumpTo(node.TargetFunction, node.TargetOffset);
    }

    private void Select(BlueprintGraphNode node)
    {
        if (_selected != null) _selected.IsSelected = false;
        _selected = node;
        if (node == null) return;

        node.IsSelected = true;
        CodeText.Text = node.Code;
        if (node.CanJump) SetStatus($"double click to open {node.TargetFunction}");
    }

    private void CenterOn(BlueprintGraphNode node)
    {
        var scale = GraphScale.ScaleX;
        GraphScroll.ScrollToHorizontalOffset(Math.Max(0, (node.X + node.Width / 2) * scale - GraphScroll.ViewportWidth / 2));
        GraphScroll.ScrollToVerticalOffset(Math.Max(0, (node.Y + node.Height / 2) * scale - GraphScroll.ViewportHeight / 2));
    }

    private void OnNodeSearchChanged(object sender, TextChangedEventArgs e) => FindNodes(NodeSearch.Text);

    private void OnNodeSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _matches.Count == 0) return;

        _matchIndex = (_matchIndex + (Keyboard.Modifiers == ModifierKeys.Shift ? _matches.Count - 1 : 1)) % _matches.Count;
        var node = _matches[_matchIndex];
        Select(node);
        CenterOn(node);
        SetStatus($"match {_matchIndex + 1} / {_matches.Count}");
        e.Handled = true;
    }

    private void FindNodes(string text)
    {
        ClearMatches();
        if (_current == null) return;
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus(_graph.Note);
            return;
        }

        text = text.Trim();
        _matches = _current.Nodes.Where(n =>
            n.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            (n.Subtitle?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) ||
            n.Code.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var node in _matches) node.IsMatch = true;
        SetStatus(_matches.Count == 0 ? "no match" : $"{_matches.Count} matches, press Enter to go through them");
    }

    private void ClearMatches()
    {
        foreach (var node in _matches) node.IsMatch = false;
        _matches = [];
        _matchIndex = -1;
    }

    private void OnPanStart(object sender, MouseButtonEventArgs e)
    {
        _panOrigin = e.GetPosition(GraphScroll);
        _panScroll = new Point(GraphScroll.HorizontalOffset, GraphScroll.VerticalOffset);
        GraphRoot.CaptureMouse();
        GraphRoot.Cursor = Cursors.SizeAll;
    }

    private void OnPanMove(object sender, MouseEventArgs e)
    {
        if (_panOrigin is not { } origin) return;

        var position = e.GetPosition(GraphScroll);
        GraphScroll.ScrollToHorizontalOffset(_panScroll.X - (position.X - origin.X));
        GraphScroll.ScrollToVerticalOffset(_panScroll.Y - (position.Y - origin.Y));
    }

    private void OnPanEnd(object sender, MouseButtonEventArgs e)
    {
        _panOrigin = null;
        GraphRoot.ReleaseMouseCapture();
        GraphRoot.Cursor = null;
    }

    private void OnGraphMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;

        Zoom(e.Delta > 0 ? 1.1 : 1 / 1.1);
        e.Handled = true;
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e) => Zoom(1.1);

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => Zoom(1 / 1.1);

    private void OnResetZoomClick(object sender, RoutedEventArgs e) => SetZoom(1);

    private void OnFitClick(object sender, RoutedEventArgs e)
    {
        if (_current == null || GraphScroll.ViewportWidth <= 0) return;

        SetZoom(Math.Min(GraphScroll.ViewportWidth / _current.CanvasWidth, GraphScroll.ViewportHeight / _current.CanvasHeight));
        GraphScroll.ScrollToHome();
    }

    private void Zoom(double factor) => SetZoom(GraphScale.ScaleX * factor);

    private void SetZoom(double scale)
    {
        scale = Math.Clamp(scale, _MIN_ZOOM, _MAX_ZOOM);
        GraphScale.ScaleX = GraphScale.ScaleY = scale;
        SetStatus($"zoom {scale:0.##}x");
    }

    private void SetStatus(string status) => Status.Text = status ?? string.Empty;
}
