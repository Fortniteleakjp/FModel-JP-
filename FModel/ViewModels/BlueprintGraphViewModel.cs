using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Objects.UObject.BlueprintDecompiler;
using CUE4Parse.UE4.Objects.UObject.Editor;
using CUE4Parse.Utils;
using FModel.Services;

namespace FModel.ViewModels;

public enum EBlueprintNodeKind
{
    Event,
    FunctionEntry,
    Call,
    Pure,
    Latent,
    Set,
    Get,
    Branch,
    Flow,
    Return,
    Cast,
    Timeline,
    Struct,
    Component,
    Defaults,
    Other
}

public enum EBlueprintEdgeKind
{
    Exec,
    Data
}

/// <summary>
/// A pin of a node. Exec pins carry the flow, data pins a value, either wired or typed in (<see cref="Value"/>).
/// </summary>
public class BlueprintPin
{
    public string Name { get; init; }
    public bool IsExec { get; init; }
    public string Value { get; set; }
    public bool HasValue => !string.IsNullOrEmpty(Value);
}

/// <summary>
/// A node of the graph, already laid out. Pins sit on fixed rows so wires can be drawn to them.
/// </summary>
public class BlueprintGraphNode : INotifyPropertyChanged
{
    public const double PIN_HEIGHT = 18;
    public const double BORDER = 2;
    public const double BODY_PADDING = 4;

    public string Id { get; init; }
    public string Title { get; init; }
    public string Subtitle { get; set; }

    /// <summary>Pseudo code of the statement or expression, shown in the detail pane.</summary>
    public string Code { get; init; }

    public EBlueprintNodeKind Kind { get; init; }

    /// <summary>Operator nodes (CompactNodeTitle): no header, the symbol drawn in the middle, unnamed pins.</summary>
    public bool IsCompact { get; init; }

    /// <summary>Bytecode offset of the statement, -1 for the synthetic entry and event nodes.</summary>
    public int Offset { get; init; } = -1;

    /// <summary>Function of the same class this node calls, used to jump into it.</summary>
    public string TargetFunction { get; set; }

    /// <summary>Offset inside <see cref="TargetFunction"/> the call enters at, the ubergraph entry point of an event.</summary>
    public int TargetOffset { get; set; } = -1;

    public List<BlueprintPin> Inputs { get; } = [];
    public List<BlueprintPin> Outputs { get; } = [];

    public bool IsExec => Inputs.Any(p => p.IsExec) || Outputs.Any(p => p.IsExec);

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 240;

    // a variable getter is a bare pill with its name on the output pin, an operator shows its symbol instead of a header
    public double HeaderHeight => Kind == EBlueprintNodeKind.Get || IsCompact ? 0 : string.IsNullOrEmpty(Subtitle) ? 24 : 38;
    public double Height => BORDER * 2 + HeaderHeight + BODY_PADDING * 2 + Math.Max(IsCompact ? 2 : 1, Math.Max(Inputs.Count, Outputs.Count)) * PIN_HEIGHT;

    public double InputY(int index) => Y + BORDER + HeaderHeight + BODY_PADDING + index * PIN_HEIGHT + PIN_HEIGHT / 2;
    public double OutputY(int index) => InputY(index);

    public bool CanJump => !string.IsNullOrEmpty(TargetFunction);

    /// <summary>The function or variable this node calls or reads, as <see cref="MemberUsageQuery"/> text, to find its other uses.</summary>
    public string MemberQuery { get; set; }
    public EMemberKind MemberKind { get; set; }
    public bool HasMemberQuery => MemberQuery != null;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    private bool _isMatch;
    public bool IsMatch
    {
        get => _isMatch;
        set
        {
            if (_isMatch == value) return;
            _isMatch = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class BlueprintGraphEdge
{
    public EBlueprintEdgeKind Kind { get; init; }
    public string Geometry { get; init; }
}

/// <summary>
/// Graph of one function of the class.
/// </summary>
public class BlueprintFunctionGraph
{
    public string Name { get; init; }
    public string DisplayName { get; set; }
    public string Signature { get; init; }
    public bool IsUbergraph { get; init; }
    public bool IsEventStub { get; init; }

    /// <summary>Components tree or class defaults: what the class holds besides its functions.</summary>
    public bool IsClassView { get; init; }

    /// <summary>Parent blueprint the function is declared in, null for the class's own ones.</summary>
    public string InheritedFrom { get; set; }
    public bool IsInherited => InheritedFrom != null;

    /// <summary>The function as <see cref="MemberUsageQuery"/> text, null for the event graph and the class views.</summary>
    public string MemberQuery { get; set; }

    public List<BlueprintGraphNode> Nodes { get; init; } = [];
    public List<BlueprintGraphEdge> Edges { get; init; } = [];
    public double CanvasWidth { get; set; }
    public double CanvasHeight { get; set; }
    public string Note { get; set; }

    public BlueprintGraphNode FindByOffset(int offset) =>
        Nodes.Where(node => node.IsExec && node.Offset >= offset).MinBy(node => node.Offset);
}

public class BlueprintGraph
{
    public string Name { get; init; }
    public string PackagePath { get; init; }
    public string SuperName { get; init; }
    public List<BlueprintFunctionGraph> Functions { get; init; } = [];
    public string Note { get; init; }
}

/// <summary>
/// Editor names the blueprint keeps in its .o.uasset (CookedClassMetaData): display names of its own
/// functions and variables.
/// </summary>
public sealed class BlueprintEditorNames
{
    public Dictionary<string, string> Functions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static BlueprintEditorNames From(UClassCookedMetaData metaData)
    {
        var names = new BlueprintEditorNames();
        if (metaData == null) return names;

        foreach (var (name, store) in metaData.ClassMetaData.PropertiesMetaData ?? [])
        {
            if (store?.FieldMetaData != null && store.Value.FieldMetaData.TryGetValue("DisplayName", out var display) && !string.IsNullOrEmpty(display))
                names.Properties[name] = display;
        }

        foreach (var (name, store) in metaData.FunctionsMetaData ?? [])
        {
            if (store?.ObjectMetaData.ObjectMetaData != null &&
                store.Value.ObjectMetaData.ObjectMetaData.TryGetValue("DisplayName", out var display) && !string.IsNullOrEmpty(display))
                names.Functions[name] = display;
        }

        return names;
    }
}

/// <summary>
/// Rebuilds node graphs out of a blueprint generated class.
/// A cooked blueprint keeps no EdGraph, only the Kismet bytecode of every function. Statements with side
/// effects become exec nodes chained by the jumps between them, and the expressions they read are broken
/// back down into the nodes the editor shows: calls with one pin per argument, variable getters, pure
/// functions and casts, wired into the pins that consume them. Constants stay on the pin as typed-in values.
/// The compiler routes node outputs through temporaries (CallFunc_X_ReturnValue, K2Node_...), a read of one
/// is wired back to the node that wrote it.
/// Node and pin names follow the editor: native functions from <see cref="BlueprintNodeDatabase"/>, the
/// blueprint's own names from its cooked metadata, K2 nodes (casts, timelines, bound events, Make Struct,
/// Spawn Actor, Create Widget...) from the titles their K2Node classes build.
/// </summary>
public static partial class BlueprintGraphBuilder
{
    private const double _EXEC_WIDTH = 240;
    private const double _DATA_WIDTH = 200;
    private const double _COMPACT_WIDTH = 110;
    private const double _DATA_GAP_X = 40;
    private const double _DATA_GAP_Y = 14;
    private const double _COLUMN_GAP = 70;
    private const double _ROW_GAP = 50;
    private const double _BLOCK_GAP = 26;
    private const double _MARGIN = 40;
    private const int _MAX_STATEMENTS = 1500;
    private const int _MAX_EXPRESSION_DEPTH = 12;
    private const int _MAX_VALUE = 26;
    private const int _MAX_PARENTS = 16;

    // BlueprintDecompilerUtils keeps the function being printed in a static
    private static readonly Lock _decompilerLock = new();
    private static readonly Regex _whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex _numberSuffix = new(@"_\d+$", RegexOptions.Compiled);

    public static UClass FindClass(IPackage package) =>
        package.GetExports().OfType<UClass>().FirstOrDefault(c => c.FuncMap is { Count: > 0 }) ??
        package.GetExports().OfType<UClass>().FirstOrDefault();

    /// <summary>
    /// Graphs of every function of <paramref name="blueprint"/>, its components and class defaults, then the functions
    /// it inherits from its parent blueprints (a child or data-only blueprint often has no function of its own).
    /// </summary>
    /// <param name="editorNamesOf">editor names of a parent blueprint class, read from its own .o.uasset</param>
    public static BlueprintGraph Build(UClass blueprint, string packagePath, bool scriptDataRead, BlueprintEditorNames editorNames,
        CancellationToken cancellationToken, Func<UClass, BlueprintEditorNames> editorNamesOf = null)
    {
        var graphs = BuildFunctions(blueprint, editorNames, cancellationToken, out var functionCount, out var eventCount);
        graphs.AddRange(BuildClassViews(blueprint, cancellationToken));

        var inherited = 0;
        var parents = new List<string>();
        foreach (var parent in ParentBlueprints(blueprint))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var parentName = parent.Name.EndsWith("_C", StringComparison.Ordinal) ? parent.Name[..^2] : parent.Name;
            List<BlueprintFunctionGraph> parentGraphs;
            try
            {
                parentGraphs = BuildFunctions(parent, editorNamesOf?.Invoke(parent), cancellationToken, out _, out _);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                continue; // a parent that fails to build is left out
            }

            if (parentGraphs.Count == 0) continue;

            foreach (var graph in parentGraphs)
            {
                graph.InheritedFrom = parentName;
                if (!graph.IsUbergraph) graph.DisplayName = $"{graph.DisplayName}  ({parentName})";
                graph.Note = $"Parent: {parentName}  |  {graph.Note}";
            }

            graphs.AddRange(parentGraphs);
            inherited += parentGraphs.Count;
            parents.Add(parentName);
        }

        string note;
        if (!scriptDataRead)
            note = "Script bytecode is not read, enable \"Serialize Script Bytecode\" in the settings and reload to see the graphs";
        else if (functionCount == 0)
            note = inherited > 0
                ? $"this class has no functions of its own, {inherited} inherited from {string.Join(", ", parents)}"
                : "this class has no functions, see its components and class defaults";
        else
            note = $"{functionCount} functions" + (eventCount > 0 ? $", {eventCount} events" : string.Empty) +
                   (inherited > 0 ? $", {inherited} inherited" : string.Empty) +
                   $"  |  node names: {BlueprintNodeDatabase.Source ?? "editor naming rules"}";

        return new BlueprintGraph
        {
            Name = blueprint.Name,
            PackagePath = packagePath,
            SuperName = blueprint.SuperStruct?.Name,
            Functions = graphs,
            Note = note
        };
    }

    /// <summary>Graphs of the functions declared in <paramref name="blueprint"/> itself, the EventGraph first.</summary>
    private static List<BlueprintFunctionGraph> BuildFunctions(UClass blueprint, BlueprintEditorNames editorNames, CancellationToken cancellationToken,
        out int functionCount, out int eventCount)
    {
        var functions = new List<UFunction>();
        foreach (var pointer in blueprint.FuncMap?.Values ?? Enumerable.Empty<FPackageIndex>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (pointer.TryLoad(out var export) && export is UFunction function)
                    functions.Add(function);
            }
            catch (Exception)
            {
                // a function that fails to load is left out
            }
        }

        functionCount = functions.Count;
        eventCount = 0;
        if (functions.Count == 0) return [];

        var context = new BlueprintContext(blueprint, functions, editorNames ?? new BlueprintEditorNames());
        eventCount = context.Events.Count;
        var graphs = new List<BlueprintFunctionGraph>();

        if (context.Ubergraph != null)
            graphs.Add(new FunctionGraphBuilder(context.Ubergraph, context).Build(cancellationToken));

        var package = blueprint.Owner?.Name;
        foreach (var function in functions.Where(f => f != context.Ubergraph && !context.IsTimelineCallback(f.Name))
                     .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var graph = new FunctionGraphBuilder(function, context).Build(cancellationToken);
            // an override is the parent's function, calls may go through the parent class
            graph.MemberQuery = function.SuperStruct is { IsNull: false } overridden
                ? MemberUsageLookup.QueryForFunction(overridden)
                : MemberUsageLookup.QueryFor(package, blueprint.Name, function.Name);
            graphs.Add(graph);
        }

        return graphs;
    }

    /// <summary>The blueprint classes above <paramref name="blueprint"/>, nearest first, up to the first native class.</summary>
    private static IEnumerable<UClass> ParentBlueprints(UClass blueprint)
    {
        var super = blueprint.SuperStruct;
        for (var depth = 0; super is { IsNull: false } && depth < _MAX_PARENTS; depth++)
        {
            UClass parent;
            try
            {
                parent = super.TryLoad(out var loaded) ? loaded as UClass : null;
            }
            catch (Exception)
            {
                parent = null;
            }

            if (parent is not UBlueprintGeneratedClass) yield break;

            yield return parent;
            super = parent.SuperStruct;
        }
    }

    /// <param name="Parameters">ubergraph frame variable the stub copies each parameter into, and the parameter name</param>
    private sealed record EventEntry(UFunction Function, int Offset, List<(string Frame, string Name)> Parameters);

    private sealed record TimelineInfo(string Name, string UpdateFunction, string FinishedFunction, string DirectionProperty,
        List<(string Track, string Function)> EventTracks, List<(string Track, string Property)> ValueTracks);

    /// <summary>
    /// What the whole class knows: its functions, the events entering the ubergraph, the timelines and the
    /// component delegates bound to events, and the editor names of its members.
    /// </summary>
    private sealed class BlueprintContext
    {
        public UClass Class { get; }
        public UFunction Ubergraph { get; }
        public Dictionary<string, UFunction> Functions { get; }
        public BlueprintEditorNames Names { get; }
        public List<EventEntry> Events { get; }
        public Dictionary<string, TimelineInfo> Timelines { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (string Component, string Delegate)> BoundEvents { get; } = new(StringComparer.Ordinal);

        private readonly HashSet<string> _timelineCallbacks = new(StringComparer.Ordinal);
        private readonly HashSet<string> _boolProperties = new(StringComparer.Ordinal);

        public BlueprintContext(UClass blueprint, List<UFunction> functions, BlueprintEditorNames names)
        {
            Class = blueprint;
            Names = names;
            Functions = functions.GroupBy(f => f.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            Ubergraph = functions.FirstOrDefault(f => f.FunctionFlags.HasFlag(EFunctionFlags.FUNC_UbergraphFunction)) ??
                        functions.FirstOrDefault(f => f.Name.StartsWith("ExecuteUbergraph", StringComparison.Ordinal));

            foreach (var property in blueprint.ChildProperties ?? [])
            {
                if (property is FBoolProperty) _boolProperties.Add(property.Name.Text);
            }

            ReadBindings(blueprint);
            Events = Ubergraph == null ? [] : FindEvents(functions);
        }

        public bool IsTimelineCallback(string function) => _timelineCallbacks.Contains(function);

        private string _nativeParent;

        /// <summary>The first native class above the blueprint and its parent blueprints (Character, UserWidget...).</summary>
        public string NativeParent => _nativeParent ??= FindNativeParent();

        private string FindNativeParent()
        {
            var super = Class.SuperStruct;
            for (var depth = 0; super is { IsNull: false } && depth < 16; depth++)
            {
                try
                {
                    if (!super.TryLoad(out var loaded) || loaded is not UClass parent || parent.SuperStruct is not { IsNull: false })
                        return super.Name;

                    super = parent.SuperStruct;
                }
                catch (Exception)
                {
                    return super.Name;
                }
            }

            return super?.Name ?? string.Empty;
        }

        /// <summary>A variable by name: a local of the function, a member of the class or of its parent blueprints.</summary>
        public FProperty FindProperty(string name, UFunction function)
        {
            foreach (var owner in new UStruct[] { function, Ubergraph })
            {
                if (owner?.ChildProperties?.FirstOrDefault(p => p.Name.Text == name) is FProperty local) return local;
            }

            UStruct type = Class;
            for (var depth = 0; type != null && depth < 16; depth++)
            {
                if (type.ChildProperties?.FirstOrDefault(p => p.Name.Text == name) is FProperty member) return member;

                try
                {
                    type = type.SuperStruct is { IsNull: false } super && super.TryLoad(out var loaded) ? loaded as UStruct : null;
                }
                catch (Exception)
                {
                    type = null;
                }
            }

            return null;
        }

        public bool IsBoolProperty(string name, UFunction function) =>
            _boolProperties.Contains(name) || (function?.ChildProperties ?? []).Any(p => p is FBoolProperty && p.Name.Text == name) ||
            (Ubergraph?.ChildProperties ?? []).Any(p => p is FBoolProperty && p.Name.Text == name);

        /// <summary>Pin name of a variable: its DisplayName metadata made friendly, as K2Node_Variable shows it.</summary>
        public string VariableDisplay(string name, UFunction function)
        {
            var display = Names.Properties.GetValueOrDefault(name) ?? name;
            return BlueprintNodeDatabase.PinDisplayName(display, IsBoolProperty(name, function));
        }

        private readonly Dictionary<string, Dictionary<long, string>> _enums = new(StringComparer.Ordinal);

        /// <summary>Enumerators of the enum a property holds, by value: what an enum pin shows instead of its byte.</summary>
        public Dictionary<long, string> EnumMembers(FProperty property) => property switch
        {
            FEnumProperty { Enum: { IsNull: false } enumeration } => EnumMembers(enumeration),
            FByteProperty { Enum: { IsNull: false } enumeration } => EnumMembers(enumeration),
            _ => null
        };

        private Dictionary<long, string> EnumMembers(FPackageIndex index)
        {
            if (_enums.TryGetValue(index.Name, out var members)) return members;

            try
            {
                // a user defined enum is in a package, a native one only in the mappings
                if (index.TryLoad(out var loaded) && loaded is UEnum { Names.Length: > 0 } enumeration) members = EnumMembers(enumeration);
            }
            catch (Exception)
            {
                // native enum
            }

            return _enums[index.Name] = members ?? EnumMembers(index.Name);
        }

        private Dictionary<long, string> EnumMembers(string enumName)
        {
            if (Class.Owner?.Mappings?.Enums.TryGetValue(enumName, out var values) != true) return null;
            return values.ToDictionary(v => v.Key, v => EnumeratorDisplay(v.Value));
        }

        private static Dictionary<long, string> EnumMembers(UEnum enumeration)
        {
            // UUserDefinedEnum keeps its editor names apart, its enumerators are NewEnumerator0, 1...
            var displayNames = new Dictionary<string, string>(StringComparer.Ordinal);
            if (enumeration.GetOrDefault<UScriptMap>("DisplayNameMap") is { } map)
            {
                foreach (var (key, value) in map.Properties)
                {
                    if (key?.GenericValue?.ToString() is { } name && (value?.GenericValue as FText)?.Text is { Length: > 0 } display)
                        displayNames[name.SubstringAfterLast("::")] = display;
                }
            }

            var members = new Dictionary<long, string>();
            foreach (var (name, value) in enumeration.Names)
            {
                var member = name.Text.SubstringAfterLast("::");
                members.TryAdd(value, displayNames.GetValueOrDefault(member) ?? EnumeratorDisplay(member));
            }

            return members;
        }

        /// <summary>UEnum::GetDisplayNameTextByIndex without a DisplayName: the friendly form of the enumerator.</summary>
        private static string EnumeratorDisplay(string name) =>
            BlueprintNodeDatabase.NameToDisplayString(name.SubstringAfterLast("::"), false);

        /// <summary>Enum of a member of a native class or struct, which only the mappings describe.</summary>
        public Dictionary<long, string> MappedEnum(string owner, string property)
        {
            if (Class.Owner?.Mappings is not { } mappings || !mappings.Types.TryGetValue(owner, out var type)) return null;

            for (var depth = 0; type != null && depth < 32; depth++)
            {
                foreach (var info in type.Properties.Values)
                {
                    if (info.Name != property) continue;
                    if (info.MappingType.EnumName is not { } enumName) return null;
                    if (_enums.TryGetValue(enumName, out var members)) return members;
                    return _enums[enumName] = EnumMembers(enumName);
                }

                type = type.Super?.Value;
            }

            return null;
        }

        /// <summary>Node title of one of the class's own functions.</summary>
        public string FunctionDisplay(string name) =>
            Names.Functions.TryGetValue(name, out var display) ? display : BlueprintNodeDatabase.NameToDisplayString(name, false);

        public string ClassName => Class.Name;

        /// <summary>
        /// Timelines and component bound events live in the class's DynamicBindingObjects and TimelineTemplates exports.
        /// </summary>
        private void ReadBindings(UClass blueprint)
        {
            IEnumerable<UObject> exports;
            try
            {
                exports = blueprint.Owner?.GetExports() ?? [];
            }
            catch (Exception)
            {
                return;
            }

            foreach (var export in exports)
            {
                try
                {
                    switch (export.ExportType)
                    {
                        case "TimelineTemplate":
                            ReadTimeline(export);
                            break;
                        case "ComponentDelegateBinding":
                            if (!export.TryGetValue(out FStructFallback[] bindings, "ComponentDelegateBindings")) break;
                            foreach (var binding in bindings)
                            {
                                var function = binding.GetOrDefault<FName>("FunctionNameToBind").Text;
                                if (string.IsNullOrEmpty(function)) continue;

                                BoundEvents[function] = (binding.GetOrDefault<FName>("ComponentPropertyName").Text,
                                    binding.GetOrDefault<FName>("DelegatePropertyName").Text);
                            }

                            break;
                    }
                }
                catch (Exception)
                {
                    // a binding that does not read leaves its event as a custom one
                }
            }
        }

        private void ReadTimeline(UObject template)
        {
            var name = template.GetOrDefault<FName>("VariableName").Text;
            if (string.IsNullOrEmpty(name) || name == "None") name = template.Name.EndsWith("_Template", StringComparison.Ordinal) ? template.Name[..^9] : template.Name;

            var eventTracks = new List<(string, string)>();
            if (template.TryGetValue(out FStructFallback[] events, "EventTracks"))
            {
                foreach (var track in events)
                    eventTracks.Add((track.GetOrDefault<FName>("TrackName").Text, track.GetOrDefault<FName>("FunctionName").Text));
            }

            var valueTracks = new List<(string, string)>();
            foreach (var kind in new[] { "FloatTracks", "VectorTracks", "LinearColorTracks" })
            {
                if (!template.TryGetValue(out FStructFallback[] tracks, kind)) continue;
                foreach (var track in tracks)
                    valueTracks.Add((track.GetOrDefault<FName>("TrackName").Text, track.GetOrDefault<FName>("PropertyName").Text));
            }

            var info = new TimelineInfo(name,
                template.GetOrDefault<FName>("UpdateFunctionName").Text,
                template.GetOrDefault<FName>("FinishedFunctionName").Text,
                template.GetOrDefault<FName>("DirectionPropertyName").Text,
                eventTracks, valueTracks);

            Timelines[name] = info;
            _timelineCallbacks.Add(info.UpdateFunction);
            _timelineCallbacks.Add(info.FinishedFunction);
            foreach (var (_, function) in eventTracks) _timelineCallbacks.Add(function);
        }

        /// <summary>
        /// An event is a stub function that copies its parameters into the ubergraph frame and calls
        /// the ubergraph with the offset it starts at.
        /// </summary>
        private List<EventEntry> FindEvents(List<UFunction> functions)
        {
            var events = new List<EventEntry>();
            foreach (var function in functions)
            {
                if (function == Ubergraph) continue;

                var parameters = new List<(string, string)>();
                int? offset = null;

                if (function.EventGraphFunction is { IsNull: false } eventGraph && eventGraph.Name == Ubergraph.Name)
                    offset = function.EventGraphCallOffset;

                foreach (var statement in function.ScriptBytecode ?? [])
                {
                    switch (statement)
                    {
                        case EX_LetValueOnPersistentFrame persistent:
                            parameters.Add((persistent.DestinationProperty.ToString(),
                                persistent.AssignmentExpression is EX_VariableBase source ? source.Variable.ToString() : persistent.DestinationProperty.ToString()));
                            break;
                        case KismetExpression when offset == null && UnwrapCall(statement) is EX_FinalFunction call && call.StackNode.Name == Ubergraph.Name &&
                                                   call.Parameters is [EX_IntConst entry, ..]:
                            offset = entry.Value;
                            break;
                    }
                }

                if (offset != null) events.Add(new EventEntry(function, offset.Value, parameters));
            }

            return events.OrderBy(e => e.Offset).ToList();
        }

        /// <summary>
        /// Title of an event node: K2Node_ComponentBoundEvent "{Delegate} ({Component})", K2Node_Event
        /// "Event {Function}" for an override, K2Node_CustomEvent "{Name}" / "Custom Event" otherwise.
        /// </summary>
        public (string Title, string Subtitle) EventTitle(UFunction stub)
        {
            var name = stub.Name;
            if (BoundEvents.TryGetValue(name, out var bound))
                return ($"{BlueprintNodeDatabase.NameToDisplayString(bound.Delegate, false)} ({bound.Component})", null);

            if (name.StartsWith("InpActEvt_", StringComparison.Ordinal))
            {
                var action = name["InpActEvt_".Length..];
                var cut = action.IndexOf("_K2Node_", StringComparison.Ordinal);
                return ($"InputAction {(cut > 0 ? action[..cut] : action)}", null);
            }

            if (stub.SuperStruct is { IsNull: false } || stub.FunctionFlags.HasFlag(EFunctionFlags.FUNC_Event))
            {
                var info = OverriddenFunction(stub) ?? BlueprintNodeDatabase.FindByName(name, preferEvent: true);
                return ($"Event {BlueprintNodeDatabase.FunctionDisplayName(name, info)}", null);
            }

            return (name, "Custom Event");
        }

        /// <summary>
        /// The native function an override comes from, following the chain of parent blueprints up to the engine class
        /// that declares it (EnemyPawn_Parent_C:ReceiveBeginPlay -> Actor:ReceiveBeginPlay).
        /// </summary>
        public static BlueprintFunctionInfo OverriddenFunction(UFunction function)
        {
            var super = function.SuperStruct;
            for (var depth = 0; super is { IsNull: false } && depth < 16; depth++)
            {
                try
                {
                    var info = BlueprintNodeDatabase.Find(super.ResolvedObject?.Outer?.Name.Text, function.Name);
                    if (info != null) return info;

                    if (!super.TryLoad(out var loaded) || loaded is not UFunction parent) return null;
                    super = parent.SuperStruct;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            return null;
        }
    }

    /// <param name="Pending">a temporary read before any statement wrote it (the ubergraph is not in flow order), wired at the end</param>
    private readonly record struct Source(BlueprintGraphNode Node, int Pin, string Literal, string Pending = null)
    {
        public static Source Of(string literal) => new(null, -1, literal);
        public static Source Later(string temporary) => new(null, -1, null, temporary);
    }

    private readonly record struct DataLink(BlueprintGraphNode From, int FromPin, BlueprintGraphNode To, int ToPin);

    private readonly record struct ExecLink(int FromPin, int Target, int ToPin);

    /// <summary>A called function and everything known about it.</summary>
    private sealed record Callee(string Name, string Owner, BlueprintFunctionInfo Info, UFunction Function,
        bool IsStatic, bool IsPure, List<BlueprintParameterInfo> Parameters, bool HasReturnValue, bool IsParentCall);

    private static readonly Dictionary<string, int> _timelineInputs = new(StringComparer.Ordinal)
    {
        ["Play"] = 0, ["PlayFromStart"] = 1, ["Stop"] = 2, ["Reverse"] = 3, ["ReverseFromEnd"] = 4, ["SetNewTime"] = 5
    };

    /// <summary>
    /// Builds the graph of one function.
    /// </summary>
    private sealed class FunctionGraphBuilder(UFunction function, BlueprintContext context)
    {
        private readonly bool _isUbergraph = function == context.Ubergraph;
        private readonly List<BlueprintGraphNode> _nodes = [];
        private readonly List<DataLink> _dataLinks = [];

        /// <summary>Last node output written into each temporary, what a later read of it is wired to.</summary>
        private readonly Dictionary<string, Source> _producers = new(StringComparer.Ordinal);

        /// <summary>Values the function writes into its out parameters, drawn as pins of the return node.</summary>
        private readonly Dictionary<string, Source> _returnValues = new(StringComparer.Ordinal);

        private readonly HashSet<string> _outParameters = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Callee> _callees = new(StringComparer.Ordinal);

        /// <summary>Data nodes created by a statement of their own (pure calls), waiting for the exec node that follows them.</summary>
        private readonly List<BlueprintGraphNode> _standalone = [];

        /// <summary>Exec node each standalone data node is drawn under.</summary>
        private readonly Dictionary<BlueprintGraphNode, BlueprintGraphNode> _standaloneOwner = [];

        /// <summary>Make Struct nodes by the temporary they fill, Break Struct nodes by the struct they read.</summary>
        private readonly Dictionary<string, BlueprintGraphNode> _makeStructs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, BlueprintGraphNode> _breakStructs = new(StringComparer.Ordinal);

        /// <summary>SpawnActor nodes waiting for their FinishSpawningActor, by the temporary holding the deferred actor.</summary>
        private readonly Dictionary<BlueprintGraphNode, string> _spawnNodes = [];

        /// <summary>Impure casts: the success flag each one writes, and the offset its Cast Failed output jumps to.</summary>
        private readonly Dictionary<string, BlueprintGraphNode> _castSuccess = new(StringComparer.Ordinal);
        private readonly Dictionary<BlueprintGraphNode, int> _castFailed = [];
        private readonly HashSet<string> _branchedSuccessFlags = new(StringComparer.Ordinal);

        /// <summary>Timeline nodes by timeline name, and the statements that drive one of their exec inputs.</summary>
        private readonly Dictionary<string, BlueprintGraphNode> _timelines = new(StringComparer.Ordinal);
        private readonly Dictionary<KismetExpression, int> _redirectPin = [];

        private readonly List<(BlueprintGraphNode Node, int Pin, string Temporary)> _pendingLinks = [];

        private KismetExpression[] _all = [];
        private int _index;
        private int _nextId;

        public BlueprintFunctionGraph Build(CancellationToken cancellationToken)
        {
            var signature = Signature(function);
            var bytecode = function.ScriptBytecode ?? [];
            _all = bytecode.Where(s => s is not (EX_EndOfScript or EX_Nothing or EX_NothingInt32)).ToArray();
            var truncated = _all.Length > _MAX_STATEMENTS;
            if (truncated) _all = _all[.._MAX_STATEMENTS];

            // success flags a Branch reads right after a cast belong to an impure cast node
            foreach (var statement in _all)
            {
                if (statement is EX_JumpIfNot { BooleanExpression: EX_VariableBase flag } &&
                    flag.Variable.ToString().StartsWith("K2Node_DynamicCast_bSuccess", StringComparison.Ordinal))
                    _branchedSuccessFlags.Add(flag.Variable.ToString());
            }

            var entries = new List<BlueprintGraphNode>();
            var entryTargets = new List<int>();
            var timelineOutputs = new List<(BlueprintGraphNode Node, int Pin, int Offset)>();

            lock (_decompilerLock)
            {
                BlueprintDecompilerUtils.Function = function;

                if (_isUbergraph)
                {
                    foreach (var entry in context.Events)
                    {
                        if (context.IsTimelineCallback(entry.Function.Name)) continue;

                        var (title, subtitle) = context.EventTitle(entry.Function);
                        var node = NewNode(title, EBlueprintNodeKind.Event, $"// event {entry.Function.Name}\nExecuteUbergraph(EntryPoint = {entry.Offset});", -1, _EXEC_WIDTH);
                        node.Subtitle = subtitle;
                        node.Outputs.Add(new BlueprintPin { IsExec = true });
                        foreach (var (frame, name) in entry.Parameters)
                        {
                            _producers[frame] = new Source(node, node.Outputs.Count, null);
                            node.Outputs.Add(new BlueprintPin { Name = BlueprintNodeDatabase.PinDisplayName(name, context.IsBoolProperty(name, entry.Function)) });
                        }

                        node.TargetFunction = entry.Function.Name;
                        entries.Add(node);
                        entryTargets.Add(entry.Offset);
                    }

                    foreach (var timeline in context.Timelines.Values)
                        timelineOutputs.AddRange(TimelineNode(timeline));
                }
                else
                {
                    var node = NewNode(context.FunctionDisplay(function.Name), EBlueprintNodeKind.FunctionEntry, $"// ({FlagsOf(function)})\n{signature}", -1, _EXEC_WIDTH);
                    node.Outputs.Add(new BlueprintPin { IsExec = true });
                    foreach (var property in Parameters(function))
                    {
                        var name = property.Name.Text;
                        if (property.PropertyFlags.HasFlag(EPropertyFlags.ReturnParm) ||
                            (property.PropertyFlags.HasFlag(EPropertyFlags.OutParm) && !property.PropertyFlags.HasFlag(EPropertyFlags.ReferenceParm)))
                        {
                            _outParameters.Add(name);
                            continue;
                        }

                        _producers[name] = new Source(node, node.Outputs.Count, null);
                        node.Outputs.Add(new BlueprintPin { Name = BlueprintNodeDatabase.PinDisplayName(name, property is FBoolProperty) });
                    }

                    entries.Add(node);
                    entryTargets.Add(_all.Length > 0 ? _all[0].StatementIndex : -1);
                }

                // statements, in order so every read finds the temporary written before it
                var execOfStatement = new Dictionary<KismetExpression, BlueprintGraphNode>();
                var ends = new HashSet<KismetExpression>();
                for (_index = 0; _index < _all.Length; _index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var statement = _all[_index];
                    var exec = Statement(statement, out var isEnd);
                    if (isEnd) ends.Add(statement);
                    if (exec == null) continue;

                    execOfStatement[statement] = exec;
                    // pure statements since the last exec node are drawn next to this one
                    foreach (var pending in _standalone) _standaloneOwner[pending] = exec;
                    _standalone.Clear();
                }

                ConnectPending();
                return Finish(entries, entryTargets, timelineOutputs, execOfStatement, ends, signature, bytecode.Length, truncated);
            }
        }

        private BlueprintFunctionGraph Finish(List<BlueprintGraphNode> entries, List<int> entryTargets,
            List<(BlueprintGraphNode Node, int Pin, int Offset)> timelineOutputs,
            Dictionary<KismetExpression, BlueprintGraphNode> execOf, HashSet<KismetExpression> ends,
            string signature, int bytecodeLength, bool truncated)
        {
            var all = _all;

            // exec wires land on the exec input of the target node, a timeline's own input for the calls driving it
            (int Node, int Pin) Resolve(int offset, int depth = 0)
            {
                if (offset < 0 || depth > 32) return (-1, 0);

                var position = Array.FindIndex(all, s => s.StatementIndex >= offset);
                if (position < 0) return (-1, 0);

                for (var i = position; i < all.Length; i++)
                {
                    var statement = all[i];
                    if (execOf.TryGetValue(statement, out var node)) return (_nodes.IndexOf(node), _redirectPin.GetValueOrDefault(statement));
                    if (ends.Contains(statement) || statement is EX_ComputedJump) return (-1, 0);
                    if (statement is EX_Jump { Token: EExprToken.EX_Jump } jump) return Resolve((int) jump.CodeOffset, depth + 1);
                }

                return (-1, 0);
            }

            (int Node, int Pin) Next(KismetExpression statement)
            {
                var position = Array.IndexOf(all, statement);
                return position < 0 || position + 1 >= all.Length ? (-1, 0) : Resolve(all[position + 1].StatementIndex);
            }

            var links = new Dictionary<BlueprintGraphNode, List<ExecLink>>();
            void Link(BlueprintGraphNode from, int pin, (int Node, int Pin) to)
            {
                if (pin < 0 || to.Node < 0) return;
                if (!links.TryGetValue(from, out var list)) links[from] = list = [];
                list.Add(new ExecLink(pin, to.Node, to.Pin));
            }

            for (var i = 0; i < entries.Count; i++)
            {
                links.TryAdd(entries[i], []);
                Link(entries[i], 0, Resolve(entryTargets[i]));
            }

            foreach (var (node, pin, offset) in timelineOutputs)
            {
                links.TryAdd(node, []);
                Link(node, pin, Resolve(offset));
            }

            foreach (var (statement, node) in execOf)
            {
                links.TryAdd(node, []);
                if (_redirectPin.ContainsKey(statement)) continue; // a timeline input, the timeline's outputs are wired above

                switch (statement)
                {
                    case EX_JumpIfNot branch:
                        Link(node, 0, Next(statement));
                        Link(node, 1, Resolve((int) branch.CodeOffset));
                        break;
                    case EX_PushExecutionFlow push:
                        Link(node, 0, Next(statement));
                        Link(node, 1, Resolve((int) push.PushingAddress));
                        break;
                    case EX_Return:
                        break;
                    default:
                        if (node.Kind == EBlueprintNodeKind.Latent)
                        {
                            // a latent node has a single "Completed" output, taken once the action finishes
                            if (LatentResume(statement) is { } resume && resume != uint.MaxValue) Link(node, 0, Resolve((int) resume));
                            break;
                        }

                        if (node.Outputs.Count > 0 && node.Outputs[0].IsExec) Link(node, 0, Next(statement));
                        if (_castFailed.TryGetValue(node, out var failed)) Link(node, 1, Resolve(failed));
                        break;
                }
            }

            // wide enough for its title and for the pins facing each other on a row
            foreach (var node in _nodes) BlueprintNodeMetrics.Fit(node);

            var edges = Layout(entries, links);
            var graph = new BlueprintFunctionGraph
            {
                Name = function.Name,
                DisplayName = _isUbergraph ? $"EventGraph ({function.Name})" : function.Name,
                Signature = signature,
                IsUbergraph = _isUbergraph,
                IsEventStub = !_isUbergraph && context.Ubergraph != null && all.Any(s => UnwrapCall(s) is EX_FinalFunction call && call.StackNode.Name == context.Ubergraph.Name),
                Nodes = _nodes,
                Edges = edges,
                Note = bytecodeLength == 0
                    ? "no bytecode"
                    : $"{_nodes.Count} nodes" + (truncated ? $" (first {_MAX_STATEMENTS} statements only)" : string.Empty)
            };

            graph.CanvasWidth = _nodes.Count == 0 ? 600 : _nodes.Max(n => n.X + n.Width) + _MARGIN;
            graph.CanvasHeight = _nodes.Count == 0 ? 400 : _nodes.Max(n => n.Y + n.Height) + _MARGIN;
            return graph;
        }

        /// <summary>
        /// K2Node_Timeline: exec inputs driving the timeline component, Update / Finished / event track outputs
        /// entering the ubergraph through their callback functions, Direction and the track values as data.
        /// </summary>
        private IEnumerable<(BlueprintGraphNode, int, int)> TimelineNode(TimelineInfo timeline)
        {
            var node = NewNode(timeline.Name, EBlueprintNodeKind.Timeline, $"// timeline {timeline.Name}", -1, _EXEC_WIDTH);
            foreach (var input in new[] { "Play", "PlayFromStart", "Stop", "Reverse", "ReverseFromEnd", "SetNewTime" })
                node.Inputs.Add(new BlueprintPin { Name = BlueprintNodeDatabase.PinDisplayName(input, false), IsExec = true });
            node.Inputs.Add(new BlueprintPin { Name = BlueprintNodeDatabase.PinDisplayName("NewTime", false) });

            var outputs = new List<(BlueprintGraphNode, int, int)>();
            void ExecOutput(string name, string callback)
            {
                var pin = node.Outputs.Count;
                node.Outputs.Add(new BlueprintPin { Name = BlueprintNodeDatabase.PinDisplayName(name, false), IsExec = true });
                var entry = context.Events.FirstOrDefault(e => e.Function.Name == callback);
                if (entry != null) outputs.Add((node, pin, entry.Offset));
            }

            ExecOutput("Update", timeline.UpdateFunction);
            ExecOutput("Finished", timeline.FinishedFunction);
            foreach (var (track, callback) in timeline.EventTracks) ExecOutput(track, callback);

            _producers[timeline.DirectionProperty] = new Source(node, node.Outputs.Count, null);
            node.Outputs.Add(new BlueprintPin { Name = "Direction" });
            foreach (var (track, property) in timeline.ValueTracks)
            {
                _producers[property] = new Source(node, node.Outputs.Count, null);
                node.Outputs.Add(new BlueprintPin { Name = BlueprintNodeDatabase.PinDisplayName(track, false) });
            }

            _timelines[timeline.Name] = node;
            return outputs;
        }

        /// <summary>
        /// Turns one statement into its nodes. Returns the exec node it becomes, null when it only
        /// feeds data (a pure call, a write into a temporary) or is compiler plumbing.
        /// </summary>
        private BlueprintGraphNode Statement(KismetExpression statement, out bool isEnd)
        {
            isEnd = false;
            switch (statement)
            {
                case EX_JumpIfNot branch:
                {
                    // the success test of an impure cast is its Cast Failed output, not a Branch
                    if (branch.BooleanExpression is EX_VariableBase flag && _castSuccess.TryGetValue(flag.Variable.ToString(), out var cast))
                    {
                        _castFailed[cast] = (int) branch.CodeOffset;
                        return null;
                    }

                    var node = ExecNode("Branch", EBlueprintNodeKind.Branch, statement);
                    AddInput(node, "Condition", branch.BooleanExpression);
                    node.Outputs.Add(new BlueprintPin { Name = "True", IsExec = true });
                    node.Outputs.Add(new BlueprintPin { Name = "False", IsExec = true });
                    return node;
                }
                case EX_Jump or EX_ComputedJump or EX_Tracepoint or EX_WireTracepoint or EX_Breakpoint or EX_InstrumentationEvent:
                    return null;
                case EX_PushExecutionFlow:
                {
                    var node = ExecNode("Sequence", EBlueprintNodeKind.Flow, statement);
                    node.Outputs.Add(new BlueprintPin { Name = "Then 0", IsExec = true });
                    node.Outputs.Add(new BlueprintPin { Name = "Then 1", IsExec = true });
                    return node;
                }
                case EX_PopExecutionFlow:
                    isEnd = true; // the end of a Sequence output, the editor draws nothing there
                    return null;
                case EX_PopExecutionFlowIfNot pop:
                {
                    // KismetCompilerMisc: a GotoIfNot whose target node is missing becomes EndOfThreadIfNot,
                    // i.e. a Branch with nothing wired to its False pin
                    var node = ExecNode("Branch", EBlueprintNodeKind.Branch, statement);
                    AddInput(node, "Condition", pop.BooleanExpression);
                    node.Outputs.Add(new BlueprintPin { Name = "True", IsExec = true });
                    node.Outputs.Add(new BlueprintPin { Name = "False", IsExec = true });
                    return node;
                }
                case EX_Return:
                {
                    if (_isUbergraph)
                    {
                        isEnd = true;
                        return null;
                    }

                    var node = NewNode("Return Node", EBlueprintNodeKind.Return, Code(statement), statement.StatementIndex, _EXEC_WIDTH);
                    node.Inputs.Add(new BlueprintPin { IsExec = true });
                    foreach (var (name, source) in _returnValues)
                        Connect(node, AddPin(node, BlueprintNodeDatabase.PinDisplayName(name, context.IsBoolProperty(name, function))), source);
                    return node;
                }
                case EX_SetArray array when VariableNameOf(array.AssigningProperty) is { } arrayName && arrayName.StartsWith("K2Node_MakeArray_", StringComparison.Ordinal):
                {
                    var node = NewNode("Make Array", EBlueprintNodeKind.Pure, Code(statement), statement.StatementIndex, _DATA_WIDTH);
                    for (var i = 0; i < array.Elements.Length; i++) AddInput(node, $"[{i}]", array.Elements[i]);
                    node.Outputs.Add(new BlueprintPin { Name = "Array" });
                    _producers[arrayName] = new Source(node, 0, null);
                    _standalone.Add(node);
                    return null;
                }
            }

            var (variable, value) = SplitAssignment(statement);
            if (variable != null) return Assignment(statement, variable, value);

            var (call, target) = UnwrapCallWithTarget(statement);
            if (call != null)
            {
                if (TimelineInput(statement, call, target) is { } timeline) return timeline;
                if (FinishSpawning(call, null)) return null;

                var callee = ResolveCallee(call, target);
                if (callee.IsPure)
                {
                    // a pure call standing alone only matters for its out parameters
                    _standalone.Add(CallNode(call, target, statement, pure: true, assigned: null));
                    return null;
                }

                return CallNode(call, target, statement, pure: false, assigned: null);
            }

            switch (statement)
            {
                // K2Node_CreateDelegate: no exec pins, the bound function picked on the node
                case EX_BindDelegate bind:
                {
                    var node = NewNode("Create Event", EBlueprintNodeKind.Other, Code(statement), statement.StatementIndex, _DATA_WIDTH);
                    node.Subtitle = context.FunctionDisplay(bind.FunctionName.Text);
                    AddInput(node, "Object", bind.ObjectTerm);
                    node.Outputs.Add(new BlueprintPin { Name = "Event" });
                    if (VariableNameOf(bind.Delegate) is { } delegateVariable) _producers[delegateVariable] = new Source(node, 0, null);
                    _standalone.Add(node);
                    return null;
                }
                // K2Node_AddDelegate / K2Node_RemoveDelegate / K2Node_ClearDelegate
                case EX_AddMulticastDelegate add:
                    return DelegateNode("Bind Event to", add.Delegate, add.DelegateToAdd, statement);
                case EX_RemoveMulticastDelegate remove:
                    return DelegateNode("Unbind Event from", remove.Delegate, remove.DelegateToAdd, statement);
                case EX_ClearMulticastDelegate clear:
                    return DelegateNode("Unbind all Events from", clear.DelegateToClear, null, statement);
            }

            var generic = ExecNode(GenericTitle(statement), EBlueprintNodeKind.Other, statement);
            switch (statement)
            {
                case EX_SetArray array:
                    AddInput(generic, "Array", array.AssigningProperty);
                    for (var i = 0; i < array.Elements.Length; i++) AddInput(generic, $"[{i}]", array.Elements[i]);
                    break;
            }

            return generic;
        }

        /// <summary>
        /// "{Action} {Delegate}" with the Target the event dispatcher belongs to and the Event bound or unbound.
        /// </summary>
        private BlueprintGraphNode DelegateNode(string action, KismetExpression dispatcher, KismetExpression boundEvent, KismetExpression statement)
        {
            var (target, property) = DelegateParts(dispatcher);
            var node = ExecNode($"{action} {context.VariableDisplay(property, function)}", EBlueprintNodeKind.Other, statement);
            node.MemberQuery = VariableQuery(dispatcher);
            node.MemberKind = EMemberKind.Variable;
            var pin = AddPin(node, "Target");
            if (target != null) Connect(node, pin, Resolve(target, 1));
            else node.Inputs[pin].Value = "self";
            if (boundEvent != null) AddInput(node, "Event", boundEvent);
            return node;
        }

        /// <summary>The object an event dispatcher belongs to (null for self) and the dispatcher property.</summary>
        private static (KismetExpression Target, string Property) DelegateParts(KismetExpression dispatcher) => dispatcher switch
        {
            EX_Context { ContextExpression: EX_VariableBase member } memberContext => (memberContext.ObjectExpression, member.Variable.ToString()),
            EX_VariableBase variable => (null, variable.Variable.ToString()),
            _ => (null, SafeLine(dispatcher))
        };

        /// <summary>
        /// Timeline_0->PlayFromStart() is the "Play from Start" input of the Timeline_0 node.
        /// </summary>
        private BlueprintGraphNode TimelineInput(KismetExpression statement, KismetExpression call, KismetExpression target)
        {
            if (target is not EX_VariableBase variable || !_timelines.TryGetValue(variable.Variable.ToString(), out var node)) return null;

            var (name, _) = CallName(call);
            if (!_timelineInputs.TryGetValue(name, out var pin)) return null;

            if (name == "SetNewTime" && CallParameters(call) is [var time, ..]) Connect(node, 6, Resolve(time, 0));
            _redirectPin[statement] = pin;
            return node;
        }

        /// <summary>
        /// FinishSpawningActor closes the SpawnActor node BeginDeferredActorSpawnFromClass opened, it is not a node itself.
        /// </summary>
        private bool FinishSpawning(KismetExpression call, string assigned)
        {
            if (CallName(call).Name != "FinishSpawningActor" || CallParameters(call) is not [var actor, ..]) return false;

            var source = Resolve(actor, 0);
            if (source.Node == null || !_spawnNodes.ContainsKey(source.Node)) return false;

            if (assigned != null) _producers[assigned] = source;
            return true;
        }

        private BlueprintGraphNode Assignment(KismetExpression statement, KismetExpression variable, KismetExpression value)
        {
            var name = VariableNameOf(variable);
            var (call, target) = UnwrapCallWithTarget(value);

            // Make Struct: its members are written one by one into a temporary
            if (variable is EX_StructMemberContext { StructExpression: EX_VariableBase structVariable } member &&
                structVariable.Variable.ToString().StartsWith("K2Node_MakeStruct_", StringComparison.Ordinal))
            {
                var node = MakeStructNode(structVariable.Variable.ToString(), member);
                var memberName = member.Property.ToString();
                Connect(node, AddPin(node, BlueprintNodeDatabase.PinDisplayName(memberName, IsBoolName(memberName))), Resolve(value, 0));
                return null;
            }

            // a property exposed on spawn is a pin of the SpawnActor node
            if (variable is EX_Context { ContextExpression: EX_VariableBase exposed } spawned &&
                Resolve(spawned.ObjectExpression, _MAX_EXPRESSION_DEPTH - 1) is { Node: { } spawnNode } && _spawnNodes.ContainsKey(spawnNode))
            {
                var exposedName = exposed.Variable.ToString();
                Connect(spawnNode, AddPin(spawnNode, BlueprintNodeDatabase.PinDisplayName(exposedName, IsBoolName(exposedName))), Resolve(value, 0));
                return null;
            }

            // a temporary is the output pin of the node that computes it
            if (name != null && IsTemporary(name))
            {
                if (call != null)
                {
                    if (FinishSpawning(call, name)) return null;

                    // the compiler converts between pin types on its own (float/double, vector 2d/2f...), no node in the editor
                    if (name.EndsWith("_ImplicitCast", StringComparison.Ordinal) && CallParameters(call) is [var converted])
                    {
                        _producers[name] = Resolve(converted, 0);
                        return null;
                    }

                    var pure = ResolveCallee(call, target).IsPure;
                    var node = CallNode(call, target, statement, pure, assigned: name);
                    if (!pure) return node;

                    _standalone.Add(node);
                    return null;
                }

                if (value is EX_DynamicCast or EX_MetaCast or EX_ObjToInterfaceCast or EX_CrossInterfaceCast or EX_InterfaceToObjCast &&
                    name.StartsWith("K2Node_DynamicCast_As", StringComparison.Ordinal))
                    return CastStatement(statement, name, (EX_CastBase) value);

                _producers[name] = Resolve(value, 0);
                return null;
            }

            if (name != null && _outParameters.Contains(name))
            {
                _returnValues[name] = Resolve(value, 0);
                return null;
            }

            // SGraphNodeK2Var draws a variable set as "SET"
            var set = ExecNode("SET", EBlueprintNodeKind.Set, statement);
            set.MemberQuery = VariableQuery(variable);
            set.MemberKind = EMemberKind.Variable;
            if (variable is EX_Context { ContextExpression: EX_VariableBase memberVariable } memberContext)
            {
                AddInput(set, "Target", memberContext.ObjectExpression);
                var memberName = memberVariable.Variable.ToString();
                Connect(set, AddPin(set, context.VariableDisplay(memberName, function)), AsEnumerator(Resolve(value, 0), value, IsIntegerConstant(value) ? EnumOf(variable) : null));
            }
            else
            {
                var display = name != null ? context.VariableDisplay(name, function) : Clean(SafeLine(variable));
                AddInput(set, display, value, enumeration: IsIntegerConstant(value) ? EnumOf(variable) : null);
            }

            return set;
        }

        /// <summary>
        /// K2Node_DynamicCast with exec pins: the success flag the compiler writes next is tested by a jump,
        /// that jump is the Cast Failed output.
        /// </summary>
        private BlueprintGraphNode CastStatement(KismetExpression statement, string asVariable, EX_CastBase cast)
        {
            var next = _index + 1 < _all.Length ? _all[_index + 1] : null;
            var successFlag = next != null && SplitAssignment(next) is { Variable: { } flagVariable } && VariableNameOf(flagVariable) is { } flag &&
                              _branchedSuccessFlags.Contains(flag) ? flag : null;

            if (successFlag == null)
            {
                _producers[asVariable] = Resolve(cast, 0);
                return null;
            }

            var (title, asPin) = CastNames(cast);
            var node = NewNode(title, EBlueprintNodeKind.Cast, Code(statement), statement.StatementIndex, _EXEC_WIDTH);
            node.Inputs.Add(new BlueprintPin { IsExec = true });
            AddInput(node, "Object", cast.Target);
            node.Outputs.Add(new BlueprintPin { IsExec = true });
            node.Outputs.Add(new BlueprintPin { Name = "Cast Failed", IsExec = true });
            _producers[asVariable] = new Source(node, node.Outputs.Count, null);
            node.Outputs.Add(new BlueprintPin { Name = asPin });

            _castSuccess[successFlag] = node;
            _index++; // the flag write is part of this node
            return node;
        }

        private BlueprintGraphNode MakeStructNode(string variable, EX_StructMemberContext member)
        {
            if (_makeStructs.TryGetValue(variable, out var node)) return node;

            var structName = StructNameOf(member) ?? _numberSuffix.Replace(variable["K2Node_MakeStruct_".Length..], string.Empty);
            var display = BlueprintNodeDatabase.StructDisplayName(structName);
            node = NewNode($"Make {display}", EBlueprintNodeKind.Struct, $"// {variable}", -1, _DATA_WIDTH);
            node.Outputs.Add(new BlueprintPin { Name = display });
            _producers[variable] = new Source(node, 0, null);
            _makeStructs[variable] = node;
            _standalone.Add(node);
            return node;
        }

        /// <summary>
        /// A function call as a node: one pin per argument named after the function's parameters, hidden pins
        /// (world context, latent info) left out, the value it returns goes into <paramref name="assigned"/>.
        /// </summary>
        private BlueprintGraphNode CallNode(KismetExpression call, KismetExpression target, KismetExpression statement, bool pure, string assigned)
        {
            var callee = ResolveCallee(call, target);
            var info = callee.Info;
            var latent = !pure && statement != null && (info?.IsLatent ?? false || LatentResume(statement) != null);
            // UK2Node_CallFunction::ShouldDrawCompact, pure or not
            var compact = !string.IsNullOrEmpty(info?.CompactNodeTitle);
            var kind = pure ? EBlueprintNodeKind.Pure : latent ? EBlueprintNodeKind.Latent : EBlueprintNodeKind.Call;
            var arguments = CallParameters(call);

            // the self pin is hidden on static functions (a library call runs on its class default object) and on parent calls
            var showSelf = !callee.IsStatic && !callee.IsParentCall && !(target != null && ContextTarget(target) == null);
            var (title, subtitle) = CallTitle(callee, arguments, showSelf);
            if (call is EX_CallMulticastDelegate multicast)
            {
                // K2Node_CallDelegate
                (target, var property) = DelegateParts(multicast.Delegate);
                title = $"Call {context.VariableDisplay(property, function)}";
                subtitle = null;
                showSelf = true;
            }
            var node = new BlueprintGraphNode
            {
                Id = $"n{_nextId++}",
                Title = compact ? CompactTitle(info.CompactNodeTitle) : title,
                Subtitle = subtitle,
                Kind = kind,
                IsCompact = compact,
                Code = Code(statement ?? call),
                Offset = statement?.StatementIndex ?? -1,
                Width = compact ? _COMPACT_WIDTH : pure ? _DATA_WIDTH : _EXEC_WIDTH
            };
            _nodes.Add(node);

            if (!pure)
            {
                node.Inputs.Add(new BlueprintPin { IsExec = true });
                // K2Node_CallFunction renames the then pin of a latent function to "Completed"
                node.Outputs.Add(new BlueprintPin { Name = latent ? "Completed" : null, IsExec = true });
            }

            if (showSelf)
            {
                var pin = AddPin(node, compact ? string.Empty : "Target");
                if (target != null && ContextTarget(target) != null) Connect(node, pin, Resolve(target, 1));
                else node.Inputs[pin].Value = "self";
            }

            var prefix = $"CallFunc_{callee.Name}_";
            for (var i = 0; i < arguments.Length; i++)
            {
                // K2Node_GetSubsystem keeps its class on the node, not on a pin
                if (IsSubsystemGetter(callee)) break;

                var argument = arguments[i];
                var parameter = i < callee.Parameters.Count ? callee.Parameters[i] : null;
                if (parameter?.IsHidden ?? false) continue;
                if (argument is EX_StructConst structConst && structConst.Struct.Name.Contains("LatentActionInfo", StringComparison.Ordinal))
                    continue;

                var variable = argument is EX_VariableBase v ? v.Variable.ToString() : null;

                // without a signature the compiler's temporaries still carry the parameter name:
                // CallFunc_{Function}_{Parameter} for an out pin, ..._{Parameter}_ImplicitCast for a converted input
                string hinted = null;
                var implicitCast = variable != null && variable.EndsWith("_ImplicitCast", StringComparison.Ordinal);
                if (parameter == null && variable != null && variable.StartsWith(prefix, StringComparison.Ordinal))
                    hinted = !implicitCast ? variable[prefix.Length..]
                        : variable.Length > prefix.Length + "_ImplicitCast".Length ? variable[prefix.Length..^"_ImplicitCast".Length] : null;

                // an SDK dump does not mark outputs: the compiler stores an out pin in CallFunc_{Function}_{Parameter}
                var isOut = (parameter?.IsOut ?? false) || (parameter == null && hinted != null && !implicitCast) ||
                            (parameter is { IsDirectionUnknown: true } && variable == prefix + parameter.Name);

                var pinName = compact ? string.Empty
                    : parameter != null ? BlueprintNodeDatabase.PinDisplayName(parameter.DisplayName ?? parameter.Name, parameter.IsBool)
                    : hinted != null ? BlueprintNodeDatabase.PinDisplayName(hinted, IsBoolName(hinted))
                    : arguments.Length == 1 ? "In" : $"Arg {i + 1}";

                if (isOut && variable != null)
                {
                    _producers[variable] = new Source(node, node.Outputs.Count, null);
                    node.Outputs.Add(new BlueprintPin { Name = pinName });
                    continue;
                }

                AddInput(node, pinName, argument, enumeration: ArgumentEnum(callee, arguments, i));
            }

            // a pure call read as a value always returns one, a standalone one only has its out pins
            if (assigned != null || callee.HasReturnValue || (pure && statement == null))
            {
                var pin = node.Outputs.Count;
                var returnName = compact ? string.Empty : BlueprintNodeDatabase.PinDisplayName(info?.ReturnDisplayName ?? "ReturnValue", false);
                node.Outputs.Add(new BlueprintPin { Name = returnName });
                if (assigned != null) _producers[assigned] = new Source(node, pin, null);
            }

            // a call into another function of this class can be followed
            if (call is not (EX_CallMath or EX_CallMulticastDelegate) && context.Functions.ContainsKey(callee.Name))
                node.TargetFunction = callee.Name;

            if (context.Ubergraph != null && callee.Name == context.Ubergraph.Name && arguments is [EX_IntConst entry, ..])
            {
                node.TargetFunction = callee.Name;
                node.TargetOffset = entry.Value;
            }

            if (info is { Class: "GameplayStatics", Name: "BeginDeferredActorSpawnFromClass" }) _spawnNodes[node] = assigned;

            // what "find usages" of this node looks for: the function, the event dispatcher of a delegate call
            if (call is EX_CallMulticastDelegate dispatcherCall)
            {
                node.MemberQuery = VariableQuery(dispatcherCall.Delegate);
                node.MemberKind = EMemberKind.Variable;
            }
            else if (context.Ubergraph == null || callee.Name != context.Ubergraph.Name)
            {
                node.MemberQuery = call switch
                {
                    EX_FinalFunction final => MemberUsageLookup.QueryForFunction(final.StackNode),
                    // a function library runs on its class default object
                    _ when target is EX_ObjectConst { Value: { IsNull: false } instance } => MemberUsageLookup.QueryForObjectMember(instance, callee.Name),
                    // one of our own functions, or the parent one it overrides
                    _ when callee.Owner == null && target is null or EX_Self && context.Functions.TryGetValue(callee.Name, out var own) =>
                        own.SuperStruct is { IsNull: false } overridden
                            ? MemberUsageLookup.QueryForFunction(overridden)
                            : MemberUsageLookup.QueryFor(context.Class.Owner?.Name, context.ClassName, callee.Name),
                    _ => null
                };
                node.MemberQuery ??= MemberUsageLookup.QueryFor(callee.Owner == context.ClassName ? context.Class.Owner?.Name : null, callee.Owner, callee.Name);
                node.MemberKind = EMemberKind.Function;
            }

            return node;
        }

        /// <summary>
        /// Title and "Target is" line of a call node, with the K2 nodes that compile down to one call.
        /// </summary>
        private (string Title, string Subtitle) CallTitle(Callee callee, KismetExpression[] arguments, bool showSelf)
        {
            var info = callee.Info;
            switch (info?.Class, callee.Name)
            {
                // K2Node_SpawnActorFromClass: "SpawnActor {ClassName}", "SpawnActor" with a wired class, "SpawnActor NONE" without one
                case ("GameplayStatics", "BeginDeferredActorSpawnFromClass"):
                    return (ClassArgument(arguments, 1) is { } spawned ? $"SpawnActor {spawned}"
                        : IsNoClass(arguments, 1) ? "SpawnActor NONE" : "SpawnActor", null);
                // K2Node_CreateWidget: "Create {ClassName} Widget", the base title with a wired class
                case ("WidgetBlueprintLibrary", "Create"):
                    return (ClassArgument(arguments, 1) is { } widget ? $"Create {widget} Widget" : "Create Widget", null);
                // K2Node_GetSubsystem: the subsystem class's display name
                case ("SubsystemBlueprintLibrary", _) when IsSubsystemGetter(callee):
                    return (ClassArgument(arguments, 1) ?? BlueprintNodeDatabase.FunctionDisplayName(callee.Name, info), null);
            }

            string title;
            if (callee.IsParentCall) // K2Node_CallParentFunction
                return ($"Parent: {(info != null ? BlueprintNodeDatabase.FunctionDisplayName(callee.Name, info) : context.FunctionDisplay(callee.Name))}", null);

            if (callee.Name.StartsWith("Call ", StringComparison.Ordinal)) title = callee.Name; // event dispatcher
            else if (info != null) title = BlueprintNodeDatabase.FunctionDisplayName(callee.Name, info);
            else if (context.Functions.ContainsKey(callee.Name)) title = context.FunctionDisplay(callee.Name);
            else title = BlueprintNodeDatabase.NameToDisplayString(callee.Name, false);

            // K2Node_CallFunction::GetFunctionContextString, only when the self pin shows
            string subtitle = null;
            if (showSelf)
            {
                var owner = callee.Owner ?? info?.Class ?? (context.Functions.ContainsKey(callee.Name) ? context.ClassName : null);
                if (owner != null) subtitle = $"Target is {BlueprintNodeDatabase.ClassDisplayName(owner)}";
            }

            return (title, subtitle);
        }

        private static bool IsSubsystemGetter(Callee callee) =>
            callee.Info?.Class == "SubsystemBlueprintLibrary" && callee.Name.StartsWith("Get", StringComparison.Ordinal) &&
            callee.Name.EndsWith("Subsystem", StringComparison.Ordinal);

        private static bool IsNoClass(KismetExpression[] arguments, int index) =>
            arguments.Length > index && arguments[index] is EX_NoObject or EX_ObjectConst { Value.IsNull: true };

        /// <summary>Display name of the class a spawn / create node is given, a constant on the call.</summary>
        private static string ClassArgument(KismetExpression[] arguments, int index)
        {
            if (arguments.Length <= index || arguments[index] is not EX_ObjectConst { Value: { IsNull: false } value }) return null;
            return BlueprintNodeDatabase.ClassDisplayName(ObjectName(value));
        }

        private Callee ResolveCallee(KismetExpression call, KismetExpression target)
        {
            var (name, owner) = CallName(call);

            // a virtual call does not name its class, the type of the object it runs on does
            var (targetClass, targetClassName) = owner == null ? TargetClass(target) : default;
            var key = $"{owner ?? targetClass?.Name ?? targetClassName}.{name}";
            if (_callees.TryGetValue(key, out var cached)) return cached;

            var info = BlueprintNodeDatabase.Find(owner, name);
            UFunction typed = null;
            if (info == null && targetClass != null)
            {
                (info, typed, var declaring) = FindOnClass(targetClass, name);
                owner ??= declaring;
            }
            else if (info == null && targetClassName != null)
            {
                info = BlueprintNodeDatabase.FindInHierarchy(targetClassName, name);
                owner ??= info?.Class;
            }

            // calling the implementation this class overrides: a final call into a parent class under one of our own names
            context.Functions.TryGetValue(name, out var own);
            var isParentCall = call is EX_FinalFunction and not EX_CallMath && own != null && owner != null && owner != context.ClassName;
            if (own != null) info ??= BlueprintContext.OverriddenFunction(own);

            var callee = typed;
            if (info == null && callee == null)
            {
                try
                {
                    if (call is EX_FinalFunction final && final.StackNode.TryLoad(out var loaded)) callee = loaded as UFunction;
                }
                catch (Exception)
                {
                    // native functions do not load
                }

                // our own function, or the parent implementation of one we override (same parameters)
                if (callee == null && (isParentCall || owner == null || owner == context.ClassName)) callee = own;

                // an event dispatcher call is named by its signature function
                if (callee == null && call is EX_CallMulticastDelegate multicast)
                {
                    try
                    {
                        if (multicast.StackNode.TryLoad(out var signature)) callee = signature as UFunction;
                    }
                    catch (Exception)
                    {
                        // native signature
                    }
                }
                if (callee == null && owner == null) info = BlueprintNodeDatabase.FindByName(name);
            }

            List<BlueprintParameterInfo> parameters;
            bool isStatic, isPure, hasReturn;
            if (info != null)
            {
                parameters = info.Parameters.ToList();
                isStatic = info.IsStatic || call is EX_CallMath;
                isPure = info.IsPure;
                hasReturn = info.HasReturnValue;
            }
            else if (callee != null)
            {
                var properties = Parameters(callee).ToList();
                parameters = properties.Where(p => !p.PropertyFlags.HasFlag(EPropertyFlags.ReturnParm))
                    .Select(p => new BlueprintParameterInfo(p.Name.Text,
                        p.PropertyFlags.HasFlag(EPropertyFlags.OutParm) && !p.PropertyFlags.HasFlag(EPropertyFlags.ReferenceParm),
                        p is FBoolProperty, false, null))
                    .ToList();
                isStatic = callee.FunctionFlags.HasFlag(EFunctionFlags.FUNC_Static) || call is EX_CallMath;
                isPure = callee.FunctionFlags.HasFlag(EFunctionFlags.FUNC_BlueprintPure);
                hasReturn = properties.Any(p => p.PropertyFlags.HasFlag(EPropertyFlags.ReturnParm));
            }
            else
            {
                // a native function outside the engine (game code): no signature to go by
                parameters = [];
                isStatic = call is EX_CallMath;
                isPure = IsPureCall(call);
                hasReturn = false;
            }

            var result = new Callee(name, owner, info, callee, isStatic, isPure, parameters, hasReturn, isParentCall);
            _callees[key] = result;
            return result;
        }

        /// <summary>
        /// Class of the object a call runs on: the declared type of the variable (members, locals and the
        /// compiler's temporaries are all typed properties), null for self or when it is not a plain variable.
        /// </summary>
        private (FPackageIndex Index, string Name) TargetClass(KismetExpression target)
        {
            switch (target)
            {
                case EX_InterfaceContext interfaceContext:
                    return TargetClass(interfaceContext.InterfaceValue);
                case EX_VariableBase variable:
                {
                    var name = variable.Variable.ToString();
                    return context.FindProperty(name, function) switch
                    {
                        FInterfaceProperty interfaceProperty => (interfaceProperty.InterfaceClass, null),
                        FObjectProperty objectProperty => (objectProperty.PropertyClass, null),
                        null => (null, BlueprintNodeDatabase.MemberType(context.NativeParent, name)), // a member of the native parent (Mesh...)
                        _ => default
                    };
                }
                default:
                    return default;
            }
        }

        /// <summary>
        /// The function <paramref name="name"/> as a class sees it: in the table for native classes (with their parents
        /// from the SDK dump), in the class itself for blueprints, climbing to the parent class until one declares it.
        /// </summary>
        private static (BlueprintFunctionInfo Info, UFunction Function, string Owner) FindOnClass(FPackageIndex type, string name)
        {
            for (var depth = 0; type is { IsNull: false } && depth < 16; depth++)
            {
                var info = BlueprintNodeDatabase.FindInHierarchy(type.Name, name);
                if (info != null) return (info, null, info.Class);

                try
                {
                    if (!type.TryLoad(out var loaded) || loaded is not UClass blueprint) return (null, null, null);

                    foreach (var (functionName, pointer) in blueprint.FuncMap ?? [])
                    {
                        if (functionName.Text == name && pointer.TryLoad(out var export) && export is UFunction found)
                            return (null, found, blueprint.Name);
                    }

                    type = blueprint.SuperStruct;
                }
                catch (Exception)
                {
                    return (null, null, null);
                }
            }

            return (null, null, null);
        }

        /// <summary>
        /// What an expression read by a pin comes from: a wire from another node, or a value typed on the pin.
        /// </summary>
        private Source Resolve(KismetExpression expression, int depth)
        {
            if (expression == null || depth > _MAX_EXPRESSION_DEPTH) return Source.Of(Value(expression));

            switch (expression)
            {
                case EX_Self:
                    return Source.Of("self");
                case EX_VariableBase variable:
                {
                    var name = variable.Variable.ToString();
                    if (_producers.TryGetValue(name, out var produced)) return produced;
                    if (IsTemporary(name)) return Source.Later(name);

                    var source = GetNode(name);
                    source.Node.MemberQuery = VariableQuery(variable);
                    source.Node.MemberKind = EMemberKind.Variable;
                    return source;
                }
                case EX_Context { ContextExpression: EX_VariableBase member } memberContext:
                {
                    var display = context.VariableDisplay(member.Variable.ToString(), function);
                    var get = NewNode(display, EBlueprintNodeKind.Get, Code(expression), -1, _DATA_WIDTH);
                    get.MemberQuery = VariableQuery(member);
                    get.MemberKind = EMemberKind.Variable;
                    if (ContextTarget(memberContext.ObjectExpression) != null) AddInput(get, "Target", memberContext.ObjectExpression, depth);
                    get.Outputs.Add(new BlueprintPin { Name = display });
                    return new Source(get, 0, null);
                }
                case EX_Cast primitive:
                    return Resolve(primitive.Target, depth + 1); // implicit conversions are not nodes in the editor
                case EX_InterfaceContext interfaceContext:
                    return Resolve(interfaceContext.InterfaceValue, depth + 1);
                case EX_CastBase cast:
                {
                    // pure K2Node_DynamicCast
                    var (title, asPin) = CastNames(cast);
                    var node = NewNode(title, EBlueprintNodeKind.Cast, Code(expression), -1, _DATA_WIDTH);
                    AddInput(node, "Object", cast.Target, depth);
                    node.Outputs.Add(new BlueprintPin { Name = asPin });
                    node.Outputs.Add(new BlueprintPin { Name = "Success" });
                    return new Source(node, 0, null);
                }
                case EX_StructMemberContext member:
                {
                    // K2Node_BreakStruct, one node per struct read with a pin per member used
                    var key = SafeLine(member.StructExpression);
                    var memberName = member.Property.ToString();
                    if (!_breakStructs.TryGetValue(key, out var node))
                    {
                        var display = BlueprintNodeDatabase.StructDisplayName(StructNameOf(member) ?? "Struct");
                        node = NewNode($"Break {display}", EBlueprintNodeKind.Struct, Code(expression), -1, _DATA_WIDTH);
                        AddInput(node, display, member.StructExpression, depth);
                        _breakStructs[key] = node;
                    }

                    var pinName = BlueprintNodeDatabase.PinDisplayName(memberName, IsBoolName(memberName));
                    var index = node.Outputs.FindIndex(p => p.Name == pinName);
                    if (index < 0)
                    {
                        index = node.Outputs.Count;
                        node.Outputs.Add(new BlueprintPin { Name = pinName });
                    }

                    return new Source(node, index, null);
                }
                case EX_ArrayGetByRef get:
                {
                    // KismetArrayLibrary::Array_Get, compact "GET"
                    var node = new BlueprintGraphNode
                    {
                        Id = $"n{_nextId++}", Title = "GET", Kind = EBlueprintNodeKind.Pure, IsCompact = true,
                        Code = Code(expression), Width = _COMPACT_WIDTH
                    };
                    _nodes.Add(node);
                    AddInput(node, string.Empty, get.ArrayVariable, depth);
                    AddInput(node, string.Empty, get.ArrayIndex, depth);
                    node.Outputs.Add(new BlueprintPin { Name = string.Empty });
                    return new Source(node, 0, null);
                }
                case EX_SwitchValue select:
                {
                    // K2Node_Select
                    var node = NewNode("Select", EBlueprintNodeKind.Pure, Code(expression), -1, _DATA_WIDTH);
                    var cases = select.Cases ?? [];
                    for (var i = 0; i < cases.Length; i++) AddInput(node, $"Option {i}", cases[i].CaseTerm, depth);
                    AddInput(node, "Index", select.IndexTerm, depth);
                    node.Outputs.Add(new BlueprintPin { Name = "Return Value" });
                    return new Source(node, 0, null);
                }
            }

            var (call, target) = UnwrapCallWithTarget(expression);
            if (call != null)
            {
                var node = CallNode(call, target, null, pure: true, assigned: null);
                return new Source(node, node.Outputs.Count - 1, null);
            }

            return Source.Of(Value(expression));
        }

        private void AddInput(BlueprintGraphNode node, string name, KismetExpression expression, int depth = 0, Dictionary<long, string> enumeration = null)
        {
            var pin = AddPin(node, name);
            Connect(node, pin, AsEnumerator(Resolve(expression, depth + 1), expression, enumeration));
        }

        /// <summary>An enum pin shows its enumerator, the bytecode only has the byte.</summary>
        private static Source AsEnumerator(Source source, KismetExpression expression, Dictionary<long, string> enumeration)
        {
            if (enumeration == null || source.Node != null || source.Pending != null) return source;

            long? value = expression switch
            {
                EX_ByteConst b => b.Value,
                EX_IntConst i => i.Value,
                EX_Int64Const l => l.Value,
                EX_IntZero => 0,
                EX_IntOne => 1,
                _ => null
            };
            return value is { } number && enumeration.TryGetValue(number, out var member) ? Source.Of(member) : source;
        }

        private static bool IsIntegerConstant(KismetExpression expression) =>
            expression is EX_ByteConst or EX_IntConst or EX_Int64Const or EX_IntZero or EX_IntOne;

        /// <summary>Enumerators of the enum a variable holds: a local, a member of this class, or of another one by its field path.</summary>
        private Dictionary<long, string> EnumOf(KismetExpression expression)
        {
            var pointer = expression switch
            {
                EX_Context memberContext => memberContext.ContextExpression is EX_VariableBase member ? member.Variable : null,
                EX_StructMemberContext structMember => structMember.Property,
                EX_VariableBase variable => variable.Variable,
                _ => null
            };
            var name = pointer?.ToString();
            if (string.IsNullOrEmpty(name) || name == "None") return null;

            var owner = pointer.New?.ResolvedOwner;
            if (owner is { IsNull: false })
            {
                try
                {
                    if (owner.TryLoad(out var loaded) && loaded is UStruct type and not UScriptClass && type.GetProperty(name, out var field))
                        return field is FProperty property ? context.EnumMembers(property) : null;
                }
                catch (Exception)
                {
                    // a native class or struct
                }

                if (context.MappedEnum(owner.Name, name) is { } mapped) return mapped;
            }

            return context.FindProperty(name, function) is { } found ? context.EnumMembers(found) : null;
        }

        /// <summary>
        /// Enum of a call argument: its parameter's type, or for a byte comparison (K2Node_EnumEquality, Switch on Enum)
        /// the type of what it is compared against.
        /// </summary>
        private Dictionary<long, string> ArgumentEnum(Callee callee, KismetExpression[] arguments, int index)
        {
            if (!IsIntegerConstant(arguments[index])) return null;

            var parameter = callee.Function == null ? null
                : Parameters(callee.Function).Where(p => !p.PropertyFlags.HasFlag(EPropertyFlags.ReturnParm)).ElementAtOrDefault(index);
            if (parameter != null && context.EnumMembers(parameter) is { } declared) return declared;

            return arguments.Length == 2 && callee.Name.EndsWith("_ByteByte", StringComparison.Ordinal) ? EnumOf(arguments[1 - index]) : null;
        }

        private static int AddPin(BlueprintGraphNode node, string name)
        {
            node.Inputs.Add(new BlueprintPin { Name = name });
            return node.Inputs.Count - 1;
        }

        private void Connect(BlueprintGraphNode node, int pin, Source source)
        {
            if (source.Pending != null) _pendingLinks.Add((node, pin, source.Pending));
            else if (source.Node == null) node.Inputs[pin].Value = source.Literal;
            else _dataLinks.Add(new DataLink(source.Node, source.Pin, node, pin));
        }

        private Source GetNode(string name)
        {
            var display = context.VariableDisplay(name, function);
            var get = NewNode(display, EBlueprintNodeKind.Get, name, -1, _DATA_WIDTH);
            get.Outputs.Add(new BlueprintPin { Name = display });
            return new Source(get, 0, null);
        }

        /// <summary>
        /// Once every statement is read, the temporaries read too early are wired to whatever wrote them.
        /// </summary>
        private void ConnectPending()
        {
            foreach (var (node, pin, temporary) in _pendingLinks)
            {
                var name = temporary;
                Source source = default;
                for (var depth = 0; depth < 16; depth++)
                {
                    if (!_producers.TryGetValue(name, out source))
                    {
                        source = GetNode(name);
                        break;
                    }

                    if (source.Pending == null) break;
                    name = source.Pending;
                }

                if (source.Pending != null) source = GetNode(name);
                Connect(node, pin, source);
            }

            _pendingLinks.Clear();
        }

        private BlueprintGraphNode ExecNode(string title, EBlueprintNodeKind kind, KismetExpression statement)
        {
            var node = NewNode(title, kind, Code(statement), statement.StatementIndex, _EXEC_WIDTH);
            node.Inputs.Add(new BlueprintPin { IsExec = true });
            if (kind != EBlueprintNodeKind.Branch && kind != EBlueprintNodeKind.Flow)
                node.Outputs.Add(new BlueprintPin { IsExec = true });
            return node;
        }

        private BlueprintGraphNode NewNode(string title, EBlueprintNodeKind kind, string code, int offset, double width)
        {
            var node = new BlueprintGraphNode
            {
                Id = $"n{_nextId++}",
                Title = title,
                Kind = kind,
                Code = code,
                Offset = offset,
                Width = width
            };

            _nodes.Add(node);
            return node;
        }

        /// <summary>
        /// Positions: exec nodes on lanes from left to right, the data nodes each one reads packed
        /// underneath it and to its left, the way pure nodes sit in the editor.
        /// </summary>
        private List<BlueprintGraphEdge> Layout(List<BlueprintGraphNode> entries, Dictionary<BlueprintGraphNode, List<ExecLink>> links)
        {
            var execNodes = _nodes.Where(n => links.ContainsKey(n)).ToList();
            var cell = new Dictionary<BlueprintGraphNode, (int Column, int Row)>();
            var rowEnd = new List<int>();

            int Allocate(int column, int below)
            {
                for (var row = Math.Max(0, below); row < rowEnd.Count; row++)
                {
                    if (rowEnd[row] < column) return row;
                }

                rowEnd.Add(-1);
                return rowEnd.Count - 1;
            }

            void Walk(BlueprintGraphNode start)
            {
                if (cell.ContainsKey(start)) return;

                var stack = new Stack<(BlueprintGraphNode Node, int Column, int Row, int Parent)>();
                stack.Push((start, 0, -1, -1));
                while (stack.Count > 0)
                {
                    var (node, column, row, parent) = stack.Pop();
                    if (cell.ContainsKey(node)) continue;

                    if (row < 0 || rowEnd[row] >= column) row = Allocate(column, parent + 1);
                    cell[node] = (column, row);
                    rowEnd[row] = Math.Max(rowEnd[row], column);

                    var next = links.GetValueOrDefault(node, []).Select(l => _nodes[l.Target]).Where(n => !cell.ContainsKey(n)).Distinct().ToList();
                    for (var i = next.Count - 1; i >= 1; i--) stack.Push((next[i], column + 1, -1, row));
                    if (next.Count > 0) stack.Push((next[0], column + 1, row, row));
                }
            }

            foreach (var entry in entries) Walk(entry);
            foreach (var node in execNodes) Walk(node);

            // data blocks, relative to the exec node that owns them
            var inputsOf = _dataLinks.GroupBy(l => l.To).ToDictionary(g => g.Key, g => g.OrderBy(l => l.ToPin).Select(l => l.From).ToList());
            var placed = new HashSet<BlueprintGraphNode>(execNodes);
            var relative = new Dictionary<BlueprintGraphNode, (BlueprintGraphNode Owner, int Depth, double Y)>();
            var blocks = new Dictionary<BlueprintGraphNode, (double Left, double Height)>();

            // distance from the owner's left edge to the right edge of each column of its data block,
            // every column as wide as its widest node
            var columnRight = new Dictionary<BlueprintGraphNode, double[]>();

            // pure statements nobody reads hang under the exec node that follows them
            var extraRoots = _standaloneOwner.GroupBy(pair => pair.Value).ToDictionary(g => g.Key, g => g.Select(pair => pair.Key).ToList());

            foreach (var owner in execNodes.OrderBy(n => cell[n].Row).ThenBy(n => cell[n].Column))
            {
                var cursor = 0.0;
                var columnWidth = new List<double> { 0 };

                void Place(BlueprintGraphNode node, int depth)
                {
                    if (!placed.Add(node)) return;

                    var start = cursor;
                    foreach (var input in inputsOf.GetValueOrDefault(node) ?? []) Place(input, depth + 1);
                    relative[node] = (owner, depth, start);
                    cursor = Math.Max(cursor, start + node.Height + _DATA_GAP_Y);
                    while (columnWidth.Count <= depth) columnWidth.Add(0);
                    columnWidth[depth] = Math.Max(columnWidth[depth], node.Width);
                }

                foreach (var input in inputsOf.GetValueOrDefault(owner) ?? []) Place(input, 1);
                foreach (var extra in extraRoots.GetValueOrDefault(owner) ?? []) Place(extra, 1);

                var right = new double[columnWidth.Count];
                var left = 0.0;
                for (var depth = 1; depth < columnWidth.Count; depth++)
                {
                    right[depth] = left + _DATA_GAP_X / 2;
                    left += columnWidth[depth] + _DATA_GAP_X;
                }

                columnRight[owner] = right;
                blocks[owner] = (left, cursor);
            }

            var rows = rowEnd.Count;
            var rowHeight = new double[rows];
            foreach (var (node, (_, row)) in cell)
            {
                var height = blocks.GetValueOrDefault(node).Height;
                rowHeight[row] = Math.Max(rowHeight[row], node.Height + (height > 0 ? _BLOCK_GAP + height : 0));
            }

            var rowStart = new double[rows];
            for (var i = 0; i < rows; i++) rowStart[i] = i == 0 ? _MARGIN : rowStart[i - 1] + rowHeight[i - 1] + _ROW_GAP;

            // each node right of the nodes that flow into it and of whatever sits before it on its row,
            // with room on its left for the data nodes it reads
            var predecessors = new Dictionary<BlueprintGraphNode, List<BlueprintGraphNode>>();
            foreach (var (node, list) in links)
            foreach (var link in list)
            {
                var target = _nodes[link.Target];
                if (!cell.ContainsKey(target) || !cell.ContainsKey(node) || cell[target].Column <= cell[node].Column) continue; // a loop back

                if (!predecessors.TryGetValue(target, out var from)) predecessors[target] = from = [];
                from.Add(node);
            }

            var rowRight = new double[rows];
            for (var i = 0; i < rows; i++) rowRight[i] = _MARGIN - _COLUMN_GAP;

            foreach (var (node, (_, row)) in cell.OrderBy(pair => pair.Value.Column))
            {
                var left = blocks.GetValueOrDefault(node).Left;
                var position = Math.Max(_MARGIN + left, rowRight[row] + _COLUMN_GAP + left);
                foreach (var from in predecessors.GetValueOrDefault(node) ?? [])
                    position = Math.Max(position, from.X + from.Width + _COLUMN_GAP + left);

                node.X = position;
                node.Y = rowStart[row];
                rowRight[row] = position + node.Width;
            }

            foreach (var (node, (owner, depth, y)) in relative)
            {
                node.X = owner.X - columnRight[owner][depth] - node.Width; // right aligned in its column
                node.Y = owner.Y + owner.Height + _BLOCK_GAP + y;
            }

            // whatever is left (a read nothing wires to) goes in a strip below everything
            var bottom = _nodes.Count == 0 ? _MARGIN : _nodes.Where(placed.Contains).Select(n => n.Y + n.Height).DefaultIfEmpty(_MARGIN).Max() + _ROW_GAP;
            var x = _MARGIN;
            foreach (var node in _nodes.Where(n => !placed.Contains(n)))
            {
                node.X = x;
                node.Y = bottom;
                x += node.Width + _DATA_GAP_X;
            }

            var edges = new List<BlueprintGraphEdge>();
            foreach (var (node, list) in links)
            {
                foreach (var link in list)
                {
                    var target = _nodes[link.Target];
                    edges.Add(Wire(node.X + node.Width, node.OutputY(link.FromPin), target.X, target.InputY(link.ToPin), EBlueprintEdgeKind.Exec));
                }
            }

            foreach (var link in _dataLinks)
                edges.Add(Wire(link.From.X + link.From.Width, link.From.OutputY(link.FromPin), link.To.X, link.To.InputY(link.ToPin), EBlueprintEdgeKind.Data));

            return edges;
        }

        private static string Code(KismetExpression expression)
        {
            var line = SafeLine(expression);
            return $"// offset {expression.StatementIndex}, {expression.GetType().Name}\n{Clean(line)};";
        }
    }

    /// <summary>
    /// UK2Node_CallFunction::GetCompactNodeTitle draws the programmer symbols with their common glyphs.
    /// </summary>
    private static string CompactTitle(string title) => title switch
    {
        "*" => "×",
        "/" => "÷",
        "->" => "•",
        _ => title
    };

    /// <summary>
    /// K2Node_DynamicCast: "Cast To {TargetName}" (a blueprint class without its _C), result pin "As {DisplayName}".
    /// </summary>
    private static (string Title, string AsPin) CastNames(EX_CastBase cast)
    {
        var type = cast.ClassPtr?.Name ?? "Object";
        var targetName = type.EndsWith("_C", StringComparison.Ordinal) ? type[..^2] : type;
        return ($"Cast To {targetName}", BlueprintNodeDatabase.PinDisplayName($"As{BlueprintNodeDatabase.ClassDisplayName(type)}", false));
    }

    /// <summary>Struct a member access belongs to, from the owner of the member's field path.</summary>
    private static string StructNameOf(EX_StructMemberContext member)
    {
        try
        {
            var owner = member.Property?.New?.ResolvedOwner;
            return owner is { IsNull: false } ? owner.Name : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Struct members carry no type in the bytecode, a bool follows the bName convention.</summary>
    private static bool IsBoolName(string name) => name.Length > 1 && name[0] == 'b' && char.IsUpper(name[1]);

    private static BlueprintGraphEdge Wire(double x1, double y1, double x2, double y2, EBlueprintEdgeKind kind)
    {
        // a wire going back to the left loops around instead of crossing the nodes
        var controlOffset = x2 > x1 ? Math.Max(30, (x2 - x1) / 2) : Math.Max(120, (x1 - x2) / 3);
        return new BlueprintGraphEdge
        {
            Kind = kind,
            Geometry = string.Create(CultureInfo.InvariantCulture,
                $"M {x1},{y1} C {x1 + controlOffset},{y1} {x2 - controlOffset},{y2} {x2},{y2}")
        };
    }

    private static readonly HashSet<string> _pureLibraries = new(StringComparer.Ordinal)
    {
        "KismetMathLibrary", "KismetStringLibrary", "KismetTextLibrary", "KismetArrayLibrary", "BlueprintTypeConversions",
        "BlueprintMapLibrary", "BlueprintSetLibrary", "GameplayTagsLibrary", "BlueprintGameplayTagLibrary"
    };

    private static readonly Regex _pureName = new(@"^(Get|Is|Has|Can|Make|Break|Conv|Equal|NotEqual|Not|Select|Find|Contains|Length|Array_Get|Array_Length|Array_Contains|Array_Find|Map_Find|Map_Contains|Map_Length|Set_Contains|Set_Length)(?=[A-Z_]|$)", RegexOptions.Compiled);

    /// <summary>
    /// For a native function outside the engine headers (game code) purity is unknown: static calls into the
    /// maths and conversion libraries and getter-like names are taken as pure.
    /// </summary>
    private static bool IsPureCall(KismetExpression call)
    {
        if (call is not EX_CallMath math) return false;

        var owner = OwnerOf(math);
        return (owner != null && _pureLibraries.Contains(owner)) || _pureName.IsMatch(math.StackNode.Name);
    }

    /// <summary>
    /// Compiler temporaries, the output pins of nodes rather than variables of the blueprint.
    /// </summary>
    private static bool IsTemporary(string name) =>
        name.StartsWith("CallFunc_", StringComparison.Ordinal) ||
        name.StartsWith("K2Node_", StringComparison.Ordinal) ||
        name.StartsWith("Temp_", StringComparison.Ordinal) || // terms of Select and of the standard macros
        name.EndsWith("_ImplicitCast", StringComparison.Ordinal);

    private static (KismetExpression Variable, KismetExpression Value) SplitAssignment(KismetExpression statement) => statement switch
    {
        EX_Let let => (let.Variable, let.Assignment),
        EX_LetBase let => (let.Variable, let.Assignment),
        EX_LetValueOnPersistentFrame persistent => (new PersistentVariable(persistent.DestinationProperty), persistent.AssignmentExpression),
        _ => (null, null)
    };

    /// <summary>Stand-in so a write into the ubergraph frame reads like any other variable write.</summary>
    private sealed class PersistentVariable(FKismetPropertyPointer property) : KismetExpression
    {
        public override string ToString() => property.ToString();
    }

    /// <summary>The member variable an expression reads or writes, null for locals and temporaries.</summary>
    private static string VariableQuery(KismetExpression expression) => expression switch
    {
        EX_Context { ContextExpression: EX_VariableBase member } => VariableQuery(member),
        EX_LocalVariable or EX_LocalOutVariable => null,
        EX_VariableBase variable when !IsTemporary(variable.Variable.ToString()) => MemberUsageLookup.QueryForProperty(variable.Variable),
        _ => null
    };

    private static string VariableNameOf(KismetExpression variable) => variable switch
    {
        PersistentVariable persistent => persistent.ToString(),
        EX_VariableBase v => v.Variable.ToString(),
        _ => null
    };

    private static KismetExpression UnwrapCall(KismetExpression expression) => UnwrapCallWithTarget(expression).Call;

    /// <summary>
    /// The function call an expression boils down to, and the object it runs on (Target.Function()).
    /// </summary>
    private static (KismetExpression Call, KismetExpression Target) UnwrapCallWithTarget(KismetExpression expression)
    {
        KismetExpression target = null;
        for (var depth = 0; expression != null && depth < 8; depth++)
        {
            switch (expression)
            {
                case EX_Context context:
                    target = context.ObjectExpression;
                    expression = context.ContextExpression;
                    continue;
                case EX_FinalFunction or EX_VirtualFunction or EX_CallMulticastDelegate:
                    return (expression, target);
                default:
                    return (null, null);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Name of the object a member call runs on, null for a static library call.
    /// </summary>
    private static string ContextTarget(KismetExpression expression)
    {
        var target = Clean(SafeLine(expression));
        return target.StartsWith("FindObject<", StringComparison.Ordinal) ? null : target;
    }

    private static KismetExpression[] CallParameters(KismetExpression call) => call switch
    {
        EX_FinalFunction final => final.Parameters,
        EX_VirtualFunction virtualFunction => virtualFunction.Parameters,
        EX_CallMulticastDelegate multicast => multicast.Parameters,
        _ => []
    };

    /// <summary>Class a called function belongs to, as the import names it (KismetMathLibrary, Actor...).</summary>
    private static string OwnerOf(EX_FinalFunction final)
    {
        string owner = null;
        try
        {
            owner = final.StackNode.ResolvedObject?.Outer?.Name.Text;
        }
        catch (Exception)
        {
            // ignored
        }

        return owner != null && owner.StartsWith("Default__", StringComparison.Ordinal) ? owner["Default__".Length..] : owner;
    }

    private static (string Name, string Owner) CallName(KismetExpression call) => call switch
    {
        // K2Node_CallDelegate
        EX_CallMulticastDelegate multicast => ($"Call {BlueprintNodeDatabase.NameToDisplayString(SafeLine(multicast.Delegate), false)}", null),
        EX_FinalFunction final => (final.StackNode.Name, OwnerOf(final)),
        EX_VirtualFunction virtualFunction => (virtualFunction.VirtualFunctionName.Text, null),
        _ => (call.Token.ToString(), null)
    };

    /// <summary>
    /// Offset a latent action resumes at once it completes, read from its FLatentActionInfo.
    /// </summary>
    private static uint? LatentResume(KismetExpression statement)
    {
        var (_, value) = SplitAssignment(statement);
        var call = UnwrapCall(value ?? statement);
        if (call == null) return null;

        foreach (var parameter in CallParameters(call))
        {
            if (parameter is EX_StructConst structConst && structConst.Struct.Name.Contains("LatentActionInfo", StringComparison.Ordinal) &&
                structConst.Properties.FirstOrDefault() is EX_SkipOffsetConst skip)
                return skip.Value;
        }

        return null;
    }

    private static string GenericTitle(KismetExpression statement) => statement switch
    {
        // K2Node_AddDelegate / K2Node_RemoveDelegate / K2Node_ClearDelegate / K2Node_CreateDelegate
        EX_AddMulticastDelegate => "Bind Event",
        EX_RemoveMulticastDelegate => "Unbind Event",
        EX_ClearMulticastDelegate => "Unbind all Events",
        EX_BindDelegate => "Create Event",
        EX_SetArray => "Make Array",
        EX_SetSet => "Make Set",
        EX_SetMap => "Make Map",
        _ => statement.Token.ToString().Replace("EX_", string.Empty)
    };

    /// <summary>A constant as it is typed on a pin.</summary>
    private static string Value(KismetExpression expression)
    {
        var text = expression switch
        {
            null or EX_Nothing => string.Empty,
            EX_True => "true",
            EX_False => "false",
            EX_NoObject or EX_NoInterface => "None",
            EX_ObjectConst constant => ObjectName(constant.Value),
            _ => _whitespace.Replace(Clean(SafeLine(expression)), " ")
        };

        return text.Length <= _MAX_VALUE ? text : text[.._MAX_VALUE] + "...";
    }

    private static string ObjectName(FPackageIndex index)
    {
        var name = index?.Name ?? "None";
        return name.StartsWith("Default__", StringComparison.Ordinal) ? name["Default__".Length..] : name;
    }

    private static string SafeLine(KismetExpression expression)
    {
        if (expression == null) return string.Empty;
        if (expression is PersistentVariable persistent) return persistent.ToString();
        try
        {
            return BlueprintDecompilerUtils.GetLineExpression(expression) ?? string.Empty;
        }
        catch (Exception)
        {
            return expression.Token.ToString();
        }
    }

    private static string Clean(string code)
    {
        if (string.IsNullOrEmpty(code)) return string.Empty;

        code = code.Replace("UberGraphFrame->", string.Empty)
            .Replace("K2Node_DynamicCast_", string.Empty)
            .Replace("CallFunc_", string.Empty)
            .Replace("K2Node_", string.Empty);
        return code.Trim();
    }

    private static IEnumerable<FProperty> Parameters(UFunction function) =>
        (function.ChildProperties ?? []).OfType<FProperty>().Where(p => p.PropertyFlags.HasFlag(EPropertyFlags.Parm));

    private static string FlagsOf(UFunction function) =>
        string.Join(", ", function.FunctionFlags.ToString().Split('|', ',').Select(f => f.Trim().Replace("FUNC_", string.Empty)));

    private static string Signature(UFunction function)
    {
        var parameters = new List<string>();
        var returnType = "void";

        foreach (var property in Parameters(function))
        {
            string type;
            try
            {
                (_, type) = BlueprintDecompilerUtils.GetPropertyType(property);
            }
            catch (Exception)
            {
                type = null;
            }

            type ??= property.GetType().Name;
            if (property.PropertyFlags.HasFlag(EPropertyFlags.ReturnParm))
            {
                returnType = type;
                continue;
            }

            var prefix = property.PropertyFlags.HasFlag(EPropertyFlags.OutParm) && !property.PropertyFlags.HasFlag(EPropertyFlags.ConstParm) ? "out " : string.Empty;
            parameters.Add($"{prefix}{type} {property.Name}");
        }

        return $"{returnType} {function.Name}({string.Join(", ", parameters)})";
    }
}
