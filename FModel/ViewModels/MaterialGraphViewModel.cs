using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Material.Editor;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;

namespace FModel.ViewModels;

public enum EMaterialNodeKind
{
    BaseMaterial,
    Instance,
    Scalar,
    Vector,
    Texture,
    Switch,
    Mask,
    Expression
}

/// <summary>
/// A card of the material graph, already laid out.
/// </summary>
public class MaterialGraphNode
{
    public string Id { get; init; }
    public string Title { get; init; }
    public string Subtitle { get; init; }
    public string Detail { get; init; }
    public EMaterialNodeKind Kind { get; init; }

    /// <summary>Asset this node points at, used to open it from the graph.</summary>
    public string AssetPath { get; init; }

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 260;
    public double Height { get; set; } = 62;

    public bool IsParameter => Kind is EMaterialNodeKind.Scalar or EMaterialNodeKind.Vector or EMaterialNodeKind.Texture or EMaterialNodeKind.Switch or EMaterialNodeKind.Mask;
    public bool CanOpen => !string.IsNullOrEmpty(AssetPath);
}

public class MaterialGraphEdge
{
    public string Label { get; init; }

    /// <summary>Bezier from the right edge of the source card to the left edge of the target card.</summary>
    public string Geometry { get; init; }

    public double LabelX { get; init; }
    public double LabelY { get; init; }
}

public class MaterialGraph
{
    public string Name { get; init; }
    public string PackagePath { get; init; }
    public List<MaterialGraphNode> Nodes { get; init; } = [];
    public List<MaterialGraphEdge> Edges { get; init; } = [];
    public double CanvasWidth { get; set; }
    public double CanvasHeight { get; set; }

    /// <summary>True when the asset still carries editor expression data, which cooked builds normally strip.</summary>
    public bool HasExpressions { get; init; }

    /// <summary>True when part of the graph was read from the sibling .o.uasset of a material.</summary>
    public bool UsesEditorOnlyData { get; init; }

    public string Note { get; init; }
}

/// <summary>
/// Resolves the editor only twin of a material export, the &lt;name&gt;EditorOnlyData export of its
/// sibling .o.uasset. Returns null when the build ships no optional segment for that package.
/// </summary>
public delegate UObject MaterialEditorOnlyDataResolver(UObject export);

/// <summary>
/// Builds a material graph out of what a package holds.
/// The runtime export of a cooked material keeps no expression graph and only a trimmed parameter set,
/// both live in the optional segment next to it, so the .o.uasset is read whenever the build ships one.
/// Without it the inheritance chain and its parameter overrides are drawn instead, which is the part
/// the editor shows in the instance editor.
/// </summary>
public static class MaterialGraphBuilder
{
    private const double _COLUMN_WIDTH = 320;
    private const double _ROW_HEIGHT = 84;
    private const double _MARGIN = 40;
    private const int _MAX_CHAIN = 8;
    private const int _MAX_EXPRESSIONS = 400;

    public static bool IsMaterial(IPackage package) =>
        package.GetExports().Any(export => export is UUnrealMaterial);

    public static MaterialGraph Build(IPackage package, string packagePath, MaterialEditorOnlyDataResolver editorOnlyData,
        CancellationToken cancellationToken)
    {
        var material = package.GetExports().OfType<UUnrealMaterial>().FirstOrDefault();
        if (material == null) return null;

        editorOnlyData ??= static _ => null;

        var expressions = ReadExpressions(material, editorOnlyData(material));
        return expressions.Length > 0
            ? BuildExpressionGraph(material, expressions, packagePath, editorOnlyData, cancellationToken)
            : BuildChainGraph(material, packagePath, editorOnlyData, cancellationToken);
    }

    /// <summary>
    /// The expression graph is editor only data. A cooked package keeps it in its .o.uasset, laid out flat
    /// under 5.0 and moved into the ExpressionCollection struct from 5.1 on.
    /// </summary>
    private static FPackageIndex[] ReadExpressions(UUnrealMaterial material, UObject editorOnly)
    {
        if (material is UMaterial { Expressions.Length: > 0 } uncooked) return uncooked.Expressions;
        if (editorOnly == null) return [];

        if (editorOnly.TryGetValue(out FPackageIndex[] expressions, "Expressions") && expressions.Length > 0)
            return expressions;

        return editorOnly.TryGetValue(out FStructFallback collection, "ExpressionCollection") &&
               collection.TryGetValue(out expressions, "Expressions")
            ? expressions
            : [];
    }

    /// <summary>
    /// Instance chain: base material on the left, each child instance to its right, with the
    /// parameters that instance overrides stacked underneath it.
    /// </summary>
    private static MaterialGraph BuildChainGraph(UUnrealMaterial material, string packagePath,
        MaterialEditorOnlyDataResolver editorOnlyData, CancellationToken cancellationToken)
    {
        var chain = new List<UUnrealMaterial>();
        var current = material;
        while (current != null && chain.Count < _MAX_CHAIN)
        {
            chain.Add(current);
            current = (current as UMaterialInstance)?.Parent?.Load<UUnrealMaterial>();
        }

        chain.Reverse(); // base material first

        var nodes = new List<MaterialGraphNode>();
        var edges = new List<MaterialGraphEdge>();
        var maxRows = 0;
        var usesEditorOnlyData = false;

        MaterialGraphNode previous = null;
        for (var column = 0; column < chain.Count; column++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = chain[column];
            var editorOnly = editorOnlyData(entry);
            usesEditorOnlyData |= editorOnly != null;

            var node = new MaterialGraphNode
            {
                Id = $"mat{column}",
                Title = entry.Name,
                Subtitle = entry.ExportType,
                Detail = Describe(entry),
                Kind = entry is UMaterialInstance ? EMaterialNodeKind.Instance : EMaterialNodeKind.BaseMaterial,
                AssetPath = AssetPathOf(entry),
                Height = 76,
                X = _MARGIN + column * _COLUMN_WIDTH,
                Y = _MARGIN
            };

            nodes.Add(node);
            if (previous != null) edges.Add(Connect(previous, node, "parent of"));
            previous = node;

            var row = 1;
            foreach (var parameter in ReadParameters(entry, editorOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var parameterNode = new MaterialGraphNode
                {
                    Id = $"mat{column}p{row}",
                    Title = parameter.Name,
                    Subtitle = parameter.Kind.ToString(),
                    Detail = parameter.Value,
                    Kind = parameter.Kind,
                    AssetPath = parameter.AssetPath,
                    X = node.X,
                    Y = _MARGIN + row * _ROW_HEIGHT + 30
                };

                nodes.Add(parameterNode);
                edges.Add(Connect(parameterNode, node, "feeds"));
                row++;
            }

            maxRows = Math.Max(maxRows, row);
        }

        var note = chain.Count > 1
            ? $"{chain.Count} materials in the inheritance chain"
            : "no expression graph in this package";
        if (usesEditorOnlyData) note += "  |  editor only data merged from .o.uasset";

        var graph = new MaterialGraph
        {
            Name = material.Name,
            PackagePath = packagePath,
            Nodes = nodes,
            Edges = edges,
            HasExpressions = false,
            UsesEditorOnlyData = usesEditorOnlyData,
            Note = note
        };

        graph.CanvasWidth = _MARGIN * 2 + Math.Max(1, chain.Count) * _COLUMN_WIDTH;
        graph.CanvasHeight = _MARGIN * 2 + Math.Max(2, maxRows + 1) * _ROW_HEIGHT;
        return graph;
    }

    /// <summary>
    /// Real expression graph, only reachable on assets that kept their editor data.
    /// Links are found by following every object reference an expression holds.
    /// </summary>
    private static MaterialGraph BuildExpressionGraph(UUnrealMaterial material, FPackageIndex[] expressions, string packagePath,
        MaterialEditorOnlyDataResolver editorOnlyData, CancellationToken cancellationToken)
    {
        var fromEditorOnlyData = material is not UMaterial { Expressions.Length: > 0 };

        var loaded = new Dictionary<UObject, MaterialGraphNode>();
        var nodes = new List<MaterialGraphNode>();
        var edges = new List<MaterialGraphEdge>();

        var index = 0;
        foreach (var pointer in expressions.Take(_MAX_EXPRESSIONS))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pointer == null || pointer.IsNull) continue;

            try
            {
                if (!pointer.TryLoad(out var expression) || expression == null) continue;

                var node = new MaterialGraphNode
                {
                    Id = $"expr{index++}",
                    Title = expression.ExportType,
                    Subtitle = expression.Name,
                    Detail = DescribeExpression(expression),
                    Kind = EMaterialNodeKind.Expression
                };

                loaded[expression] = node;
                nodes.Add(node);
            }
            catch (Exception)
            {
                // an expression that fails to load is simply left out
            }
        }

        var result = new MaterialGraphNode
        {
            Id = "result",
            Title = material.Name,
            Subtitle = material.ExportType,
            Detail = Describe(material),
            Kind = EMaterialNodeKind.BaseMaterial,
            AssetPath = AssetPathOf(material),
            Height = 76
        };
        nodes.Add(result);

        // positions first, the edges are drawn between the laid out cards
        Layout(nodes, loaded.Count);
        edges.AddRange(BuildExpressionEdges(loaded, cancellationToken));

        var graph = new MaterialGraph
        {
            Name = material.Name,
            PackagePath = packagePath,
            Nodes = nodes,
            Edges = edges,
            HasExpressions = true,
            UsesEditorOnlyData = fromEditorOnlyData,
            Note = fromEditorOnlyData
                ? $"{loaded.Count} expressions read from .o.uasset"
                : $"{loaded.Count} expressions"
        };

        graph.CanvasWidth = nodes.Count == 0 ? 600 : nodes.Max(n => n.X + n.Width) + _MARGIN;
        graph.CanvasHeight = nodes.Count == 0 ? 400 : nodes.Max(n => n.Y + n.Height) + _MARGIN;
        return graph;
    }

    /// <summary>
    /// Once every node has a position the edges can be drawn, source expressions on the left of their consumer.
    /// </summary>
    private static List<MaterialGraphEdge> BuildExpressionEdges(Dictionary<UObject, MaterialGraphNode> loaded, CancellationToken cancellationToken)
    {
        var edges = new List<MaterialGraphEdge>();
        foreach (var (expression, node) in loaded)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var property in expression.Properties)
            {
                foreach (var target in ReferencedObjects(property.Tag))
                {
                    if (target == expression || !loaded.TryGetValue(target, out var targetNode)) continue;

                    edges.Add(Connect(targetNode, node, property.Name.Text));
                }
            }
        }

        return edges;
    }

    private static void Layout(List<MaterialGraphNode> nodes, int expressionCount)
    {
        var perColumn = Math.Max(6, (int) Math.Ceiling(Math.Sqrt(Math.Max(1, expressionCount))));
        var column = 0;
        var row = 0;

        foreach (var node in nodes)
        {
            node.X = _MARGIN + column * _COLUMN_WIDTH;
            node.Y = _MARGIN + row * _ROW_HEIGHT;

            if (++row < perColumn) continue;

            row = 0;
            column++;
        }
    }

    private static MaterialGraphEdge Connect(MaterialGraphNode from, MaterialGraphNode to, string label)
    {
        var x1 = from.X + from.Width;
        var y1 = from.Y + from.Height / 2;
        var x2 = to.X;
        var y2 = to.Y + to.Height / 2;
        var controlOffset = Math.Max(40, Math.Abs(x2 - x1) / 2);

        return new MaterialGraphEdge
        {
            Label = label,
            Geometry = string.Create(CultureInfo.InvariantCulture,
                $"M {x1},{y1} C {x1 + controlOffset},{y1} {x2 - controlOffset},{y2} {x2},{y2}"),
            LabelX = (x1 + x2) / 2 - 30,
            LabelY = (y1 + y2) / 2 - 16
        };
    }

    private readonly record struct MaterialParameter(string Name, string Value, EMaterialNodeKind Kind, string AssetPath);

    private static IEnumerable<MaterialParameter> ReadParameters(UUnrealMaterial material, UObject editorOnly)
    {
        var parameters = new List<MaterialParameter>();

        foreach (var entry in ReadParameterArray(material, editorOnly, "ScalarParameterValues"))
        {
            var value = entry.Struct.GetOrDefault<float>("ParameterValue");
            parameters.Add(new MaterialParameter(entry.Name, value.ToString("0.###", CultureInfo.InvariantCulture), EMaterialNodeKind.Scalar, null));
        }

        foreach (var entry in ReadParameterArray(material, editorOnly, "VectorParameterValues"))
        {
            var value = entry.Struct.GetOrDefault<FLinearColor>("ParameterValue");
            parameters.Add(new MaterialParameter(entry.Name,
                string.Create(CultureInfo.InvariantCulture, $"R {value.R:0.###}  G {value.G:0.###}  B {value.B:0.###}  A {value.A:0.###}"),
                EMaterialNodeKind.Vector, null));
        }

        foreach (var entry in ReadParameterArray(material, editorOnly, "TextureParameterValues"))
        {
            string name = null, path = null;
            if (entry.Struct.TryGetValue(out FPackageIndex texture, "ParameterValue") && !texture.IsNull)
            {
                name = texture.Name;
                path = texture.ResolvedObject?.GetPathName();
            }

            parameters.Add(new MaterialParameter(entry.Name, name ?? "None", EMaterialNodeKind.Texture, path));
        }

        var staticParameters = ReadStaticParameters(material, editorOnly);
        if (staticParameters != null)
        {
            foreach (var entry in staticParameters.StaticSwitchParameters ?? [])
            {
                if (string.IsNullOrEmpty(entry.Name) || entry.Name == "None") continue;

                parameters.Add(new MaterialParameter(entry.Name, entry.Value ? "true" : "false", EMaterialNodeKind.Switch, null));
            }

            foreach (var entry in staticParameters.StaticComponentMaskParameters ?? [])
            {
                if (string.IsNullOrEmpty(entry.Name) || entry.Name == "None") continue;

                var mask = $"{(entry.R ? 'R' : '-')}{(entry.G ? 'G' : '-')}{(entry.B ? 'B' : '-')}{(entry.A ? 'A' : '-')}";
                parameters.Add(new MaterialParameter(entry.Name, mask, EMaterialNodeKind.Mask, null));
            }
        }

        return parameters.OrderBy(parameter => parameter.Kind).ThenBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase);
    }

    private readonly record struct ParameterEntry(string Name, FStructFallback Struct);

    private static IEnumerable<ParameterEntry> ReadParameterArray(UUnrealMaterial material, UObject editorOnly, string propertyName)
    {
        var entries = new List<ParameterEntry>();
        if (!material.TryGetValue(out FStructFallback[] values, propertyName) &&
            (editorOnly == null || !editorOnly.TryGetValue(out values, propertyName)))
            return entries;

        foreach (var value in values)
        {
            var name = value.TryGetValue(out FStructFallback info, "ParameterInfo")
                ? info.GetOrDefault<FName>("Name").Text
                : value.GetOrDefault<FName>("ParameterName").Text;

            if (string.IsNullOrEmpty(name) || name == "None") continue;

            entries.Add(new ParameterEntry(name, value));
        }

        return entries;
    }

    private static FStaticParameterSet ReadStaticParameters(UUnrealMaterial material, UObject editorOnly)
    {
        if (editorOnly is UMaterialInstanceEditorOnlyData { StaticParameters: not null } data) return data.StaticParameters;
        if (editorOnly != null && editorOnly.TryGetValue(out FStructFallback fallback, "StaticParameters"))
            return new FStaticParameterSet(fallback);

        return (material as UMaterialInstance)?.StaticParameters;
    }

    /// <summary>
    /// Walks a property value and yields every object it points at, inputs of an expression included.
    /// </summary>
    private static IEnumerable<UObject> ReferencedObjects(FPropertyTagType tag)
    {
        switch (tag?.GenericValue)
        {
            case FPackageIndex index when !index.IsNull:
                UObject loaded = null;
                try
                {
                    index.TryLoad(out loaded);
                }
                catch (Exception)
                {
                    // ignored
                }

                if (loaded != null) yield return loaded;
                break;

            case FScriptStruct { StructType: FStructFallback fallback }:
                foreach (var property in fallback.Properties)
                foreach (var nested in ReferencedObjects(property.Tag))
                    yield return nested;

                break;

            case UScriptArray array:
                foreach (var element in array.Properties)
                foreach (var nested in ReferencedObjects(element))
                    yield return nested;

                break;
        }
    }

    private static string Describe(UUnrealMaterial material)
    {
        var parts = new List<string>();
        if (material is UMaterial baseMaterial)
        {
            parts.Add(baseMaterial.BlendMode.ToString());
            parts.Add(baseMaterial.ShadingModel.ToString());
            if (baseMaterial.TwoSided) parts.Add("TwoSided");
            if (baseMaterial.ReferencedTextures.Count > 0) parts.Add($"{baseMaterial.ReferencedTextures.Count} textures");
        }
        else if (material is UMaterialInstance instance)
        {
            parts.Add(instance.Parent == null ? "no parent" : $"parent: {instance.Parent.Name}");
        }

        return string.Join("  |  ", parts);
    }

    private static string DescribeExpression(UObject expression)
    {
        foreach (var candidate in new[] { "ParameterName", "Texture", "DefaultValue", "Constant", "R", "MaterialFunction" })
        {
            var property = expression.Properties.FirstOrDefault(p => p.Name.Text == candidate);
            if (property == null) continue;

            var value = TableDocumentBuilder.Format(property.Tag);
            if (!string.IsNullOrEmpty(value)) return $"{candidate}: {value}";
        }

        return string.Empty;
    }

    private static string AssetPathOf(UObject export)
    {
        try
        {
            return export.Owner?.Name;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
