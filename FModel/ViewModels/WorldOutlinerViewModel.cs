using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using FModel.Framework;

namespace FModel.ViewModels;

/// <summary>
/// An entry of the world outliner: either an actor or the class folder grouping them.
/// </summary>
public class WorldActorNode : ViewModel
{
    public string Name { get; init; }
    public string ClassName { get; init; }

    /// <summary>Formatted location of the actor's root component, empty when it has none.</summary>
    public string Location { get; init; }

    public string Rotation { get; init; }
    public string Scale { get; init; }

    /// <summary>Export name used to jump into the json tab, null for class folders.</summary>
    public string ObjectName { get; init; }

    public List<WorldActorNode> Children { get; } = [];
    public List<KeyValuePair<string, string>> Properties { get; } = [];

    public bool IsFolder => ObjectName == null;
    public string Hint => IsFolder ? $"({Children.Count})" : Location;

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool Matches(string filter) =>
        (Name != null && Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
        (ClassName != null && ClassName.Contains(filter, StringComparison.OrdinalIgnoreCase));

    public WorldActorNode Filtered(string filter)
    {
        var kept = Children.Select(child => child.Filtered(filter)).Where(child => child != null).ToList();
        if (kept.Count == 0 && !Matches(filter)) return null;

        var copy = new WorldActorNode
        {
            Name = Name, ClassName = ClassName, Location = Location, Rotation = Rotation,
            Scale = Scale, ObjectName = ObjectName, IsExpanded = true
        };
        copy.Children.AddRange(kept);
        copy.Properties.AddRange(Properties);
        return copy;
    }
}

public class WorldOutline
{
    public string WorldName { get; init; }
    public string LevelName { get; init; }
    public string PackagePath { get; init; }
    public int ActorCount { get; init; }
    public List<WorldActorNode> Roots { get; init; } = [];
    public List<string> StreamingLevels { get; init; } = [];
}

/// <summary>
/// Reads a cooked level and lists its actors grouped by class, the way the editor's outliner does.
/// Attachment parenting is editor-only data and gone from cooked builds, so the class is what remains to group by.
/// </summary>
public static class WorldOutlineBuilder
{
    /// <summary>Properties worth showing first in the details panel.</summary>
    private static readonly string[] _interestingProperties =
        ["StaticMesh", "SkeletalMesh", "Mesh", "Materials", "OverrideMaterials", "LightmassSettings", "Tags", "ActorLabel"];

    public static bool IsWorld(IPackage package) =>
        package.GetExports().Any(export => export is UWorld || export.ExportType == "World" || export.ExportType == "Level");

    public static WorldOutline Build(IPackage package, string packagePath, Action<string> progress, CancellationToken cancellationToken)
    {
        var exports = package.GetExports().ToArray();
        var world = exports.OfType<UWorld>().FirstOrDefault();

        var level = ResolveLevel(world) ?? exports.OfType<ULevel>().FirstOrDefault();
        var actors = level?.Actors ?? [];

        var groups = new Dictionary<string, WorldActorNode>(StringComparer.Ordinal);
        var count = 0;

        for (var i = 0; i < actors.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (actors[i] is not { } index || index.IsNull) continue;

            UObject actor;
            try
            {
                if (!index.TryLoad(out actor) || actor == null) continue;
            }
            catch (Exception)
            {
                continue; // a single unreadable actor should not kill the outline
            }

            if (i % 200 == 0) progress?.Invoke($"{i} / {actors.Length}");

            var node = BuildActor(actor);
            if (!groups.TryGetValue(node.ClassName, out var group))
            {
                group = new WorldActorNode { Name = node.ClassName, ClassName = node.ClassName };
                groups[node.ClassName] = group;
            }

            group.Children.Add(node);
            count++;
        }

        var roots = groups.Values.OrderByDescending(group => group.Children.Count).ThenBy(group => group.Name, StringComparer.Ordinal).ToList();
        foreach (var root in roots)
            root.Children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

        return new WorldOutline
        {
            WorldName = world?.Name ?? package.Name,
            LevelName = level?.Name,
            PackagePath = packagePath,
            ActorCount = count,
            Roots = roots,
            StreamingLevels = ReadStreamingLevels(world)
        };
    }

    private static ULevel ResolveLevel(UWorld world)
    {
        if (world?.PersistentLevel == null || world.PersistentLevel.IsNull) return null;

        try
        {
            return world.PersistentLevel.TryLoad(out ULevel level) ? level : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<string> ReadStreamingLevels(UWorld world)
    {
        var levels = new List<string>();
        if (world?.StreamingLevels == null) return levels;

        foreach (var index in world.StreamingLevels)
        {
            if (index == null || index.IsNull) continue;

            try
            {
                levels.Add(index.TryLoad(out var streaming) && streaming != null
                    ? streaming.GetOrDefault<FSoftObjectPath>("WorldAsset").AssetPathName.Text
                    : index.Name);
            }
            catch (Exception)
            {
                levels.Add(index.Name);
            }
        }

        return levels;
    }

    private static WorldActorNode BuildActor(UObject actor)
    {
        string location = null, rotation = null, scale = null;
        try
        {
            if (actor.TryGetValue(out FPackageIndex root, "RootComponent") && root.TryLoad(out var component) && component != null)
            {
                if (component.TryGetValue(out FVector relativeLocation, "RelativeLocation"))
                    location = Format(relativeLocation);
                if (component.TryGetValue(out FRotator relativeRotation, "RelativeRotation"))
                    rotation = $"P {relativeRotation.Pitch:0.##}  Y {relativeRotation.Yaw:0.##}  R {relativeRotation.Roll:0.##}";
                if (component.TryGetValue(out FVector relativeScale, "RelativeScale3D"))
                    scale = Format(relativeScale);
            }
        }
        catch (Exception)
        {
            // components of a cooked actor are not guaranteed to be readable
        }

        var node = new WorldActorNode
        {
            Name = actor.Name,
            ClassName = actor.ExportType,
            ObjectName = actor.Name,
            Location = location,
            Rotation = rotation,
            Scale = scale
        };

        foreach (var property in actor.Properties.OrderBy(p => Array.IndexOf(_interestingProperties, p.Name.Text) is var i && i >= 0 ? i : int.MaxValue))
            node.Properties.Add(new KeyValuePair<string, string>(property.Name.Text, TableDocumentBuilder.Format(property.Tag)));

        return node;
    }

    private static string Format(FVector vector) =>
        $"X {vector.X.ToString("0.##", CultureInfo.InvariantCulture)}  " +
        $"Y {vector.Y.ToString("0.##", CultureInfo.InvariantCulture)}  " +
        $"Z {vector.Z.ToString("0.##", CultureInfo.InvariantCulture)}";
}
