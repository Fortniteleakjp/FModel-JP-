using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdonisUI.Controls;
using CUE4Parse.FileProvider.Objects;
using FModel.Services;
using FModel.ViewModels;
using FModel.Views.Resources.Controls;

namespace FModel.Views;

/// <summary>
/// Interaction logic for MaterialGraphWindow.xaml
/// Draws a material as a node graph: the expression graph when the asset kept its editor data,
/// otherwise the instance chain with the parameters each level overrides.
/// </summary>
public partial class MaterialGraphWindow : AdonisWindow
{
    private const double _MIN_ZOOM = 0.3;
    private const double _MAX_ZOOM = 2.5;

    private readonly MaterialGraph _graph;

    public MaterialGraphWindow(MaterialGraph graph)
    {
        _graph = graph;

        InitializeComponent();

        Title = $"{TryFindResource("UI_MaterialGraph") as string ?? "Material Graph"} - {graph.Name}";
        NoteText.Text = graph.Note;

        GraphRoot.Width = graph.CanvasWidth;
        GraphRoot.Height = graph.CanvasHeight;
        EdgeItems.ItemsSource = graph.Edges;
        NodeItems.ItemsSource = graph.Nodes;

        SetStatus($"{graph.Nodes.Count} nodes, {graph.Edges.Count} links" +
                  (graph.HasExpressions ? " - expression graph" : " - instance chain"));
    }

    /// <summary>
    /// Reads the material of <paramref name="entry"/>. Returns null when the asset holds none.
    /// Call off the UI thread, the window itself must then be created on it.
    /// </summary>
    public static MaterialGraph Load(GameFile entry, CancellationToken cancellationToken)
    {
        var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
        var package = provider.LoadPackage(entry);
        if (!MaterialGraphBuilder.IsMaterial(package)) return null;

        return MaterialGraphBuilder.Build(package, entry.Path, cancellationToken);
    }

    /// <summary>
    /// Clicking a card that points at an asset opens it in a regular FModel tab.
    /// </summary>
    private void OnNodeClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2 || sender is not FrameworkElement { DataContext: MaterialGraphNode node } || !node.CanOpen)
            return;

        var cue4Parse = ApplicationService.ApplicationView.CUE4Parse;
        var path = node.AssetPath;
        if (!cue4Parse.Provider.Files.TryGetValue(path, out var entry) &&
            !cue4Parse.Provider.Files.TryGetValue($"{path}.uasset", out entry))
        {
            SetStatus($"{path} is not part of the loaded build");
            return;
        }

        ApplicationService.ThreadWorkerView.Begin(cancellationToken => cue4Parse.Extract(cancellationToken, entry, true));
    }

    private void OnGraphMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;

        Zoom(e.Delta > 0 ? 1.1 : 1 / 1.1);
        e.Handled = true;
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e) => Zoom(1.1);

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => Zoom(1 / 1.1);

    private void OnResetZoomClick(object sender, RoutedEventArgs e)
    {
        GraphScale.ScaleX = GraphScale.ScaleY = 1;
        SetStatus($"{_graph.Nodes.Count} nodes, {_graph.Edges.Count} links");
    }

    private void Zoom(double factor)
    {
        var scale = Math.Clamp(GraphScale.ScaleX * factor, _MIN_ZOOM, _MAX_ZOOM);
        GraphScale.ScaleX = GraphScale.ScaleY = scale;
        SetStatus($"{_graph.Nodes.Count} nodes, {_graph.Edges.Count} links - zoom {scale:0.##}x");
    }

    private void SetStatus(string status) => Status.Text = status ?? string.Empty;
}
