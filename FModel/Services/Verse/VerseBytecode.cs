using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;

namespace FModel.Services.Verse;

// Synthetic statements and expressions the cooked Kismet is normalised into before it is structured.
// Each keeps the StatementIndex (code offset) of the statement it came from so jumps still resolve.

/// <summary>a statement that only existed for the runtime and has been folded away</summary>
internal sealed class VxNoise : KismetExpression;

/// <summary><c>Target = Value</c>, whatever the cooked form of the store was</summary>
internal sealed class VxAssign(KismetExpression target, KismetExpression value) : KismetExpression
{
    public readonly KismetExpression Target = target;
    public readonly KismetExpression Value = value;
}

/// <summary><c>set Target op= Value</c></summary>
internal sealed class VxSetOp(KismetExpression target, string op, KismetExpression value) : KismetExpression
{
    public readonly KismetExpression Target = target;
    public readonly string Op = op;
    public readonly KismetExpression Value = value;
}

/// <summary>the value one iteration of a for expression contributes to its result</summary>
internal sealed class VxYield(string forResult, KismetExpression[] values) : KismetExpression
{
    public readonly string ForResult = forResult;
    public readonly KismetExpression[] Values = values;
}

/// <summary>the local that follows is a <c>var</c></summary>
internal sealed class VxVarDecl(string cookedName) : KismetExpression
{
    public readonly string CookedName = cookedName;
}

internal sealed class VxArray(KismetExpression[] elements) : KismetExpression
{
    public readonly KismetExpression[] Elements = elements;
}

internal sealed class VxMap(KismetExpression[] pairs) : KismetExpression
{
    public readonly KismetExpression[] Pairs = pairs;
}

/// <summary>an archetype instantiation, <c>type{Field := Value, ...}</c></summary>
internal sealed class VxObject(FPackageIndex? type, List<(string Field, KismetExpression Value)> fields) : KismetExpression
{
    public readonly FPackageIndex? Type = type;
    public readonly List<(string Field, KismetExpression Value)> Fields = fields;
    public string? TypeName;
}

/// <summary><c>Owner.Name</c> for stores the runtime routes through a helper</summary>
internal sealed class VxMember(KismetExpression owner, string cookedName) : KismetExpression
{
    public readonly KismetExpression Owner = owner;
    public readonly string CookedName = cookedName;
}

/// <summary>a call into a suspends function, awaited in place</summary>
internal sealed class VxAwait(KismetExpression call) : KismetExpression
{
    public readonly KismetExpression Call = call;
}

/// <summary><c>spawn{ Call }</c>, a suspends call started without waiting for it</summary>
internal sealed class VxSpawn(KismetExpression call) : KismetExpression
{
    public readonly KismetExpression Call = call;
}

/// <summary>a string literal the runtime builds in place</summary>
internal sealed class VxString(string value) : KismetExpression
{
    public readonly string Value = value;
}

/// <summary>already rendered source text</summary>
internal sealed class VxText(string text, int precedence) : KismetExpression
{
    public readonly string Text = text;
    public readonly int Precedence = precedence;
}

internal enum StmKind { None, Begin, Commit, Rollback, EnterFrame, LeaveFrame }

/// <summary>
/// The transactional copy of a Verse function, flattened into one list of normalised statements,
/// plus what is needed to structure and print it.
/// </summary>
internal sealed class VerseCode
{
    public readonly List<KismetExpression> S = [];
    private int[] _offsets = [];

    /// <summary>true for the Update function of a lowered suspends task, where locals live on the task object</summary>
    public readonly bool TaskMode;

    public VerseCode(IEnumerable<KismetExpression> statements, bool taskMode)
    {
        TaskMode = taskMode;
        foreach (var statement in statements) Normalise(statement);
        CollapseAwaits();
        CollapseFactories();
        DropPropertyNotifications();
        CollapseCompoundAssignments();
        _offsets = S.Select(s => s.StatementIndex).ToArray();
    }

    /// <summary>index of the first statement at or after a code offset</summary>
    public int IndexOf(uint offset)
    {
        var index = Array.BinarySearch(_offsets, (int) offset);
        if (index < 0) return ~index;
        while (index > 0 && _offsets[index - 1] == (int) offset) index--;
        return index;
    }

    #region naming

    public static string VarName(FKismetPropertyPointer pointer) => pointer.ToString();

    /// <summary>compiler temporaries whose single use is inlined back into the expression it came from</summary>
    public static bool IsTemp(string cooked) =>
        cooked.StartsWith('$') && !cooked.StartsWith("$For", StringComparison.Ordinal) ||
        cooked.StartsWith("__verse_0x00000000_", StringComparison.Ordinal) ||
        cooked.Contains("ResultDummy_", StringComparison.Ordinal) ||
        cooked.Contains("___dupe_fresh", StringComparison.Ordinal);

    public static bool IsDummy(string cooked) => cooked.Contains("ResultDummy_", StringComparison.Ordinal);

    public static string? VariableOf(KismetExpression? expression) => expression switch
    {
        EX_LocalVariable or EX_LocalOutVariable or EX_InstanceVariable => VarName(((EX_VariableBase) expression).Variable),
        _ => null
    };

    /// <summary>the local a statement reads or writes, only for the bare variable forms a function frame uses</summary>
    public string? FrameVariable(KismetExpression? expression) => expression switch
    {
        EX_LocalVariable or EX_LocalOutVariable => VarName(((EX_VariableBase) expression).Variable),
        EX_InstanceVariable iv when TaskMode => VarName(iv.Variable),
        _ => null
    };

    public static bool IsElement(string cooked, out int index)
    {
        index = -1;
        var name = VerseMangling.UnmangleCasedName(cooked);
        return name.StartsWith("Elem", StringComparison.Ordinal) && int.TryParse(name.AsSpan(4), out index);
    }

    #endregion

    #region classification

    public static string CalleeName(EX_FinalFunction call) => call.StackNode.ToString().SubstringAfterLast(':').Trim('\'');

    public static string CalleeLibrary(EX_FinalFunction call)
    {
        var node = call.StackNode.ToString();
        var afterDot = node.Contains('.') ? node.SubstringAfterLast('.') : node;
        return afterDot.SubstringBefore(':');
    }

    public static bool IsCall(KismetExpression? expression, string name) =>
        expression is EX_FinalFunction call && CalleeName(call) == name;

    public static StmKind Stm(KismetExpression expression) => expression is EX_CallMath call
        ? CalleeName(call) switch
        {
            "StmBegin" => StmKind.Begin,
            "StmCommit" => StmKind.Commit,
            "StmRollback" => StmKind.Rollback,
            "StmEnterFrame" => StmKind.EnterFrame,
            "StmLeaveFrame" => StmKind.LeaveFrame,
            _ => StmKind.None
        }
        : StmKind.None;

    private static readonly HashSet<string> NoiseCalls = new(StringComparer.Ordinal)
    {
        "StmEnterFrame", "DestroyClassVarProxy", "CallBlockFunctions", "AddPropertyToSubobjectExclusionList",
        "TaskComplete", "StmEnabled", "DebugReportFunction"
    };

    /// <summary>a statement that carries nothing of the source and is skipped wherever it sits</summary>
    public bool IsNoise(int index)
    {
        var statement = S[index];
        switch (statement)
        {
            case VxNoise or EX_Tracepoint or EX_WireTracepoint or EX_Nothing or EX_EndOfScript:
            case EX_True or EX_False or EX_NoObject or EX_IntConst or EX_Int64Const or EX_ByteConst or EX_IntZero or EX_IntOne:
            case EX_AutoRtfmStopTransact:
                return true;
            case EX_CallMath call when NoiseCalls.Contains(CalleeName(call)):
                return true;
            case EX_JumpIfNot jump when IsNoiseCondition(jump.BooleanExpression):
                return true;
            case EX_ComputedJump:
                return TaskMode;
            case EX_JumpIfNot { BooleanExpression: EX_CallMath { Parameters: [EX_CallMath state, ..] } } when TaskMode && IsCall(state, "TaskGetState"):
                return true;
            default:
                return false;
        }
    }

    /// <summary>checks the runtime makes that the source never spelled and that cannot fail in practice</summary>
    private bool IsNoiseCondition(KismetExpression condition)
    {
        if (condition is not EX_CallMath call) return false;
        switch (CalleeName(call))
        {
            case "ObjectHasNoFlags" or "DidClassVarProxyCallSucceed" or "CheckFunctionReportCVar":
                return true;
            case "IsOptionSet" when call.Parameters is [var checkedValue] && VariableOf(checkedValue) is { } name:
                return _typeChecks.Contains(name);
            default:
                return false;
        }
    }

    /// <summary>temps that hold a CheckConstrainedFloat, the compiler's own type assertion</summary>
    private readonly HashSet<string> _typeChecks = new(StringComparer.Ordinal);

    #endregion

    #region normalisation

    private void Add(KismetExpression expression, int offset)
    {
        expression.StatementIndex = offset;
        S.Add(expression);
    }

    private static readonly Dictionary<string, string> CompoundOperators = new(StringComparer.Ordinal)
    {
        ["AddEquals"] = "+=", ["SubtractEquals"] = "-=", ["MultiplyEquals"] = "*=", ["DivideEquals"] = "/=",
        ["ConcatEquals"] = "+="
    };

    private void Normalise(KismetExpression statement)
    {
        var offset = statement.StatementIndex;
        switch (statement)
        {
            case EX_Tracepoint or EX_WireTracepoint:
                return;
            case EX_Let let:
                NormaliseStore(let.Variable, let.Assignment, offset);
                return;
            case EX_LetBase let:
                NormaliseStore(let.Variable, let.Assignment, offset);
                return;
            case EX_SetArray { AssigningProperty: { } target } array:
                Add(new VxAssign(target, new VxArray(array.Elements)), offset);
                return;
            case EX_SetMap map:
                Add(new VxAssign(map.MapProperty, new VxMap(map.Elements)), offset);
                return;
            case EX_CallMath call:
                NormaliseCall(call, offset, statement);
                return;
            default:
                Add(statement, offset);
                return;
        }
    }

    private void NormaliseCall(EX_CallMath call, int offset, KismetExpression original)
    {
        var name = CalleeName(call);
        var library = CalleeLibrary(call);
        var parameters = call.Parameters;

        switch (name)
        {
            case "InstantiateObject" when parameters.Length >= 3:
                NormaliseInstantiation(call, offset);
                return;
            case "CheckAllocateVar" when parameters is [EX_NameConst variable]:
                Add(new VxVarDecl(variable.Value.Text), offset);
                return;
            case "MakeLiteral" when parameters.Length == 2:
                Add(new VxAssign(parameters[0], parameters[1]), offset);
                return;
            case "Make" when library == "SolarisMathLibrary_String" && parameters is [var text, EX_ByteConst character]:
                Add(new VxAssign(text, new VxString(((char) character.Value).ToString())), offset);
                return;
            case "InitMap" when parameters.Length >= 1:
                // InitMap(Map, Count, Key0, Value0, Key1, Value1, ...)
                Add(new VxAssign(parameters[0], new VxMap(parameters.Length > 2 ? parameters[2..] : [])), offset);
                return;
            case "Move" when parameters.Length == 2:
                Add(new VxAssign(parameters[0], parameters[1]), offset);
                return;
            case "Add" when library is "SolarisArrayLibrary" or "SolarisMapLibrary" &&
                            parameters.Length >= 2 && VariableOf(parameters[0]) is { } forResult && forResult.StartsWith("$ForResult", StringComparison.Ordinal):
                Add(new VxYield(forResult, parameters[1..]), offset);
                return;
            case "WriteClassVar" when parameters is [var owner, EX_NameConst field, _, var value]:
                Add(new VxAssign(new VxMember(owner, field.Value.Text), value), offset);
                return;
            case "CallClassVarProxySetter" when parameters.Length == 3:
                Add(new VxAssign(parameters[0], parameters[2]), offset);
                return;
        }

        if (CompoundOperators.TryGetValue(name, out var op) && parameters.Length == 2)
        {
            Add(new VxSetOp(parameters[0], op, parameters[1]), offset);
            return;
        }

        Add(original, offset);
    }

    private void NormaliseStore(KismetExpression target, KismetExpression value, int offset)
    {
        // the value of a runtime store helper is a status the source never looked at
        if (value is EX_CallMath call)
        {
            var name = CalleeName(call);
            if (name == "CallClassVarProxySetter" && call.Parameters.Length == 3)
            {
                Add(new VxAssign(call.Parameters[0], call.Parameters[2]), offset);
                return;
            }

            if (CompoundOperators.TryGetValue(name, out var op) && call.Parameters.Length == 2 &&
                VariableOf(target) is { } dummy && IsTemp(dummy))
            {
                Add(new VxSetOp(call.Parameters[0], op, call.Parameters[1]), offset);
                return;
            }

            if (name == "CheckConstrainedFloat" && VariableOf(target) is { } check)
                _typeChecks.Add(check);
        }

        if (target is EX_CallMath save && CalleeName(save) == "StmSave" && save.Parameters.Length == 1)
            target = save.Parameters[0];

        Add(new VxAssign(target, value), offset);
    }

    /// <summary>
    /// InstantiateObject(class, graph, Target = GetCurrentlyInstantiatedObject(), field initialisers...)
    /// is how an archetype expression <c>class{Field := Value}</c> is lowered
    /// </summary>
    private void NormaliseInstantiation(EX_CallMath call, int offset)
    {
        KismetExpression? target = null;
        var fields = new List<(string, KismetExpression)>();

        foreach (var inner in call.Parameters.Skip(2))
        {
            switch (inner)
            {
                case EX_Tracepoint or EX_WireTracepoint:
                    continue;
                case EX_LetBase { Assignment: EX_CallMath current } let when CalleeName(current) == "GetCurrentlyInstantiatedObject":
                    target = let.Variable;
                    continue;
                case EX_Let { Assignment: EX_CallMath current } let when CalleeName(current) == "GetCurrentlyInstantiatedObject":
                    target = let.Variable;
                    continue;
                case EX_Let let when IsFieldOf(let.Variable, target, out var field):
                    fields.Add((field, let.Assignment));
                    continue;
                case EX_LetBase let when IsFieldOf(let.Variable, target, out var field):
                    fields.Add((field, let.Assignment));
                    continue;
                case EX_CallMath { Parameters: [var owner, EX_NameConst field, _, var value] } write
                    when CalleeName(write) == "WriteClassVar" && SameVariable(owner, target):
                    fields.Add((field.Value.Text, value));
                    continue;
                default:
                    // field values that need statements of their own keep their own code offsets,
                    // jumps between them land on those
                    Normalise(inner);
                    continue;
            }
        }

        var type = call.Parameters[0] is EX_ObjectConst constant ? constant.Value : null;
        var at = S.Count > 0 ? Math.Max(offset, S[^1].StatementIndex + 1) : offset;
        foreach (var inner in call.Parameters.Skip(2))
            if (inner is EX_Jump jump && jump.CodeOffset > at) at = (int) jump.CodeOffset;
        var instance = new VxObject(type, fields) { StatementIndex = at };
        if (target is null) Add(instance, at);
        else Add(new VxAssign(target, instance), at);
    }

    private static bool SameVariable(KismetExpression? a, KismetExpression? b) =>
        VariableOf(a) is { } left && left == VariableOf(b);

    private static bool IsFieldOf(KismetExpression store, KismetExpression? target, out string field)
    {
        field = string.Empty;
        if (store is not EX_Context { ContextExpression: EX_InstanceVariable member } context || !SameVariable(context.ObjectExpression, target))
            return false;

        field = VarName(member.Variable);
        return true;
    }

    /// <summary>
    /// A call into a suspends function is lowered to
    /// <c>Task = f(CallingTask, ResumeOffset, 0, args); Result = Task.Update(); if (Result != 0) suspend;
    /// [Value = TaskGetReturnProperty(Task)]; TaskComplete(Task); Task = None</c>. Inside a task it is one
    /// awaited call; started with no calling task it is a spawn that is left running.
    /// </summary>
    private void CollapseAwaits()
    {
        for (var i = 0; i < S.Count; i++)
        {
            if (S[i] is not VxAssign { Value: var call } start || VariableOf(start.Target) is not { } task ||
                !task.StartsWith("$AsyncTask", StringComparison.Ordinal))
                continue;

            var j = i + 1;
            if (j >= S.Count || S[j] is not VxAssign { Value: EX_Context { ObjectExpression: var updated, ContextExpression: EX_VirtualFunction update } } ||
                VariableOf(updated) != task || update.VirtualFunctionName.Text != "Update")
                continue;

            j++;
            if (j >= S.Count || S[j] is not EX_JumpIfNot) continue;

            var spawned = AsyncArguments(call) is [EX_NoObject, ..];
            var end = j + 1;
            KismetExpression? valueTarget = null;
            for (; end < S.Count; end++)
            {
                var statement = S[end];
                if (statement is VxAssign { Value: EX_CallMath returned } take && CalleeName(returned) == "TaskGetReturnProperty")
                {
                    valueTarget = take.Target;
                    continue;
                }

                if (statement is EX_CallMath completed && CalleeName(completed) == "TaskComplete") continue;
                if (statement is VxAssign { Value: EX_NoObject } reset && VariableOf(reset.Target) == task)
                {
                    end++;
                    break;
                }

                if (statement is VxNoise) continue;
                break;
            }

            KismetExpression replacement = spawned
                ? new VxSpawn(call)
                : valueTarget is null ? new VxAwait(call) : new VxAssign(valueTarget, new VxAwait(call));
            replacement.StatementIndex = start.StatementIndex;
            S[i] = replacement;
            for (var k = i + 1; k < end; k++)
                S[k] = new VxNoise { StatementIndex = S[k].StatementIndex };

            i = end - 1;
        }
    }

    /// <summary>
    /// a struct or class literal of a native type is lowered to
    /// <c>Keys = map{"Field" => false, ...}; T = type$OverrideFactory(Keys); T.Field = Value; ...</c>
    /// </summary>
    private void CollapseFactories()
    {
        for (var i = 0; i < S.Count; i++)
        {
            if (S[i] is not VxAssign { Value: EX_FinalFunction factory } made || FrameVariable(made.Target) is not { } instance) continue;
            var cooked = factory.StackNode?.ResolvedObject?.Name.Text ?? string.Empty;
            var dollar = cooked.IndexOf("$OverrideFactory", StringComparison.Ordinal);
            if (dollar < 0) continue;

            var fields = new List<(string, KismetExpression)>();
            var j = i + 1;
            for (; j < S.Count; j++)
            {
                if (S[j] is VxNoise or EX_Tracepoint) continue;
                if (S[j] is not VxAssign { Target: EX_StructMemberContext member } field || FrameVariable(member.StructExpression) != instance) break;
                fields.Add((VarName(member.Property), field.Value));
                S[j] = new VxNoise { StatementIndex = S[j].StatementIndex };
            }

            S[i] = new VxAssign(made.Target, new VxObject(null, fields) { TypeName = FactoryType(cooked[..dollar]) })
                { StatementIndex = made.StatementIndex };

            // the field name map handed to the factory, and the literals it was built from
            if (factory.Parameters is not [var keys] || FrameVariable(keys) is not { } keysName) continue;
            for (var k = i - 1; k >= 0 && k >= i - 64; k--)
            {
                if (S[k] is not VxAssign { Value: VxMap map } built || FrameVariable(built.Target) != keysName) continue;
                var literals = map.Pairs.Select(FrameVariable).Where(n => n is not null).ToHashSet();
                for (var l = k - 1; l >= 0 && l >= k - 2 * literals.Count - 8; l--)
                    if (S[l] is VxAssign literal && FrameVariable(literal.Target) is { } name && literals.Contains(name))
                        S[l] = new VxNoise { StatementIndex = S[l].StatementIndex };
                S[k] = new VxNoise { StatementIndex = S[k].StatementIndex };
                break;
            }
        }
    }

    /// <summary>
    /// <c>set X += V</c> on a field or a var is lowered through a fresh copy:
    /// <c>Dup = X; Dup += V; X = Dup</c>
    /// </summary>
    private void CollapseCompoundAssignments()
    {
        for (var i = 0; i < S.Count; i++)
        {
            if (S[i] is not VxAssign { Value: var original } copy || FrameVariable(copy.Target) is not { } dupe ||
                !dupe.Contains("___dupe_fresh", StringComparison.Ordinal) || PlaceKey(original) is not { } place)
                continue;

            var opAt = NextStatement(i);
            if (opAt < 0 || S[opAt] is not VxSetOp op || FrameVariable(op.Target) != dupe) continue;
            var storeAt = NextStatement(opAt);
            if (storeAt < 0 || S[storeAt] is not VxAssign store || FrameVariable(store.Value) != dupe || PlaceKey(store.Target) != place) continue;

            S[storeAt] = new VxSetOp(store.Target, op.Op, op.Value) { StatementIndex = S[storeAt].StatementIndex };
            S[i] = new VxNoise { StatementIndex = S[i].StatementIndex };
            S[opAt] = new VxNoise { StatementIndex = S[opAt].StatementIndex };
        }
    }

    private int NextStatement(int index)
    {
        for (var k = index + 1; k < S.Count; k++)
        {
            if (S[k] is VxNoise or EX_Tracepoint) continue;
            if (S[k] is EX_CallMath call && NoiseCalls.Contains(CalleeName(call))) continue;
            if (S[k] is EX_JumpIfNot jump && IsNoiseCondition(jump.BooleanExpression)) continue;
            return k;
        }

        return -1;
    }

    /// <summary>a key naming the storage location an expression reads or writes, or null when it is not a plain place</summary>
    private string? PlaceKey(KismetExpression? expression) => expression switch
    {
        EX_LocalVariable or EX_LocalOutVariable or EX_InstanceVariable => VarName(((EX_VariableBase) expression).Variable),
        EX_Self => "Self",
        EX_CallMath { Parameters: [_, var owner, EX_NameConst field, ..] } proxy when CalleeName(proxy) == "MakeClassVarProxy" =>
            PlaceKey(owner) is { } key ? $"{key}.{field.Value.Text}" : null,
        EX_CallMath { Parameters: [var inner] } wrap when CalleeName(wrap) is "CallClassVarProxyGetter" or "StmSave" or "Dereference" or "Addressof" =>
            PlaceKey(inner),
        EX_CallMath { Parameters: [_, var inner] } validate when CalleeName(validate) == "Validate" => PlaceKey(inner),
        EX_Context { ContextExpression: EX_InstanceVariable member } context =>
            PlaceKey(context.ObjectExpression) is { } key ? $"{key}.{VarName(member.Variable)}" : null,
        EX_StructMemberContext member => PlaceKey(member.StructExpression) is { } key ? $"{key}.{VarName(member.Property)}" : null,
        VxMember member => PlaceKey(member.Owner) is { } key ? $"{key}.{member.CookedName}" : null,
        _ => null
    };

    /// <summary>SpatialMath_vector3 is vector3, TycoonCode-character is character</summary>
    private static string FactoryType(string cooked)
    {
        if (cooked.Contains('-')) return VerseMangling.UnmangleCasedName(cooked.SubstringAfterLast('-'));
        var parts = cooked.Split('_');
        var first = 0;
        while (first < parts.Length - 1 && parts[first].Length > 0 && char.IsUpper(parts[first][0])) first++;
        return string.Join('_', parts[first..]);
    }

    /// <summary>OnPropertyChangedFromVerse("Field") only tells a native object a property was set</summary>
    private void DropPropertyNotifications()
    {
        var callees = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < S.Count; i++)
        {
            var value = S[i] switch
            {
                VxAssign assign => assign.Value,
                EX_CallMath call => call,
                _ => null
            };

            if (value is EX_CallMath { Parameters: [_, EX_NameConst name] } function && CalleeName(function) == "InstanceFunction" &&
                VerseMangling.FunctionName(name.Value.Text) == "OnPropertyChangedFromVerse" && S[i] is VxAssign { Target: var callee } &&
                FrameVariable(callee) is { } calleeName)
            {
                callees.Add(calleeName);
                S[i] = new VxNoise { StatementIndex = S[i].StatementIndex };
                continue;
            }

            if (value is EX_CallMath { Parameters.Length: >= 1 } invoke && CalleeName(invoke) == "CallFunction" &&
                (FrameVariable(invoke.Parameters[0]) is { } used && callees.Contains(used) ||
                 invoke.Parameters[0] is EX_CallMath { Parameters: [_, EX_NameConst direct] } &&
                 VerseMangling.FunctionName(direct.Value.Text) == "OnPropertyChangedFromVerse"))
                S[i] = new VxNoise { StatementIndex = S[i].StatementIndex };
        }
    }

    /// <summary>the arguments of an async call, starting at the calling task it passes</summary>
    private static KismetExpression[] AsyncArguments(KismetExpression call) => call switch
    {
        EX_CallMath { Parameters.Length: >= 4 } function when CalleeName(function) == "CallFunction" => function.Parameters[1..],
        EX_CallMath { Parameters.Length: >= 5 } final when CalleeName(final) == "CallFinalFunctionWithContext" => final.Parameters[2..],
        _ => []
    };

    #endregion

    #region expression walking

    private static readonly ConcurrentDictionary<Type, FieldInfo[]> ChildFields = new();

    /// <summary>every sub expression of an expression, in evaluation order as far as the cooked form keeps it</summary>
    public static IEnumerable<KismetExpression> Children(KismetExpression expression)
    {
        var fields = ChildFields.GetOrAdd(expression.GetType(), static type => type
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => typeof(KismetExpression).IsAssignableFrom(f.FieldType) || f.FieldType == typeof(KismetExpression[]))
            .ToArray());

        foreach (var field in fields)
        {
            switch (field.GetValue(expression))
            {
                case KismetExpression child:
                    yield return child;
                    break;
                case KismetExpression[] children:
                    foreach (var child in children)
                        if (child is not null) yield return child;
                    break;
            }
        }

        if (expression is VxObject instance)
            foreach (var (_, value) in instance.Fields)
                yield return value;
    }

    #endregion
}

/// <summary>
/// Def/use facts over the normalised statements, which decide what the printer may inline.
/// A temp is inlined when every value stored into it is read exactly once before it is stored
/// again; the IsOptionSet test that guards a failable temp is not counted as a read.
/// </summary>
internal sealed class VerseUsage
{
    private readonly VerseCode _code;
    private readonly Dictionary<string, List<(int Index, char Kind)>> _events = new(StringComparer.Ordinal);
    private readonly Dictionary<string, KismetExpression> _lastValue = new(StringComparer.Ordinal);

    public readonly HashSet<string> Inlinable = new(StringComparer.Ordinal);
    public readonly HashSet<string> TupleTemps = new(StringComparer.Ordinal);
    public readonly HashSet<string> Failable = new(StringComparer.Ordinal);
    public readonly Dictionary<string, int> DefCount = new(StringComparer.Ordinal);
    public readonly Dictionary<string, int> ReadCount = new(StringComparer.Ordinal);
    public readonly Dictionary<string, int> CheckCount = new(StringComparer.Ordinal);

    public VerseUsage(VerseCode code, HashSet<int> skipped)
    {
        _code = code;
        for (var i = 0; i < code.S.Count; i++)
        {
            if (skipped.Contains(i) || code.IsNoise(i)) continue;
            Statement(i, code.S[i]);
        }

        foreach (var (name, events) in _events)
        {
            if (!VerseCode.IsTemp(name)) continue;
            if (TupleTemps.Contains(name)) continue;
            if (IsInlinable(name, events)) Inlinable.Add(name);
        }

        foreach (var tuple in TupleTemps)
        {
            var elements = _events.Where(pair => pair.Key.StartsWith(tuple + "#", StringComparison.Ordinal)).ToList();
            var whole = _events.GetValueOrDefault(tuple) ?? [];
            if (whole.Any(e => e.Kind == 'd')) continue;
            if (elements.All(pair => pair.Value.Count(e => e.Kind == 'd') == 1 && pair.Value.Count(e => e.Kind == 'r') <= 1) &&
                whole.Count(e => e.Kind == 'r') <= 1)
                Inlinable.Add(tuple);
        }
    }

    private static bool IsInlinable(string name, List<(int Index, char Kind)> events)
    {
        if (!events.Any(e => e.Kind == 'd')) return false;
        var totalReads = events.Count(e => e.Kind is 'r' or 'c');
        if (totalReads == 0) return true;

        var reads = 0;
        var checks = 0;
        var open = false;
        foreach (var (_, kind) in events)
        {
            switch (kind)
            {
                case 'd':
                    if (open && !ChainOk(reads, checks)) return false;
                    open = true;
                    reads = checks = 0;
                    break;
                case 'r':
                    if (!open) return false;
                    reads++;
                    break;
                case 'c':
                    if (!open) return false;
                    checks++;
                    break;
            }
        }

        return !open || ChainOk(reads, checks);

        static bool ChainOk(int reads, int checks) => reads == 1 || reads == 0 && checks > 0;
    }

    /// <summary>the next plain read of a temp after an index, or -1</summary>
    public int NextRead(string name, int after)
    {
        if (!_events.TryGetValue(name, out var events)) return -1;
        foreach (var (index, kind) in events)
        {
            if (index <= after) continue;
            if (kind == 'd') return -1;
            if (kind == 'r') return index;
        }

        return -1;
    }

    public KismetExpression? ValueOf(string name) => _lastValue.GetValueOrDefault(name);

    private void Event(string name, int index, char kind)
    {
        if (!_events.TryGetValue(name, out var list)) _events[name] = list = [];
        list.Add((index, kind));
        if (kind == 'd') DefCount[name] = DefCount.GetValueOrDefault(name) + 1;
        else if (kind == 'r') ReadCount[name] = ReadCount.GetValueOrDefault(name) + 1;
        else if (kind == 'c') CheckCount[name] = CheckCount.GetValueOrDefault(name) + 1;
    }

    private void Statement(int index, KismetExpression statement)
    {
        switch (statement)
        {
            case VxAssign assign:
                Store(index, assign.Target, assign.Value);
                Reads(index, assign.Value);
                break;
            case VxSetOp op:
                Reads(index, op.Target);
                Reads(index, op.Value);
                break;
            case EX_JumpIfNot jump:
                if (jump.BooleanExpression is EX_CallMath { Parameters: [var tested] } test &&
                    VerseCode.CalleeName(test) == "IsOptionSet" && _code.FrameVariable(tested) is { } temp)
                {
                    Event(temp, index, 'c');
                    if (_lastValue.TryGetValue(temp, out var value) && IsFailableValue(value))
                        Failable.Add(temp);
                }
                else Reads(index, jump.BooleanExpression);
                break;
            case EX_Jump or VxVarDecl:
                break;
            default:
                Reads(index, statement);
                break;
        }
    }

    private static bool IsFailableValue(KismetExpression value) =>
        value is EX_CallMath call && VerseCode.CalleeName(call) is "CallFunction" or "Call" or "DynamicCastClassOrInterfaceType" ||
        value is VxAwait or EX_FinalFunction;

    private void Store(int index, KismetExpression target, KismetExpression value)
    {
        if (_code.FrameVariable(target) is { } name)
        {
            Event(name, index, 'd');
            _lastValue[name] = value;
            return;
        }

        if (target is EX_StructMemberContext member && _code.FrameVariable(member.StructExpression) is { } owner &&
            VerseCode.IsTemp(owner) && VerseCode.IsElement(VerseCode.VarName(member.Property), out var element))
        {
            TupleTemps.Add(owner);
            Event($"{owner}#{element}", index, 'd');
            return;
        }

        // a store into a member or through a helper reads whatever it stores through
        Reads(index, target);
    }

    private void Reads(int index, KismetExpression expression)
    {
        switch (expression)
        {
            case EX_PropertyConst:
                return;
            case EX_StructMemberContext member when _code.FrameVariable(member.StructExpression) is { } owner &&
                                                    VerseCode.IsTemp(owner) && VerseCode.IsElement(VerseCode.VarName(member.Property), out var element):
                Event($"{owner}#{element}", index, 'r');
                return;
        }

        if (_code.FrameVariable(expression) is { } name)
        {
            if (TupleTemps.Contains(name))
            {
                foreach (var key in _events.Keys.Where(k => k.StartsWith(name + "#", StringComparison.Ordinal)).ToList())
                    Event(key, index, 'r');
            }

            Event(name, index, 'r');
            return;
        }

        foreach (var child in VerseCode.Children(expression))
            Reads(index, child);
    }
}
