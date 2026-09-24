using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FModel.ViewModels;

/// <summary>
/// What a blueprint holds besides its functions, the part a data-only blueprint is made of: the components
/// tree of the editor's Components panel (native default subobjects, then the SimpleConstructionScript of the
/// class and of its parent blueprints, with the overrides of its InheritableComponentHandler) and the class
/// defaults it changes. Each component is a node listing the values its template sets.
/// </summary>
public static partial class BlueprintGraphBuilder
{
    private const double _COMPONENT_WIDTH = 300;
    private const double _TREE_GAP_X = 60;
    private const double _TREE_GAP_Y = 20;
    private const int _MAX_PROPERTY_PINS = 16;
    private const int _DEFAULTS_PER_NODE = 30;
    private const int _MAX_DEFAULT_VALUE = 60;
    private const int _MAX_CODE_VALUE = 4000;

    private static readonly string[] _sceneProperties = ["AttachParent", "RelativeLocation", "RelativeRotation", "RelativeScale3D"];

    private sealed class ComponentEntry
    {
        /// <summary>Subobject name of a native component, variable name of a construction script one.</summary>
        public string Key { get; init; }
        public string Name { get; init; }
        public string ClassName { get; init; }
        public string Origin { get; init; }
        public UObject Template { get; init; }
        public bool IsScene { get; init; }
        public string AttachTo { get; set; }
        public List<ComponentEntry> Children { get; } = [];
    }

    private static List<BlueprintFunctionGraph> BuildClassViews(UClass blueprint, CancellationToken cancellationToken)
    {
        UObject defaults = null;
        try
        {
            if (blueprint.ClassDefaultObject is { IsNull: false } pointer) pointer.TryLoad(out defaults);
        }
        catch (Exception)
        {
            // no class default object to read
        }

        var graphs = new List<BlueprintFunctionGraph>();
        foreach (var build in new Func<BlueprintFunctionGraph>[]
                 {
                     () => ComponentsGraph(blueprint, defaults, cancellationToken),
                     () => DefaultsGraph(blueprint, defaults)
                 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (build() is { } graph) graphs.Add(graph);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // a view that fails to read is left out, the functions still show
            }
        }

        return graphs;
    }

    private static BlueprintFunctionGraph ComponentsGraph(UClass blueprint, UObject defaults, CancellationToken cancellationToken)
    {
        var components = new List<ComponentEntry>();
        var rootKey = NativeComponents(blueprint, defaults, components);
        ConstructionScriptComponents(blueprint, components, rootKey == null, cancellationToken);
        if (components.Count == 0) return null;

        ComponentEntry Find(string name) =>
            name == null ? null : components.FirstOrDefault(c => c.Key == name) ?? components.FirstOrDefault(c => c.Name == name);

        // the actor root: the native RootComponent, else the first construction script scene component
        var root = Find(rootKey) ?? components.FirstOrDefault(c => c.IsScene && c.AttachTo == null);
        var self = new ComponentEntry { Key = string.Empty, Name = $"{DisplayClassName(blueprint.Name)} (Self)", ClassName = blueprint.Name };
        var parentOf = new Dictionary<ComponentEntry, ComponentEntry>();
        foreach (var component in components)
        {
            if (component == root) continue;
            parentOf[component] = Find(component.AttachTo) ?? (component.IsScene && root != null ? root : null);
        }

        // an attachment going round in a loop is cut, the component then hangs under the actor
        foreach (var component in components)
        {
            var parent = parentOf.GetValueOrDefault(component);
            for (var depth = 0; parent != null && depth <= components.Count; depth++)
            {
                if (parent == component)
                {
                    parentOf[component] = null;
                    break;
                }

                parent = parentOf.GetValueOrDefault(parent);
            }
        }

        foreach (var component in components)
            (parentOf.GetValueOrDefault(component) ?? self).Children.Add(component);

        // the root first, then the other scene components, the way the panel lists them above the others
        var ordered = self.Children.OrderBy(c => c == root ? 0 : c.IsScene ? 1 : 2).ToList();
        self.Children.Clear();
        self.Children.AddRange(ordered);

        var nodes = new List<BlueprintGraphNode>();
        var edges = new List<BlueprintGraphEdge>();
        var cursor = _MARGIN;
        BlueprintGraphNode Place(ComponentEntry entry, int depth, bool isSelf)
        {
            var node = isSelf ? SelfNode(blueprint, entry) : ComponentNode(entry, nodes.Count);
            if (!isSelf) node.Inputs.Insert(0, new BlueprintPin { Name = "Parent" });
            if (entry.Children.Count > 0) node.Outputs.Add(new BlueprintPin { Name = "Children" });
            nodes.Add(node);

            node.X = _MARGIN + depth * (_COMPONENT_WIDTH + _TREE_GAP_X);
            node.Y = cursor;
            var end = cursor + node.Height + _TREE_GAP_Y;

            foreach (var child in entry.Children)
            {
                var childNode = Place(child, depth + 1, false);
                edges.Add(Wire(node.X + node.Width, node.OutputY(0), childNode.X, childNode.InputY(0), EBlueprintEdgeKind.Data));
            }

            cursor = Math.Max(cursor, end);
            return node;
        }

        Place(self, 0, true);

        return new BlueprintFunctionGraph
        {
            Name = "$Components",
            DisplayName = "Components",
            Signature = $"Components of {blueprint.Name}",
            IsClassView = true,
            Nodes = nodes,
            Edges = edges,
            Note = $"{components.Count} components",
            CanvasWidth = nodes.Max(n => n.X + n.Width) + _MARGIN,
            CanvasHeight = nodes.Max(n => n.Y + n.Height) + _MARGIN
        };
    }

    /// <summary>
    /// Default subobjects of the class default object (the components the native parent creates in its
    /// constructor), named after the property holding them. Returns the subobject name of the RootComponent.
    /// </summary>
    private static string NativeComponents(UClass blueprint, UObject defaults, List<ComponentEntry> components)
    {
        if (defaults == null) return null;

        var variableOf = new Dictionary<string, string>(StringComparer.Ordinal);
        string rootKey = null;
        foreach (var property in defaults.Properties)
        {
            if (property.Tag?.GenericValue is not FPackageIndex { IsNull: false } index) continue;

            var name = property.Name.Text;
            if (name == "RootComponent") rootKey = index.Name;
            else variableOf.TryAdd(index.Name, name);
        }

        foreach (var export in ExportsOf(blueprint))
        {
            if (export.Outer == null || export.Outer.Name != defaults.Name || !export.Flags.HasFlag(EObjectFlags.RF_DefaultSubObject)) continue;

            components.Add(new ComponentEntry
            {
                Key = export.Name,
                Name = variableOf.GetValueOrDefault(export.Name) ?? export.Name,
                ClassName = export.Class?.Name.Text ?? export.ExportType,
                Origin = "Native",
                Template = export,
                IsScene = export is USceneComponent || export.Name == rootKey || _sceneProperties.Any(p => export.Properties.Any(t => t.Name.Text == p)),
                AttachTo = export.GetOrDefault<FPackageIndex>("AttachParent") is { IsNull: false } parent ? parent.Name : null
            });
        }

        return rootKey != null && components.Any(c => c.Key == rootKey) ? rootKey : null;
    }

    /// <summary>
    /// Components added in the Components panel of the blueprint and of its parent blueprints. An inherited one
    /// shows the template the class's InheritableComponentHandler overrides it with.
    /// </summary>
    private static void ConstructionScriptComponents(UClass blueprint, List<ComponentEntry> components, bool noNativeRoot, CancellationToken cancellationToken)
    {
        var classes = new List<UClass> { blueprint };
        classes.AddRange(ParentBlueprints(blueprint));

        var handlers = classes.OfType<UBlueprintGeneratedClass>()
            .Select(c => Load<UInheritableComponentHandler>(c.InheritableComponentHandler))
            .Where(h => h != null).ToList();

        UObject Override(string variable)
        {
            foreach (var handler in handlers)
            {
                foreach (var record in handler.Records)
                {
                    if (record.ComponentKey.SCSVariableName.Text == variable && Load<UObject>(record.ComponentTemplate) is { } template)
                        return template;
                }
            }

            return null;
        }

        USCS_Node defaultSceneRoot = null;
        var defaultSceneRootOrigin = string.Empty;

        // root-most parent first, a class attaches its components to the ones it inherits
        foreach (var owner in Enumerable.Reverse(classes))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (owner is not UBlueprintGeneratedClass generated || Load<USimpleConstructionScript>(generated.SimpleConstructionScript) is not { } script)
                continue;

            var origin = owner == blueprint ? null : $"Parent: {DisplayClassName(owner.Name)}";
            if (Load<USCS_Node>(script.DefaultSceneRootNode) is { } sceneRoot && defaultSceneRoot == null)
            {
                defaultSceneRoot = sceneRoot;
                defaultSceneRootOrigin = origin;
            }

            var visited = new HashSet<string>(StringComparer.Ordinal);
            void Add(USCS_Node node, string attachTo, int depth)
            {
                if (node == null || depth > 64 || !visited.Add(node.Name)) return;

                var variable = VariableName(node);
                var overridden = owner == blueprint ? null : Override(variable);
                var template = overridden ?? Load<UObject>(node.ComponentTemplate);
                var parentName = node.GetOrDefault<FName>("ParentComponentOrVariableName").Text;
                var children = node.ChildNodes.Select(Load<USCS_Node>).Where(n => n != null).ToList();

                components.Add(new ComponentEntry
                {
                    Key = variable,
                    Name = variable,
                    ClassName = node.GetOrDefault<FPackageIndex>("ComponentClass")?.Name ?? template?.Class?.Name.Text ?? "ActorComponent",
                    Origin = overridden != null ? $"{origin} (overridden)" : origin,
                    Template = template,
                    IsScene = attachTo != null || children.Count > 0 || !string.IsNullOrEmpty(parentName) && parentName != "None" ||
                              template is not UActorComponent || template is USceneComponent,
                    AttachTo = attachTo ?? (string.IsNullOrEmpty(parentName) || parentName == "None" ? null : parentName)
                });

                foreach (var child in children) Add(child, variable, depth + 1);
            }

            foreach (var node in script.RootNodes.Select(Load<USCS_Node>)) Add(node, null, 0);
            foreach (var node in script.AllNodes.Select(Load<USCS_Node>)) Add(node, null, 0);
        }

        // the DefaultSceneRoot only exists while nothing else can be the root
        if (defaultSceneRoot != null && noNativeRoot && !components.Any(c => c.IsScene))
        {
            components.Insert(0, new ComponentEntry
            {
                Key = VariableName(defaultSceneRoot),
                Name = VariableName(defaultSceneRoot),
                ClassName = "SceneComponent",
                Origin = defaultSceneRootOrigin,
                Template = Load<UObject>(defaultSceneRoot.ComponentTemplate),
                IsScene = true
            });
        }
    }

    private static string VariableName(USCS_Node node)
    {
        foreach (var name in new[] { node.InternalVariableName.Text, node.GetOrDefault<FName>("VariableName").Text })
        {
            if (!string.IsNullOrEmpty(name) && name != "None") return name;
        }

        return node.Name;
    }

    private static BlueprintGraphNode SelfNode(UClass blueprint, ComponentEntry entry) => new()
    {
        Id = "self",
        Title = entry.Name,
        Subtitle = blueprint.SuperStruct is { IsNull: false } super ? $"Parent: {DisplayClassName(super.Name)}" : null,
        Kind = EBlueprintNodeKind.Defaults,
        Code = $"// {blueprint.Name}\nclass {blueprint.Name} : {blueprint.SuperStruct?.Name}",
        Width = _COMPONENT_WIDTH
    };

    private static BlueprintGraphNode ComponentNode(ComponentEntry entry, int id)
    {
        var properties = PropertyValues(entry.Template);
        var node = new BlueprintGraphNode
        {
            Id = $"c{id}",
            Title = entry.Name,
            Subtitle = string.IsNullOrEmpty(entry.Origin) ? entry.ClassName : $"{entry.ClassName}  |  {entry.Origin}",
            Kind = EBlueprintNodeKind.Component,
            Code = $"// {entry.ClassName} {entry.Origin}\n// template: {entry.Template?.GetPathName() ?? "none"}\n" +
                   (properties.Count == 0 ? "// no value changed from the class defaults" : string.Join('\n', properties.Select(p => $"{p.Name} = {p.Full}"))),
            Width = _COMPONENT_WIDTH
        };

        foreach (var (name, value, _) in properties.Take(_MAX_PROPERTY_PINS))
            node.Inputs.Add(new BlueprintPin { Name = name, Value = value });
        if (properties.Count > _MAX_PROPERTY_PINS)
            node.Inputs.Add(new BlueprintPin { Name = $"+{properties.Count - _MAX_PROPERTY_PINS} more" });

        return node;
    }

    /// <summary>
    /// Class Defaults: the values the class default object sets, only what differs from the parent class is
    /// serialized. Split in several nodes once it gets long.
    /// </summary>
    private static BlueprintFunctionGraph DefaultsGraph(UClass blueprint, UObject defaults)
    {
        if (defaults == null) return null;

        // component pointers are drawn in the Components graph
        var properties = PropertyValues(defaults, tag => tag.Tag?.GenericValue is FPackageIndex { IsNull: false } index &&
                                                          index.ResolvedObject?.Outer?.Name.Text == defaults.Name);

        var nodes = new List<BlueprintGraphNode>();
        var chunks = properties.Chunk(_DEFAULTS_PER_NODE).ToList();
        if (chunks.Count == 0) chunks.Add([]);

        var x = _MARGIN;
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var node = new BlueprintGraphNode
            {
                Id = $"d{i}",
                Title = chunks.Count > 1 ? $"Class Defaults ({i + 1}/{chunks.Count})" : "Class Defaults",
                Subtitle = properties.Count == 0 ? "no value changed from the parent class" : $"{DisplayClassName(blueprint.Name)}, values set in this class",
                Kind = EBlueprintNodeKind.Defaults,
                Code = chunk.Length == 0
                    ? $"// {defaults.Name}\n// no value changed from the parent class"
                    : $"// {defaults.Name}\n" + string.Join('\n', chunk.Select(p => $"{p.Name} = {p.Full}")),
                Width = _COMPONENT_WIDTH + 40,
                X = x,
                Y = _MARGIN
            };

            foreach (var (name, value, _) in chunk)
                node.Inputs.Add(new BlueprintPin { Name = name, Value = value });

            nodes.Add(node);
            x += node.Width + _TREE_GAP_X;
        }

        return new BlueprintFunctionGraph
        {
            Name = "$ClassDefaults",
            DisplayName = "Class Defaults",
            Signature = $"Class Defaults of {blueprint.Name}",
            IsClassView = true,
            Nodes = nodes,
            Edges = [],
            Note = $"{properties.Count} values",
            CanvasWidth = nodes.Max(n => n.X + n.Width) + _MARGIN,
            CanvasHeight = nodes.Max(n => n.Y + n.Height) + _MARGIN
        };
    }

    /// <summary>Serialized properties of an object as pin name, short pin value and full value.</summary>
    private static List<(string Name, string Value, string Full)> PropertyValues(UObject owner, Func<FPropertyTag, bool> skip = null)
    {
        var values = new List<(string, string, string)>();
        if (owner == null) return values;

        foreach (var property in owner.Properties)
        {
            var type = property.PropertyType.Text ?? string.Empty;
            if (type.Contains("Delegate", StringComparison.Ordinal) || property.Name.Text == "UberGraphFrame" || (skip?.Invoke(property) ?? false)) continue;

            // members of game classes often start with an underscore (_bForceDestroy), the bool prefix follows it
            var name = BlueprintNodeDatabase.PinDisplayName(property.Name.Text.TrimStart('_'), type == "BoolProperty").Trim();
            if (property.ArrayIndex > 0) name += $" [{property.ArrayIndex}]";

            var full = FullValue(property.Tag);
            var value = ShortValue(property.Tag, full);
            values.Add((name, value.Length <= _MAX_DEFAULT_VALUE ? value : value[.._MAX_DEFAULT_VALUE] + "...",
                full.Length <= _MAX_CODE_VALUE ? full : full[.._MAX_CODE_VALUE] + "..."));
        }

        return values;
    }

    /// <summary>A value as the details panel shows it: object and asset names, enum entries without their type, vectors as (X=, Y=, Z=).</summary>
    private static string ShortValue(FPropertyTagType tag, string full)
    {
        switch (tag?.GenericValue)
        {
            case null:
                return "None";
            case FPackageIndex index:
                return index.IsNull ? "None" : ObjectName(index);
            case FSoftObjectPath path:
                var asset = path.AssetPathName.Text;
                if (string.IsNullOrEmpty(asset) || asset == "None") return "None";
                var cut = Math.Max(asset.LastIndexOf('.'), asset.LastIndexOf('/'));
                return cut >= 0 ? asset[(cut + 1)..] : asset;
            case bool flag:
                return flag ? "true" : "false";
            case FName name:
                var text = name.Text;
                var scope = text.IndexOf("::", StringComparison.Ordinal);
                return scope >= 0 ? text[(scope + 2)..] : text;
            case FText localized:
                return localized.Text ?? string.Empty;
            case string plain:
                return plain;
            case IFormattable number:
                return number.ToString(null, CultureInfo.InvariantCulture);
        }

        try
        {
            if (JToken.Parse(full) is JObject { Count: > 0 } structure && structure.Properties().All(p => p.Value is JValue))
                return $"({string.Join(", ", structure.Properties().Select(p => $"{p.Name}={Convert.ToString(((JValue) p.Value).Value, CultureInfo.InvariantCulture)}"))})";
        }
        catch (Exception)
        {
            // not json, shown as it is
        }

        return full;
    }

    private static string FullValue(FPropertyTagType tag)
    {
        if (tag == null) return "None";
        try
        {
            var json = JsonConvert.SerializeObject(tag, Formatting.None);
            return json.Length > 1 && json[0] == '"' && json[^1] == '"' ? JsonConvert.DeserializeObject<string>(json) : json;
        }
        catch (Exception)
        {
            return tag.ToString() ?? string.Empty;
        }
    }

    private static IEnumerable<UObject> ExportsOf(UClass blueprint)
    {
        try
        {
            return blueprint.Owner?.GetExports().ToList() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static T Load<T>(FPackageIndex index) where T : UObject
    {
        if (index is not { IsNull: false }) return null;
        try
        {
            return index.TryLoad(out var loaded) ? loaded as T : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string DisplayClassName(string name) => name.EndsWith("_C", StringComparison.Ordinal) ? name[..^2] : name;
}
