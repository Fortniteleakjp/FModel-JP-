using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;

namespace FModel.Services.Verse;

/// <summary>
/// Turns one Kismet expression back into the Verse expression it was lowered from.
///
/// The Verse compiler lowers everything through a handful of runtime libraries - arithmetic and
/// comparison through SolarisMathLibrary, calls through SolarisUtilLibrary, and boxing through
/// VerseDynamicallyTypedValueLibrary - so undoing those is most of the work. Anything this does not
/// recognise is written out in a readable form rather than dropped, so nothing disappears silently.
/// </summary>
public class VerseExpressionWriter
{
    /// <summary>arithmetic and comparison that the compiler lowered to a library call</summary>
    private static readonly Dictionary<string, string> BinaryOperators = new(StringComparer.Ordinal)
    {
        ["Add"] = "+", ["AddEquals"] = "+",
        ["Subtract"] = "-", ["SubtractEquals"] = "-",
        ["Multiply"] = "*", ["MultiplyEquals"] = "*",
        ["MultiplyIntFloat"] = "*", ["MultiplyFloatInt"] = "*",
        ["Divide"] = "/", ["DivideEquals"] = "/",
        ["Concat"] = "+", ["ConcatEquals"] = "+",
        ["Less"] = "<", ["PredicateLess"] = "<",
        ["LessEqual"] = "<=", ["PredicateLessEqual"] = "<=",
        ["Greater"] = ">", ["PredicateGreater"] = ">",
        ["GreaterEqual"] = ">=", ["PredicateGreaterEqual"] = ">=",
        ["Equal"] = "=", ["PredicateEqual"] = "=",
        ["NotEqual"] = "<>", ["PredicateNotEqual"] = "<>",
    };

    /// <summary>calls that only exist to carry a value across a lowering step</summary>
    private static readonly HashSet<string> Transparent = new(StringComparer.Ordinal)
    {
        "ConvertToDynamicallyTypedValue", "ConvertFromDynamicallyTypedValue",
        "UncheckedConvertI32I64", "UncheckedConvertI64I32",
        "IsOptionSet", "GetOptionValue", "Validate", "Query",
        "CallClassVarProxyGetter", "MakeLiteral",
    };

    /// <summary>bookkeeping the runtime needs and the source never had</summary>
    private static readonly HashSet<string> Housekeeping = new(StringComparer.Ordinal)
    {
        "StmEnabled", "StmEnterFrame", "StmLeaveFrame", "StmBegin", "StmCommit", "StmRollback",
        "MakeClassVarProxy", "DestroyClassVarProxy", "AddPropertyToSubobjectExclusionList",
        "ObjectHasNoFlags", "InitMap",
    };

    private readonly VerseTypeResolver _resolver;
    private readonly IReadOnlyDictionary<string, string> _locals;
    private readonly string? _returnSlot;

    public VerseExpressionWriter(VerseTypeResolver resolver, IReadOnlyDictionary<string, string> locals, string? returnSlot = null)
    {
        _resolver = resolver;
        _locals = locals;
        _returnSlot = returnSlot;
    }

    /// <summary>true when a statement is pure runtime bookkeeping and carries no source meaning</summary>
    public static bool IsHousekeeping(KismetExpression expression) => expression switch
    {
        EX_Tracepoint or EX_WireTracepoint or EX_Nothing => true,
        EX_AutoRtfmStopTransact => true,
        EX_FinalFunction call => Housekeeping.Contains(CalleeName(call)),
        _ => false
    };

    public static string CalleeName(EX_FinalFunction call)
    {
        var node = call.StackNode.ToString();
        return node.SubstringAfterLast(':').Trim('\'');
    }

    private static string CalleeLibrary(EX_FinalFunction call)
    {
        var node = call.StackNode.ToString();
        var afterDot = node.Contains('.') ? node.SubstringAfterLast('.') : node;
        return afterDot.SubstringBefore(':');
    }

    public string Write(KismetExpression? expression) => expression switch
    {
        null => "_",
        EX_Self => "Self",
        EX_NoObject or EX_NoInterface => "false",
        EX_True => "true",
        EX_False => "false",
        EX_Nothing or EX_EndOfScript => string.Empty,
        EX_IntConst c => c.Value.ToString(CultureInfo.InvariantCulture),
        EX_Int64Const c => c.Value.ToString(CultureInfo.InvariantCulture),
        EX_IntZero => "0",
        EX_IntOne => "1",
        EX_ByteConst c => c.Value.ToString(CultureInfo.InvariantCulture),
        EX_FloatConst c => Float(c.Value),
        EX_DoubleConst c => Float(c.Value),
        EX_StringConst c => $"\"{c.Value}\"",
        EX_UnicodeStringConst c => $"\"{c.Value}\"",
        EX_NameConst c => Name(c.Value.Text),
        EX_ObjectConst c => ObjectName(c.Value),
        EX_PropertyConst c => Name(c.Property.ToString()),
        EX_VariableBase variable => Local(variable.Variable.ToString()),
        EX_StructMemberContext member => Member(Write(member.StructExpression), member.Property.ToString()),
        EX_ClassContext context => Member(Write(context.ObjectExpression), Write(context.ContextExpression)),
        EX_Context context => Member(Write(context.ObjectExpression), Write(context.ContextExpression)),
        EX_InterfaceContext context => Write(context.InterfaceValue),
        EX_CallMath call => Call(call),
        EX_FinalFunction call => Call(call),
        EX_LocalVirtualFunction call => $"{Name(call.VirtualFunctionName.Text)}({Arguments(call.Parameters)})",
        EX_VirtualFunction call => $"{Name(call.VirtualFunctionName.Text)}({Arguments(call.Parameters)})",
        EX_ArrayGetByRef array => $"{Write(array.ArrayVariable)}[{Write(array.ArrayIndex)}]",
        EX_SetArray array => $"array{{{Arguments(array.Elements)}}}",
        EX_ArrayConst array => $"array{{{Arguments(array.Elements)}}}",
        EX_SetMap map => $"map{{{Arguments(map.Elements)}}}",
        EX_StructConst structure => $"{_resolver.NameOf(structure.Struct.ResolvedObject?.Object?.Value as UStruct)}{{{Arguments(structure.Properties)}}}",
        EX_Cast cast => Write(cast.Target),
        EX_CastBase cast => Write(cast.Target),
        EX_LetBase let => Assignment(let.Variable, let.Assignment),
        EX_Let let => Assignment(let.Variable, let.Assignment),
        EX_Return ret => Write(ret.ReturnExpression),
        _ => $"<{expression.GetType().Name.SubstringAfter("EX_")}>"
    };

    /// <summary>
    /// writing the cooked return slot is how a Verse function yields its value, so it is written
    /// as the bare value rather than as an assignment to a name the source never had
    /// </summary>
    private string Assignment(KismetExpression variable, KismetExpression value)
    {
        var target = Write(variable);
        var assigned = Write(value);
        if (string.IsNullOrEmpty(assigned)) return string.Empty;

        return _returnSlot is not null && target == _returnSlot ? assigned : $"set {target} = {assigned}";
    }

    private static string Float(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.AsSpan().IndexOfAny('.', 'E', 'N') >= 0 ? text : text + ".0";
    }

    /// <summary>a cooked local name, mapped back to the parameter it stands for where we know one</summary>
    private string Local(string cooked)
    {
        var bare = VerseMangling.UnmangleCasedName(cooked);
        return _locals.TryGetValue(bare, out var mapped) ? mapped : bare;
    }

    private static string Name(string cooked) => VerseMangling.UnmangleCasedName(cooked.SubstringAfterLast('.').Trim('\''));

    private string ObjectName(FPackageIndex? index)
    {
        if (index?.ResolvedObject is not { } resolved) return "false";
        if (resolved.Object?.Value is UStruct type && type.GetOrDefault<string>("PackageRelativeVersePath") is { Length: > 0 })
            return _resolver.NameOf(type);

        return VerseMangling.UnmangleCasedName(resolved.Name.Text);
    }

    /// <summary>"Argument.Elem0" and the like collapse to the parameter they stand for</summary>
    private string Member(string owner, string member)
    {
        var name = Name(member);
        if (string.IsNullOrEmpty(owner)) return name;

        var combined = $"{owner}.{name}";
        return _locals.TryGetValue(combined, out var mapped) ? mapped : combined;
    }

    private string Arguments(IEnumerable<KismetExpression> parameters) =>
        string.Join(", ", parameters.Select(Write).Where(static p => !string.IsNullOrEmpty(p)));

    private string Call(EX_FinalFunction call)
    {
        var callee = CalleeName(call);
        var library = CalleeLibrary(call);
        var arguments = call.Parameters;

        // a Verse call the compiler routed through the runtime: CallFunction(InstanceFunction(obj, name), args)
        if (callee == "CallFunction" && arguments.Length >= 1)
            return VerseCall(arguments);

        // boxing, validation and other steps that just carry their value through
        if (Transparent.Contains(callee))
            return arguments.Length == 0 ? string.Empty : Write(arguments[^1]);

        if (Housekeeping.Contains(callee))
            return string.Empty;

        if (callee.StartsWith("MakeRuntimeType", StringComparison.Ordinal))
            return string.Empty;

        if (BinaryOperators.TryGetValue(callee, out var op) && arguments.Length == 2)
            return $"({Write(arguments[0])} {op} {Write(arguments[1])})";

        // a math library call that is a real function, e.g. Floor or Abs
        if (library.StartsWith("SolarisMathLibrary", StringComparison.Ordinal))
            return $"{callee}({Arguments(arguments)})";

        return $"{callee}({Arguments(arguments)})";
    }

    /// <summary>
    /// unwraps CallFunction(InstanceFunction(receiver, "mangled name"), packed arguments)
    /// </summary>
    private string VerseCall(KismetExpression[] arguments)
    {
        var target = arguments[0];
        var rest = arguments.Skip(1).ToArray();

        if (target is EX_FinalFunction { } instance && CalleeName(instance) is "InstanceFunction" or "ClassFunction" && instance.Parameters.Length >= 2)
        {
            var receiver = Write(instance.Parameters[0]);
            var name = instance.Parameters[1] switch
            {
                EX_StringConst text => VerseMangling.FunctionName(text.Value),
                EX_NameConst name2 => VerseMangling.FunctionName(name2.Value.Text),
                var other => Write(other)
            };

            var call = $"{name}({Arguments(rest)})";
            return string.IsNullOrEmpty(receiver) || receiver == "Self" ? call : $"{receiver}.{call}";
        }

        return $"{Write(target)}({Arguments(rest)})";
    }
}
