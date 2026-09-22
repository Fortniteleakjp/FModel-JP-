using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using AdonisUI.Controls;
using CUE4Parse.FileProvider.Objects;
using FModel.Services;
using FModel.ViewModels;

namespace FModel.Views;

/// <summary>
/// Interaction logic for WorldOutlinerWindow.xaml
/// Lists the actors of a cooked level, grouped by class, with their transform and properties.
/// </summary>
public partial class WorldOutlinerWindow : AdonisWindow
{
    private readonly WorldOutline _outline;
    private WorldActorNode _selected;

    public WorldOutlinerWindow(WorldOutline outline)
    {
        _outline = outline;

        InitializeComponent();

        Title = $"{TryFindResource("UI_WorldOutliner") as string ?? "World Outliner"} - {outline.WorldName}";
        ActorTree.ItemsSource = _outline.Roots;

        if (_outline.StreamingLevels.Count > 0)
        {
            StreamingExpander.Visibility = Visibility.Visible;
            StreamingList.ItemsSource = _outline.StreamingLevels;
        }

        SetStatus($"{_outline.ActorCount} actors in {_outline.Roots.Count} classes" +
                  (string.IsNullOrEmpty(_outline.LevelName) ? string.Empty : $" - {_outline.LevelName}"));
    }

    /// <summary>
    /// Reads the level of <paramref name="entry"/>. Returns null when the asset is not a level.
    /// Call off the UI thread, the window itself must then be created on it.
    /// </summary>
    public static WorldOutline Load(GameFile entry, Action<string> progress, CancellationToken cancellationToken)
    {
        var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
        var package = provider.LoadPackage(entry);
        if (!WorldOutlineBuilder.IsWorld(package)) return null;

        return WorldOutlineBuilder.Build(package, entry.Path, progress, cancellationToken);
    }

    private void OnClosing(object sender, CancelEventArgs e) { }

    private void OnFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        var filter = FilterBox.Text;
        if (string.IsNullOrWhiteSpace(filter))
        {
            ActorTree.ItemsSource = _outline.Roots;
            SetStatus($"{_outline.ActorCount} actors in {_outline.Roots.Count} classes");
            return;
        }

        var filtered = new List<WorldActorNode>();
        foreach (var root in _outline.Roots)
        {
            var kept = root.Filtered(filter);
            if (kept != null) filtered.Add(kept);
        }

        ActorTree.ItemsSource = filtered;
        SetStatus($"{filtered.Sum(group => group.Children.Count)} matching actors");
    }

    private void OnActorSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selected = e.NewValue as WorldActorNode;
        OpenJsonButton.IsEnabled = _selected is { IsFolder: false };

        if (_selected == null)
        {
            ActorTitle.Text = string.Empty;
            ActorTransform.Text = string.Empty;
            PropertyGrid.ItemsSource = null;
            return;
        }

        ActorTitle.Text = _selected.IsFolder ? $"{_selected.Name} ({_selected.Children.Count})" : $"{_selected.Name}  -  {_selected.ClassName}";
        ActorTransform.Text = string.Join("   |   ", new[]
        {
            _selected.Location, _selected.Rotation, _selected.Scale
        }.Where(part => !string.IsNullOrEmpty(part)));

        PropertyGrid.ItemsSource = _selected.Properties;
    }

    /// <summary>
    /// Opens the level's json in a tab and scrolls to the selected actor.
    /// </summary>
    private void OnOpenJsonClick(object sender, RoutedEventArgs e)
    {
        if (_selected is not { IsFolder: false } actor) return;

        var cue4Parse = ApplicationService.ApplicationView.CUE4Parse;
        ApplicationService.ThreadWorkerView.Begin(cancellationToken =>
            cue4Parse.ExtractAndScroll(cancellationToken, _outline.PackagePath, actor.ObjectName));
    }

    private void SetStatus(string status) => Status.Text = status ?? string.Empty;
}
