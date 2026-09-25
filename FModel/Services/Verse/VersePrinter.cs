using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;

namespace FModel.Services.Verse;

/// <summary>one parameter slot of a signature, and where the body finds it in the packed argument</summary>
public sealed class VerseParameter
{
    /// <summary>"" for the argument itself, otherwise the element path, e.g. "Elem1.Elem0"</summary>
    public required string Path { get; init; }
    public required string Type { get; init; }
    /// <summary>a named parameter, ?Name:type = default</summary>
    public bool Named { get; init; }
    public string? Name { get; set; }
    public string? Default { get; set; }
}

/// <summary>
/// Prints structured Verse from the nodes the structurer built. Temps are inlined into the one place
/// they are read, tuples packed for a call become its argument list, and the runtime helpers the
/// compiler lowers to are turned back into the syntax they stand for.
/// </summary>
internal sealed class VersePrinter
{
    private const int PLow = 0, POr = 1, PAnd = 2, PNot = 3, PCmp = 4, PAdd = 5, PMul = 6, PUnary = 7, PPostfix = 8, PPrimary = 9;

    private readonly record struct R(string Text, int Prec);

    private sealed class Pending
    {
        public KismetExpression? Value;
        public SortedDictionary<int, KismetExpression>? Elements;
    }

    private static readonly Dictionary<string, (string Op, int Prec)> Binary = new(StringComparer.Ordinal)
    {
        ["Add"] = ("+", PAdd), ["Subtract"] = ("-", PAdd), ["Concat"] = ("+", PAdd),
        ["Multiply"] = ("*", PMul), ["MultiplyIntFloat"] = ("*", PMul), ["MultiplyFloatInt"] = ("*", PMul),
        ["Divide"] = ("/", PMul),
        ["Less"] = ("<", PCmp), ["PredicateLess"] = ("<", PCmp),
        ["LessEqual"] = ("<=", PCmp), ["PredicateLessEqual"] = ("<=", PCmp),
        ["Greater"] = (">", PCmp), ["PredicateGreater"] = (">", PCmp),
        ["GreaterEqual"] = (">=", PCmp), ["PredicateGreaterEqual"] = (">=", PCmp),
        ["Equal"] = ("=", PCmp), ["PredicateEqual"] = ("=", PCmp),
        ["NotEqual"] = ("<>", PCmp), ["PredicateNotEqual"] = ("<>", PCmp),
    };

    /// <summary>helpers that only carry the last of their arguments through</summary>
    private static readonly HashSet<string> Transparent = new(StringComparer.Ordinal)
    {
        "Validate", "Dereference", "Addressof", "StmSave", "ConvertToDynamicallyTypedValue", "ConvertFromDynamicallyTypedValue",
        "UncheckedConvertI32I64", "UncheckedConvertI64I32", "MakeRationalFromInt", "CheckConstrainedFloat",
        "GetOptionValue", "CallClassVarProxyGetter", "MakeLiteral", "DestroyClassVarProxy"
    };

    private static readonly HashSet<string> Indexers = new(StringComparer.Ordinal)
    {
        "Call", "UncheckedCall", "RefCall", "CompletelyAssignedRefCall", "CompletelyAssignedPersistentVarRefCall", "PersistentVarCall"
    };

    private readonly VerseCode _code;
    private readonly VerseUsage _usage;
    private readonly VerseStructurer _structure;
    private readonly VerseTypeResolver _resolver;
    private readonly string _owner;
    private readonly IReadOnlyList<VerseParameter> _parameters;
    private readonly Func<string, FProperty?> _localProperty;
    private readonly bool _decides;
    private readonly StringBuilder _out = new();

    private readonly Dictionary<string, Pending> _env = new(StringComparer.Ordinal);
    private readonly HashSet<string> _materialised = new(StringComparer.Ordinal);
    private readonly HashSet<string> _vars = new(StringComparer.Ordinal);
    private Dictionary<string, string> _declared = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _display = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parameterByPath;
    private readonly HashSet<int> _skip = [];
    private readonly Dictionary<string, KismetExpression> _aliases = new(StringComparer.Ordinal);
    private readonly SortedDictionary<int, KismetExpression> _returnElements = new();

    /// <summary>when set, stores into fields of Self are collected here instead of printed ($InitCDO)</summary>
    public Dictionary<string, string>? FieldInitialisers;

    public VersePrinter(VerseCode code, VerseUsage usage, VerseStructurer structure, VerseTypeResolver resolver, string owner,
        IReadOnlyList<VerseParameter> parameters, Func<string, FProperty?> localProperty, bool decides)
    {
        _code = code;
        _usage = usage;
        _structure = structure;
        _resolver = resolver;
        _owner = owner;
        _parameters = parameters;
        _localProperty = localProperty;
        _decides = decides;
        _parameterByPath = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public string Print(List<VNode> nodes, string indent)
    {
        NameParameters(nodes);
        foreach (var parameter in _parameters)
        {
            if (parameter.Named && _structure.Defaults.TryGetValue(parameter.Path, out var fallback))
                parameter.Default = Expr(fallback);
        }

        Block(nodes, indent);
        if (_returnElements.Count > 0) Line(indent, ReturnTuple());
        return _out.ToString();
    }

    #region parameters

    /// <summary>
    /// parameter names are not cooked; a parameter the body copies straight into a local at its very
    /// start gets that local's name, the rest read Arg0..ArgN
    /// </summary>
    private void NameParameters(List<VNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node is not VStatement statement || _code.S[statement.Index] is not VxAssign assign) break;
            if (_structure.ArgumentPath(assign.Value) is not { } path || _code.FrameVariable(assign.Target) is not { } local ||
                VerseCode.IsTemp(local) || _usage.DefCount.GetValueOrDefault(local) != 1)
                break;

            var parameter = _parameters.FirstOrDefault(p => p.Path == path && !p.Named && p.Name is null);
            if (parameter is null) break;

            parameter.Name = LocalName(local);
            _skip.Add(statement.Index);
        }

        for (var i = 0; i < _parameters.Count; i++)
        {
            var parameter = _parameters[i];
            parameter.Name ??= $"Arg{i}";
            _parameterByPath[parameter.Path] = parameter.Name;
            _declared[$"<parameter {i}>"] = parameter.Name;
        }
    }

    #endregion

    #region blocks

    private void Line(string indent, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (var line in text.Split('\n'))
            _out.Append(indent).Append(line).Append('\n');
    }

    private void Block(List<VNode> nodes, string indent)
    {
        var outer = _declared;
        _declared = new Dictionary<string, string>(outer, StringComparer.Ordinal);
        var definedHere = new List<string>();

        for (var n = 0; n < nodes.Count; n++)
        {
            var node = nodes[n];
            switch (node)
            {
                case VStatement subject when n + 1 < nodes.Count && nodes[n + 1] is VIf chain && TryCase(subject, chain, indent):
                    n++;
                    break;
                case VStatement statement:
                {
                    if (_skip.Contains(statement.Index)) break;
                    var s = _code.S[statement.Index];
                    if (s is VxAssign { Target: EX_LocalOutVariable ret } assign && VerseCode.VarName(ret.Variable) == "RetVal")
                    {
                        // "set X = Value" as the last expression: the result is stored and then read back
                        if (n + 1 < nodes.Count && nodes[n + 1] is VStatement { Index: var nextIndex } &&
                            _code.S[nextIndex] is VxAssign { Value: EX_LocalOutVariable back } store && VerseCode.VarName(back.Variable) == "RetVal")
                        {
                            Line(indent, Assign(store.Target, assign.Value, definedHere, false));
                            n++;
                            break;
                        }

                        var returns = n + 1 < nodes.Count && nodes[n + 1] is VReturn;
                        var value = ReturnValue(assign.Value);
                        if (returns) n++;
                        if (value is null) Line(indent, returns ? "return" : string.Empty);
                        else Line(indent, returns ? $"return {value}" : value);
                        break;
                    }

                    Line(indent, Statement(statement.Index, definedHere));
                    break;
                }
                case VCheck check:
                    Line(indent, Check(check, int.MaxValue));
                    break;
                case VNot not:
                    Line(indent, Not(not));
                    break;
                case VIf conditional:
                    If(conditional, indent);
                    break;
                case VLoop loop:
                    Line(indent, "loop:");
                    Body(loop.Body, indent + "    ");
                    break;
                case VFor loop:
                    if (For(loop, indent, n + 1 < nodes.Count ? nodes[n + 1] : null)) n++;
                    break;
                case VConcurrent concurrent:
                    Line(indent, $"{concurrent.Kind}:");
                    foreach (var branch in concurrent.Branches)
                    {
                        // a branch that is one expression is written as it is, anything longer as a block
                        var start = _out.Length;
                        Body(branch, indent + "        ");
                        var text = _out.ToString(start, _out.Length - start);
                        _out.Length = start;
                        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        if (lines.Count(l => !l.StartsWith(indent + "         ", StringComparison.Ordinal)) == 1)
                            foreach (var line in lines) _out.Append(line[4..]).Append('\n');
                        else
                        {
                            Line(indent + "    ", "block:");
                            _out.Append(text);
                        }
                    }
                    break;
                case VBreak:
                    Line(indent, "break");
                    break;
                case VReturn:
                    Line(indent, _returnElements.Count > 0 ? $"return {ReturnTuple()}" : "return");
                    break;
                case VFail:
                    Line(indent, "false?");
                    break;
            }
        }

        // a temp whose read ended up in another block is bound to its own name where it was made
        foreach (var name in definedHere)
        {
            if (!_env.Remove(name, out var pending) || pending.Value is null) continue;
            _materialised.Add(name);
            Line(indent, $"{TempName(name)} := {Expr(pending.Value)}");
        }

        _declared = outer;
    }

    private string ReturnTuple()
    {
        var text = $"({string.Join(", ", _returnElements.Values.Select(Expr))})";
        _returnElements.Clear();
        return text;
    }

    private void Body(List<VNode> nodes, string indent)
    {
        var length = _out.Length;
        Block(nodes, indent);
        if (_out.Length == length) Line(indent, "block {}");
    }

    private string? ReturnValue(KismetExpression value)
    {
        if (_localProperty("RetVal") is { } returned && EnumOf(returned) is { } enumeration && EnumLiteral(enumeration, value) is { } literal)
            return literal;

        if (_decides && value is EX_CallMath made && VerseCode.CalleeName(made) is "MakeOptionFromValue" or "MakeUnsetOption")
        {
            if (VerseCode.CalleeName(made) == "MakeUnsetOption") return "false?";
            var payload = made.Parameters[^1];
            return IsFalse(payload) ? null : Expr(payload);
        }

        return Expr(value);
    }

    private bool IsFalse(KismetExpression value) => value switch
    {
        EX_False => true,
        _ when _code.FrameVariable(value) is { } temp && _env.TryGetValue(temp, out var pending) && pending.Value is EX_False =>
            _env.Remove(temp),
        _ => false
    };

    #endregion

    #region statements

    private string Statement(int index, List<string> definedHere)
    {
        var statement = _code.S[index];
        switch (statement)
        {
            case VxVarDecl declaration:
                _vars.Add(declaration.CookedName);
                return string.Empty;
            case VxAssign assign:
                return Assign(assign.Target, assign.Value, definedHere, false);
            case VxSetOp op:
                return $"set {Expr(op.Target)} {op.Op} {Expr(op.Value)}";
            case VxYield yield:
                return yield.Values.Length == 2 ? $"{Expr(yield.Values[0])} => {Expr(yield.Values[1])}" : Expr(yield.Values[0]);
            default:
                return Effect(statement);
        }
    }

    /// <summary>an expression evaluated for its effect; bare reads and constants carry none</summary>
    private string Effect(KismetExpression expression)
    {
        if (_code.FrameVariable(expression) is { } name && !_env.ContainsKey(name)) return string.Empty;
        if (expression is EX_True or EX_False or EX_IntConst or EX_Int64Const or EX_DoubleConst or EX_FloatConst or EX_NoObject or
            EX_ByteConst or EX_StringConst or EX_UnicodeStringConst or EX_NameConst or VxString or EX_Self)
            return string.Empty;

        var text = Expr(expression);
        // an inlined temp that only held a literal or a name
        return IsInert(text) ? string.Empty : text;
    }

    private static bool IsInert(string text) =>
        text.Length == 0 || text.StartsWith('"') && text.EndsWith('"') && text.IndexOf('"', 1) == text.Length - 1 ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _) || text is "true" or "false";

    private string Assign(KismetExpression target, KismetExpression value, List<string>? definedHere, bool inCondition)
    {
        if (_code.FrameVariable(target) is { } name)
        {
            if (VerseCode.IsTemp(name))
            {
                if (_usage.Inlinable.Contains(name))
                {
                    var reads = _usage.ReadCount.GetValueOrDefault(name);
                    if (reads == 0 && _usage.CheckCount.GetValueOrDefault(name) == 0)
                        return Effect(value);

                    _env[name] = new Pending { Value = value };
                    definedHere?.Add(name);
                    return string.Empty;
                }

                // a temp that only names a variable or the packed argument is read through wherever it is read
                if (_usage.DefCount.GetValueOrDefault(name) == 1 && IsPlace(value))
                {
                    _aliases[name] = value;
                    return string.Empty;
                }

                var temp = TempName(name);
                var bracket = _usage.Failable.Contains(name);
                var rendered = bracket ? ExprFailable(value) : Expr(value);
                if (_materialised.Add(name) || inCondition) return $"{temp} := {rendered}";
                return $"set {temp} = {rendered}";
            }

            if (name == "CurrentlyInstantiatedObject") return string.Empty;

            var text = _localProperty(name) is { } localType && EnumOf(localType) is { } localEnum &&
                       EnumLiteral(localEnum, value) is { } enumText
                ? enumText
                : Expr(value);
            if (_structure.ArgumentPath(target) is null && Declare(name, out var local))
            {
                if (_vars.Contains(name))
                {
                    var type = _localProperty(name) is { } property ? _resolver.Resolve(property) : "any";
                    return $"var {local}:{type} = {text}";
                }

                return $"{local} := {text}";
            }

            return $"set {Display(name)} = {text}";
        }

        // element stores into a packed tuple become its elements
        if (target is EX_StructMemberContext member && _code.FrameVariable(member.StructExpression) is { } tuple &&
            VerseCode.IsTemp(tuple) && _usage.Inlinable.Contains(tuple) &&
            VerseCode.IsElement(VerseCode.VarName(member.Property), out var element))
        {
            if (!_env.TryGetValue(tuple, out var pending)) _env[tuple] = pending = new Pending();
            (pending.Elements ??= new SortedDictionary<int, KismetExpression>())[element] = value;
            return string.Empty;
        }

        // the class default object sets up its fields
        if (FieldInitialisers is not null && target is EX_InstanceVariable initialised)
        {
            var text = Expr(value);
            if (!text.Contains('\n')) FieldInitialisers[VerseCode.VarName(initialised.Variable)] = text;
            return string.Empty;
        }

        // a function returning a tuple fills in its elements
        if (target is EX_StructMemberContext { StructExpression: EX_LocalOutVariable ret } returnElement &&
            VerseCode.VarName(ret.Variable) == "RetVal" && VerseCode.IsElement(VerseCode.VarName(returnElement.Property), out var slot))
        {
            _returnElements[slot] = value;
            return string.Empty;
        }

        // a constructor fills in the object it is instantiating
        if (target is EX_Context { ObjectExpression: var instance, ContextExpression: EX_InstanceVariable field } &&
            _code.FrameVariable(instance) == "CurrentlyInstantiatedObject")
            return $"{FieldName(VerseCode.VarName(field.Variable))} := {Expr(value)}";
        if (target is VxMember { Owner: var owner } written && _code.FrameVariable(owner) == "CurrentlyInstantiatedObject")
            return $"{FieldName(written.CookedName)} := {Expr(value)}";

        return $"set {Expr(target)} = {Expr(value)}";
    }

    #endregion

    #region locals

    /// <summary>
    /// brings a local into scope; the compiler's _N suffix is dropped unless another local of the
    /// same name is already visible, which Verse does not allow and so means two different ones
    /// </summary>
    private bool Declare(string cooked, out string display)
    {
        if (_declared.TryGetValue(cooked, out display!)) return false;

        display = LocalName(cooked);
        var candidate = display;
        if (_declared.Any(pair => pair.Value == candidate && pair.Key != cooked))
            display = VerseMangling.UnmangleCasedName(cooked);

        _declared[cooked] = display;
        _display[cooked] = display;
        return true;
    }

    private string DeclareName(string cooked)
    {
        Declare(cooked, out var display);
        return display;
    }

    private string Display(string cooked) => _display.TryGetValue(cooked, out var display) ? display : LocalName(cooked);

    #endregion

    #region control flow

    private string Check(VCheck check, int scopeEnd)
    {
        if (check.Condition is EX_CallMath { Parameters: [var tested] } test && VerseCode.CalleeName(test) == "IsOptionSet")
        {
            if (_code.FrameVariable(tested) is { } temp && VerseCode.IsTemp(temp))
            {
                if (_env.TryGetValue(temp, out var pending) && pending.Value is { } value)
                {
                    var next = _usage.NextRead(temp, check.Index);
                    if (next >= 0 && next < scopeEnd) return string.Empty;

                    _env.Remove(temp);
                    var failable = ExprFailable(value);
                    if (next < 0) return failable;

                    _materialised.Add(temp);
                    return $"{TempName(temp)} := {failable}";
                }

                // the failable value was already bound where it was made
                if (_materialised.Contains(temp) && _usage.Failable.Contains(temp)) return string.Empty;
            }

            return $"{Postfix(tested)}?";
        }

        return Expr(check.Condition);
    }

    private string Not(VNot not)
    {
        var parts = Condition(not.Items, int.MaxValue);
        return parts.Count switch
        {
            0 => string.Empty,
            1 => $"not {Paren(parts[0])}",
            _ => $"not ({string.Join(" and ", parts)})"
        };
    }

    private static string Paren(string text) =>
        text.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '[' or ']' or '(' or ')' or '?') ? text : $"({text})";

    /// <summary>the items of a failure context as the comma separated parts of an if or for header</summary>
    private List<string> Condition(List<VNode> items, int scopeEnd)
    {
        var parts = new List<string>();
        foreach (var item in items)
        {
            switch (item)
            {
                case VStatement statement:
                {
                    if (_skip.Contains(statement.Index)) break;
                    var s = _code.S[statement.Index];
                    var text = s switch
                    {
                        VxAssign assign => ConditionBinding(assign),
                        VxVarDecl declaration => Declare(declaration),
                        VxSetOp op => $"set {Expr(op.Target)} {op.Op} {Expr(op.Value)}",
                        _ => Effect(s)
                    };
                    if (!string.IsNullOrEmpty(text)) parts.Add(text);
                    break;
                }
                case VCheck check:
                {
                    var text = Check(check, scopeEnd);
                    if (!string.IsNullOrEmpty(text)) parts.Add(text);
                    break;
                }
                case VNot not:
                {
                    var text = Not(not);
                    if (!string.IsNullOrEmpty(text)) parts.Add(text);
                    break;
                }
                case VOr or:
                {
                    var left = Condition(or.Left, scopeEnd);
                    var right = Condition(or.Right, scopeEnd);
                    parts.Add($"{Conjunction(left)} or {Conjunction(right)}");
                    break;
                }
                default:
                {
                    // a nested construct inside a condition, rendered on its own lines
                    var saved = _out.Length;
                    Block([item], string.Empty);
                    var text = _out.ToString(saved, _out.Length - saved).TrimEnd('\n').Replace("\n", "; ");
                    _out.Length = saved;
                    if (!string.IsNullOrEmpty(text)) parts.Add(text);
                    break;
                }
            }
        }

        return parts;
    }

    private static string Conjunction(List<string> parts) => parts.Count switch
    {
        0 => "true",
        1 => parts[0].Contains(" or ", StringComparison.Ordinal) ? $"({parts[0]})" : parts[0],
        _ => $"({string.Join(" and ", parts)})"
    };

    private bool IsPlace(KismetExpression value) => value switch
    {
        EX_LocalVariable or EX_LocalOutVariable => _code.FrameVariable(value) is { } name && !VerseCode.IsTemp(name) ||
                                                   _structure.ArgumentPath(value) is not null,
        EX_InstanceVariable => !_code.TaskMode || _structure.ArgumentPath(value) is not null,
        EX_StructMemberContext => _structure.ArgumentPath(value) is not null,
        EX_Self => true,
        _ => false
    };

    private string Declare(VxVarDecl declaration)
    {
        _vars.Add(declaration.CookedName);
        return string.Empty;
    }

    private string ConditionBinding(VxAssign assign)
    {
        if (_code.FrameVariable(assign.Target) is { } name && !VerseCode.IsTemp(name) && name != "CurrentlyInstantiatedObject")
        {
            var text = Expr(assign.Value);
            Declare(name, out var local);
            return $"{local} := {text}";
        }

        return Assign(assign.Target, assign.Value, null, true);
    }

    private void If(VIf node, string indent)
    {
        if (TryIfExpression(node)) return;
        if (TryLocalIfExpression(node, indent)) return;

        // converting the literal empty option to another option type: only the else path can run
        if (AlwaysFails(node.Condition))
        {
            if (node.Else is { } otherwise) Block(otherwise, indent);
            return;
        }

        var outer = _declared;
        _declared = new Dictionary<string, string>(outer, StringComparer.Ordinal);
        var parts = Condition(node.Condition, node.ConditionEnd);

        var then = node.Then;
        var @else = node.Else;

        if (parts.Count == 0)
        {
            // the context cannot fail once its checks were folded away, it is just its body
            Block(then, indent);
            _declared = outer;
            return;
        }

        var condition = string.Join(", ", parts);
        if (!HasContent(then) && @else is not null && HasContent(@else))
        {
            Line(indent, $"if (not ({string.Join(" and ", parts)})):");
            Body(@else, indent + "    ");
            _declared = outer;
            return;
        }

        Line(indent, $"if ({condition}):");
        Body(then, indent + "    ");
        _declared = outer;

        while (@else is not null && HasContent(@else))
        {
            if (@else is [VIf chained] && !IsIfExpression(chained))
            {
                _declared = new Dictionary<string, string>(outer, StringComparer.Ordinal);
                var chainedParts = Condition(chained.Condition, chained.ConditionEnd);
                if (chainedParts.Count == 0)
                {
                    Line(indent, "else:");
                    Body(chained.Then, indent + "    ");
                    _declared = outer;
                    return;
                }

                Line(indent, $"else if ({string.Join(", ", chainedParts)}):");
                Body(chained.Then, indent + "    ");
                _declared = outer;
                @else = chained.Else;
                continue;
            }

            Line(indent, "else:");
            Body(@else, indent + "    ");
            break;
        }
    }

    /// <summary>
    /// <c>case (X):</c> is lowered to storing X in an anonymous local and comparing it against each
    /// arm in turn: if (S = A) {..} else if (S = B) {..} else {..}
    /// </summary>
    private bool TryCase(VStatement subject, VIf chain, string indent)
    {
        if (_code.S[subject.Index] is not VxAssign { Value: var value } store || _code.FrameVariable(store.Target) is not { } temp ||
            !temp.StartsWith("__verse_0x00000000_", StringComparison.Ordinal))
            return false;

        var arms = new List<(KismetExpression? Key, List<VNode> Body)>();
        var current = chain;
        while (true)
        {
            if (CaseKey(current.Condition, temp, "Equal") is not { } key) return arms.Count > 0 && false;
            arms.Add((key, current.Then));
            if (current.Else is [VIf next] && CaseKey(next.Condition, temp, "Equal") is not null)
            {
                current = next;
                continue;
            }

            if (current.Else is { Count: > 0 } rest)
            {
                // with no default arm the last one checks its value and fails otherwise
                if (rest[0] is VIf { Else: null, Then: [VFail] } guard && CaseKey(guard.Condition, temp, "NotEqual") is { } last)
                    arms.Add((last, rest.Skip(1).ToList()));
                else
                    arms.Add((null, rest));
            }

            break;
        }

        if (arms.Count < 2) return false;

        // when every arm ends by storing the same temp, the case is the value of that temp
        string? result = null;
        if (arms.All(arm => arm.Body is [.., VStatement { Index: var last }] && _code.S[last] is VxAssign tail &&
                            _code.FrameVariable(tail.Target) is { } name && VerseCode.IsTemp(name) &&
                            name == ResultOf(arms[0].Body)))
            result = ResultOf(arms[0].Body);

        var header = $"case ({Expr(value)}):";
        if (result is not null)
        {
            _materialised.Add(result);
            header = $"{TempName(result)} := {header}";
        }

        Line(indent, header);
        foreach (var (key, body) in arms)
        {
            var label = key is null ? "_" : Expr(key);
            var nodes = body;
            string? yielded = null;
            if (result is not null && body is [.., VStatement { Index: var last }] && _code.S[last] is VxAssign tail)
            {
                nodes = body.Take(body.Count - 1).ToList();
                yielded = tail.Value is { } v ? ReturnLike(v) : null;
            }

            var start = _out.Length;
            Block(nodes, indent + "        ");
            if (yielded is not null) Line(indent + "        ", yielded);
            var text = _out.ToString(start, _out.Length - start);
            _out.Length = start;

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 1) Line(indent + "    ", $"{label} => {lines[0].Trim()}");
            else if (lines.Length == 0) Line(indent + "    ", $"{label} => {{}}");
            else
            {
                Line(indent + "    ", $"{label} =>");
                _out.Append(text);
            }
        }

        return true;

        string? ResultOf(List<VNode> body) =>
            body is [.., VStatement { Index: var last }] && _code.S[last] is VxAssign tail ? _code.FrameVariable(tail.Target) : null;
    }

    /// <summary>a value flowing into the function result, spelled as the result type spells it</summary>
    private string ReturnLike(KismetExpression value) =>
        _localProperty("RetVal") is { } returned && EnumOf(returned) is { } enumeration && EnumLiteral(enumeration, value) is { } literal
            ? literal
            : Expr(value);

    /// <summary>the value a case arm's condition compares the subject against</summary>
    private KismetExpression? CaseKey(List<VNode> condition, string subject, string op)
    {
        var values = new Dictionary<string, KismetExpression>(StringComparer.Ordinal);
        KismetExpression? test = null;
        foreach (var item in condition)
        {
            switch (item)
            {
                case VStatement { Index: var index } when _code.S[index] is VxAssign assign && _code.FrameVariable(assign.Target) is { } name:
                    if (VerseCode.IsDummy(name)) continue;
                    values[name] = assign.Value;
                    if (assign.Value is EX_CallMath compared && VerseCode.CalleeName(compared) is var c && (c == op || c == "Predicate" + op))
                        test = assign.Value;
                    continue;
                case VCheck { Condition: EX_CallMath check } when VerseCode.CalleeName(check) == "IsOptionSet":
                    continue;
                case VCheck { Condition: var direct }:
                    if (test is not null) return null;
                    test = direct;
                    continue;
                default:
                    return null;
            }
        }

        if (test is not EX_CallMath { Parameters: [var left, var right] } compare) return null;
        var callee = VerseCode.CalleeName(compare);
        if (callee != op && callee != "Predicate" + op) return null;
        left = Resolve(left);
        right = Resolve(right);
        if (Unwrapped(left) == subject) return right;
        if (Unwrapped(right) == subject) return left;
        return null;

        KismetExpression Resolve(KismetExpression side) =>
            _code.FrameVariable(side) is { } name && values.TryGetValue(name, out var value) ? value : side;

        string? Unwrapped(KismetExpression side) => side is EX_CallMath { Parameters: [_, var inner] } convert &&
                                                   VerseCode.CalleeName(convert) == "ConvertToDynamicallyTypedValue"
            ? _code.FrameVariable(inner)
            : _code.FrameVariable(side);
    }

    private bool HasContent(List<VNode> nodes) => nodes.Any(node => node is not VStatement statement || !_skip.Contains(statement.Index) &&
        _code.S[statement.Index] is not VxVarDecl);

    /// <summary>the temp both branches of an if store into, when that is all they do</summary>
    private string? IfExpressionTemp(VIf node, out KismetExpression? thenValue, out KismetExpression? elseValue, out bool thenInCondition)
    {
        thenValue = elseValue = null;
        thenInCondition = false;
        if (node.Else is not [.., VStatement elseLast] || !Feeds(node.Else)) return null;
        if (_code.S[elseLast.Index] is not VxAssign elseStore || _code.FrameVariable(elseStore.Target) is not { } temp || !VerseCode.IsTemp(temp))
            return null;

        VStatement? thenLast = null;
        if (node.Then is [.., VStatement last] && Feeds(node.Then)) thenLast = last;
        else if (node.Then.Count == 0 && node.Condition is [.., VStatement conditionLast])
        {
            thenLast = conditionLast;
            thenInCondition = true;
        }

        if (thenLast is null || _code.S[thenLast.Index] is not VxAssign thenStore || _code.FrameVariable(thenStore.Target) != temp) return null;
        if (_usage.Inlinable.Contains(temp) || _usage.DefCount.GetValueOrDefault(temp) != 2 || _usage.ReadCount.GetValueOrDefault(temp) != 1)
            return null;

        thenValue = thenStore.Value;
        elseValue = elseStore.Value;
        return temp;
    }

    private bool IsIfExpression(VIf node) => IfExpressionTemp(node, out _, out _, out _) is not null;

    /// <summary>if (c) { L := a } else { L := b }  reads as  L := if (c) then a else b</summary>
    private bool TryLocalIfExpression(VIf node, string indent)
    {
        if (node.Then is not [.., VStatement thenLast] || node.Else is not [.., VStatement elseLast] || !Feeds(node.Then) || !Feeds(node.Else))
            return false;
        if (_code.S[thenLast.Index] is not VxAssign thenStore || _code.S[elseLast.Index] is not VxAssign elseStore) return false;
        if (_code.FrameVariable(thenStore.Target) is not { } name || VerseCode.IsTemp(name) || _code.FrameVariable(elseStore.Target) != name ||
            _declared.ContainsKey(name) || _vars.Contains(name) || _structure.ArgumentPath(thenStore.Target) is not null)
            return false;
        if (AlwaysFails(node.Condition)) return false;

        var outer = _declared;
        _declared = new Dictionary<string, string>(outer, StringComparer.Ordinal);
        var parts = Condition(node.Condition, node.ConditionEnd);
        var a = FeedAndRender(node.Then, thenStore.Value);
        _declared = outer;
        var b = FeedAndRender(node.Else, elseStore.Value);

        Declare(name, out var local);
        Line(indent, parts.Count == 0 ? $"{local} := {a}" : $"{local} := if ({string.Join(", ", parts)}) then {a} else {b}");
        return true;
    }

    private bool AlwaysFails(List<VNode> condition) => condition.Any(item =>
        item is VCheck { Condition: EX_CallMath { Parameters: [var tested] } test } && VerseCode.CalleeName(test) == "IsOptionSet" &&
        _code.FrameVariable(tested) is { } queried && _usage.ValueOf(queried) is EX_CallMath { Parameters: [var option] } query &&
        VerseCode.CalleeName(query) == "Query" && IsUnset(option));

    private bool IsUnset(KismetExpression option) => option switch
    {
        EX_CallMath unset when VerseCode.CalleeName(unset) == "MakeUnsetOption" => true,
        _ => _code.FrameVariable(option) is { } name && _usage.DefCount.GetValueOrDefault(name) == 1 &&
             _usage.ValueOf(name) is EX_CallMath made && VerseCode.CalleeName(made) == "MakeUnsetOption"
    };

    /// <summary>everything but the last node only computes temps the last one reads</summary>
    private bool Feeds(List<VNode> nodes)
    {
        for (var i = 0; i < nodes.Count - 1; i++)
        {
            switch (nodes[i])
            {
                case VCheck:
                    continue;
                case VStatement { Index: var index } when _code.S[index] is VxAssign assign && _code.FrameVariable(assign.Target) is { } name &&
                                                           VerseCode.IsTemp(name) && _usage.Inlinable.Contains(name):
                    continue;
                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>runs the feeding nodes of a branch so the temps they compute are in scope for its value</summary>
    private string FeedAndRender(List<VNode> nodes, KismetExpression value)
    {
        for (var i = 0; i < nodes.Count - 1; i++)
        {
            if (nodes[i] is VStatement { Index: var index }) Statement(index, []);
            else if (nodes[i] is VCheck check) Check(check, int.MaxValue);
        }

        return Expr(value);
    }

    /// <summary>if (c) { T = a } else { T = b }; use(T)  reads as  use(if (c) then a else b), or a or b</summary>
    private bool TryIfExpression(VIf node)
    {
        if (IfExpressionTemp(node, out var thenValue, out var elseValue, out var thenInCondition) is not { } temp) return false;

        var items = thenInCondition ? node.Condition.Take(node.Condition.Count - 1).ToList() : node.Condition;
        var parts = Condition(items, node.ConditionEnd);
        var a = thenInCondition ? Expr(thenValue!) : FeedAndRender(node.Then, thenValue!);
        var b = FeedAndRender(node.Else!, elseValue!);

        // converting the empty option to another option type tests it and rebuilds it
        if (parts is ["false?"])
        {
            _env[temp] = new Pending { Value = new VxText(b, PPrimary) };
            return true;
        }

        var text = parts.Count == 0 ? $"{a} or {b}" : $"if ({string.Join(", ", parts)}) then {a} else {b}";
        _env[temp] = new Pending { Value = new VxText(text, parts.Count == 0 ? POr : PLow) };
        return true;
    }

    private bool For(VFor node, string indent, VNode? next)
    {
        var outer = _declared;
        _declared = new Dictionary<string, string>(outer, StringComparer.Ordinal);

        var source = node.Kind == ForKind.Range ? $"{Expr(node.RangeFrom!)}..{Expr(node.RangeTo!)}" : Expr(node.Source!);
        var key = node.Key is null ? "_" : DeclareName(node.Key);
        var item = node.Item is null ? "_" : DeclareName(node.Item);
        var header = node.Kind switch
        {
            ForKind.Range => $"{item} := {source}",
            ForKind.Map => $"{key} -> {item} : {source}",
            _ => node.Key is null ? $"{item} : {source}" : $"{key} -> {item} : {source}"
        };

        var parts = Condition(node.Filters, node.FiltersEnd);
        parts.Insert(0, header);
        var head = $"for ({string.Join(", ", parts)})";

        // a for whose result is kept is an expression, printed inline when its body is one value
        var start = _out.Length;
        Block(node.Body, indent + "    ");
        var body = _out.ToString(start, _out.Length - start);
        _out.Length = start;
        _declared = outer;

        string? target = null;
        if (node.ResultTarget is { } resultTarget && !(resultTarget is EX_LocalOutVariable ret && VerseCode.VarName(ret.Variable) == "RetVal"))
        {
            var name = _code.FrameVariable(resultTarget);
            var bodyLines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var consumed = false;
            if (name is not null && VerseCode.IsTemp(name) && _usage.Inlinable.Contains(name))
            {
                if (bodyLines.Length == 1)
                {
                    _env[name] = new Pending { Value = new VxText($"{head} {{ {bodyLines[0].Trim()} }}", PLow) };
                    return false;
                }

                // a multi line for is not inlined into an expression; it is stored where its one read stores it,
                // or bound to its own name
                if (next is VStatement { Index: var nextIndex } && _code.S[nextIndex] is VxAssign store &&
                    _code.FrameVariable(store.Value) == name && !(_code.FrameVariable(store.Target) is { } storeName && VerseCode.IsTemp(storeName)))
                {
                    target = Assign(store.Target, new VxText(head, PLow), null, false);
                    consumed = true;
                }
                else
                {
                    _materialised.Add(name);
                    target = $"{TempName(name)} := {head}";
                }
            }
            else target = Assign(resultTarget, new VxText(head, PLow), null, false);

            Line(indent, (string.IsNullOrEmpty(target) ? head : target) + ":");
            if (body.Length == 0) Line(indent + "    ", "block {}");
            else _out.Append(body);
            return consumed;
        }

        Line(indent, head + ":");
        if (body.Length == 0) Line(indent + "    ", "block {}");
        else _out.Append(body);
        return false;
    }

    #endregion

    #region expressions

    private string Expr(KismetExpression expression) => Render(expression, false).Text;

    private string ExprFailable(KismetExpression expression) => Render(expression, true).Text;

    private string Wrap(KismetExpression expression, int minimum)
    {
        var r = Render(expression, false);
        return r.Prec < minimum ? $"({r.Text})" : r.Text;
    }

    private string Postfix(KismetExpression expression) => Wrap(expression, PPostfix);

    private static R Primary(string text) => new(text, PPrimary);

    private R Render(KismetExpression? expression, bool failable)
    {
        switch (expression)
        {
            case null:
                return Primary("_");
            case VxText text:
                return new R(text.Text, text.Precedence);
            case VxString str:
                return Primary(Quote(str.Value));
            case VxArray array:
                return Primary($"array{{{string.Join(", ", array.Elements.Select(Expr))}}}");
            case VxMap map:
                return Primary($"map{{{string.Join(", ", Pairs(map.Pairs))}}}");
            case VxObject instance:
            {
                var type = instance.TypeName ?? (instance.Type?.ResolvedObject?.Object?.Value is UStruct structure
                    ? _resolver.NameOf(structure)
                    : VerseMangling.UnmangleCasedName(instance.Type?.ResolvedObject?.Name.Text ?? "object").SubstringAfterLast('-'));
                return Primary($"{type}{{{string.Join(", ", instance.Fields.Select(f => $"{FieldName(f.Field)} := {Expr(f.Value)}"))}}}");
            }
            case VxMember member:
                return Member(member.Owner, member.CookedName);
            case VxAwait awaited:
                return Render(awaited.Call, failable);
            case VxSpawn spawned:
                return new R($"spawn {{ {Expr(spawned.Call)} }}", PLow);
            case VxAssign assign:
                return new R(Assign(assign.Target, assign.Value, null, false), PLow);
            case VxSetOp op:
                return new R($"set {Expr(op.Target)} {op.Op} {Expr(op.Value)}", PLow);
            case VxYield yield:
                return Primary(string.Join(" => ", yield.Values.Select(Expr)));
            case VxNoise or VxVarDecl:
                return Primary(string.Empty);

            case EX_Self:
                return Primary("Self");
            case EX_NoObject or EX_NoInterface or EX_False:
                return Primary("false");
            case EX_True:
                return Primary("true");
            case EX_Nothing or EX_EndOfScript:
                return Primary(string.Empty);
            case EX_IntConst c:
                return Number(c.Value);
            case EX_Int64Const c:
                return Number(c.Value);
            case EX_IntZero:
                return Primary("0");
            case EX_IntOne:
                return Primary("1");
            case EX_ByteConst c:
                return Primary(c.Value.ToString(CultureInfo.InvariantCulture));
            case EX_FloatConst c:
                return Float(c.Value);
            case EX_DoubleConst c:
                return Float(c.Value);
            case EX_StringConst c:
                return Primary(Quote(c.Value));
            case EX_UnicodeStringConst c:
                return Primary(Quote(c.Value));
            case EX_NameConst c:
                return Primary(VerseMangling.FunctionName(c.Value.Text));
            case EX_ObjectConst c:
                return Primary(ObjectName(c.Value));
            case EX_PropertyConst c:
                return Primary(FieldName(VerseCode.VarName(c.Property)));

            case EX_LocalVariable or EX_LocalOutVariable:
                return Variable(VerseCode.VarName(((EX_VariableBase) expression).Variable), failable);
            case EX_InstanceVariable instanceVariable:
            {
                var name = VerseCode.VarName(instanceVariable.Variable);
                if (name == "_Self") return Primary("Self");
                return _code.TaskMode ? Variable(name, failable) : Primary(FieldName(name));
            }
            case EX_StructMemberContext member:
                return StructMember(member, failable);
            case EX_ClassContext context:
                return Context(context.ObjectExpression, context.ContextExpression);
            case EX_Context context:
                return Context(context.ObjectExpression, context.ContextExpression);
            case EX_InterfaceContext context:
                return Render(context.InterfaceValue, failable);
            case EX_ArrayGetByRef array:
                return new R($"{Postfix(array.ArrayVariable)}[{Expr(array.ArrayIndex)}]", PPostfix);
            case EX_ArrayConst array:
                return Primary($"array{{{string.Join(", ", array.Elements.Select(Expr))}}}");
            case EX_SetArray array:
                return Primary($"array{{{string.Join(", ", array.Elements.Select(Expr))}}}");
            case EX_SetMap map:
                return Primary($"map{{{string.Join(", ", Pairs(map.Elements))}}}");
            case EX_StructConst structure:
                return Primary($"{_resolver.NameOf(structure.Struct.ResolvedObject?.Object?.Value as UStruct)}{{{string.Join(", ", structure.Properties.Select(Expr))}}}");
            case EX_CastBase cast:
                return Render(cast.Target, failable);
            case EX_CallMath call:
                return Library(call, failable);
            case EX_FinalFunction call:
                return new R($"{FunctionReference(call.StackNode)}({string.Join(", ", call.Parameters.Select(Expr).Where(p => p.Length > 0))})", PPostfix);
            case EX_VirtualFunction call:
                return new R($"{VerseMangling.FunctionName(call.VirtualFunctionName.Text)}({string.Join(", ", call.Parameters.Select(Expr).Where(p => p.Length > 0))})", PPostfix);
            case EX_Return ret:
                return Render(ret.ReturnExpression, failable);
            default:
                return Primary($"<{expression.GetType().Name.SubstringAfter("EX_")}>");
        }
    }

    private IEnumerable<string> Pairs(KismetExpression[] pairs)
    {
        for (var i = 0; i + 1 < pairs.Length; i += 2)
            yield return $"{Expr(pairs[i])} => {Expr(pairs[i + 1])}";
    }

    private static R Number(long value) => new(value.ToString(CultureInfo.InvariantCulture), value < 0 ? PUnary : PPrimary);

    private static R Float(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        if (text.AsSpan().IndexOfAny('.', 'E', 'N') < 0) text += ".0";
        return new R(text, value < 0 ? PUnary : PPrimary);
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("{", "\\{").Replace("}", "\\}")
            .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";

    /// <summary>the name a cooked local reads as in source: the compiler appends a _N scope suffix</summary>
    public static string LocalName(string cooked)
    {
        var name = VerseMangling.UnmangleCasedName(cooked);
        var underscore = name.LastIndexOf('_');
        return underscore > 0 && underscore < name.Length - 1 && name.AsSpan(underscore + 1).IndexOfAnyExceptInRange('0', '9') < 0
            ? name[..underscore]
            : name;
    }

    private static string FieldName(string cooked) => VerseMangling.UnmangleCasedName(cooked);

    private static string TempName(string cooked)
    {
        var name = cooked.TrimStart('$');
        if (name.StartsWith("__verse_0x00000000_", StringComparison.Ordinal)) return VerseMangling.UnmangleCasedName(name);
        var number = name.SubstringAfterLast('_');
        var kind = name.SubstringBeforeLast('_');
        if (name.Contains("___dupe_fresh", StringComparison.Ordinal)) return $"Dup{number}";
        return kind switch
        {
            "ExprResult" or "ExprResultStack" or "ExprResultDummy" => $"Tmp{number}",
            _ => LocalName(name).Replace("_", string.Empty) + number
        };
    }

    private R Variable(string name, bool failable)
    {
        if (_env.Remove(name, out var pending))
        {
            if (pending.Value is { } value) return Render(value, failable || _usage.Failable.Contains(name));
            if (pending.Elements is { } elements) return Primary($"({string.Join(", ", elements.Values.Select(Expr))})");
        }

        if (_aliases.TryGetValue(name, out var place)) return Render(place, failable);

        if (VerseMangling.UnmangleCasedName(name) == "Argument" && _parameterByPath.TryGetValue(string.Empty, out var parameter))
            return Primary(parameter);
        if (name == "RetVal") return Primary("Result");
        if (name == "CurrentlyInstantiatedObject") return Primary("Self");
        return Primary(VerseCode.IsTemp(name) ? TempName(name) : Display(name));
    }

    private R StructMember(EX_StructMemberContext member, bool failable)
    {
        if (_structure.ArgumentPath(member) is { } path && _parameterByPath.TryGetValue(path, out var parameter))
            return Primary(parameter);

        // an element of an alias of the packed argument
        if (_code.FrameVariable(member.StructExpression) is { } aliased && _aliases.TryGetValue(aliased, out var place) &&
            _structure.ArgumentPath(place) is { } basePath && VerseCode.IsElement(VerseCode.VarName(member.Property), out var aliasElement) &&
            _parameterByPath.TryGetValue(basePath.Length == 0 ? $"Elem{aliasElement}" : $"{basePath}.Elem{aliasElement}", out var aliasParameter))
            return Primary(aliasParameter);

        var field = VerseCode.VarName(member.Property);
        if (_code.FrameVariable(member.StructExpression) is { } tuple && VerseCode.IsElement(field, out var element) &&
            _env.TryGetValue(tuple, out var pending) && pending.Elements is { } elements && elements.Remove(element, out var value))
        {
            if (elements.Count == 0 && pending.Value is null) _env.Remove(tuple);
            return Render(value, failable);
        }

        if (VerseCode.IsElement(field, out var index))
            return new R($"{Postfix(member.StructExpression)}({index})", PPostfix);
        return new R($"{Postfix(member.StructExpression)}.{FieldName(field)}", PPostfix);
    }

    private R Member(KismetExpression owner, string cookedName)
    {
        var name = FieldName(cookedName);
        var left = Render(owner, false);
        if (left.Text is "Self" or "") return Primary(name);
        return new R($"{(left.Prec < PPostfix ? $"({left.Text})" : left.Text)}.{name}", PPostfix);
    }

    private R Context(KismetExpression owner, KismetExpression member)
    {
        switch (member)
        {
            case EX_InstanceVariable field:
            {
                if (owner is EX_ObjectConst module) return Qualified(module, FieldName(VerseCode.VarName(field.Variable)));
                return Member(owner, VerseCode.VarName(field.Variable));
            }
            case EX_Self:
                return Render(owner, false);
            default:
            {
                var left = Render(owner, false);
                var right = Expr(member);
                if (left.Text is "Self" or "") return new R(right, PPostfix);
                return new R($"{(left.Prec < PPostfix ? $"({left.Text})" : left.Text)}.{right}", PPostfix);
            }
        }
    }

    /// <summary>module level names are written bare inside their own module and qualified outside it</summary>
    private R Qualified(EX_ObjectConst module, string name)
    {
        var qualifier = ModuleQualifier(module.Value);
        return qualifier is null ? Primary(name) : new R($"{qualifier}.{name}", PPostfix);
    }

    private string? ModuleQualifier(FPackageIndex? module)
    {
        if (module?.ResolvedObject is not { } resolved) return null;
        if (module.ToString().Contains("/VNI/", StringComparison.Ordinal)) return null;

        var cooked = resolved.Name.Text;
        if (_owner == cooked || _owner.StartsWith(cooked + "-", StringComparison.Ordinal)) return null;
        return VerseMangling.UnmangleCasedName(cooked.SubstringAfterLast('-'));
    }

    private string ObjectName(FPackageIndex? index)
    {
        if (index?.ResolvedObject is not { } resolved) return "false";
        if (resolved.Object?.Value is UStruct type && type.GetOrDefault<string>("PackageRelativeVersePath") is { Length: > 0 })
            return _resolver.NameOf(type);
        return VerseMangling.UnmangleCasedName(resolved.Name.Text).SubstringAfterLast('-');
    }

    private static string FunctionReference(FPackageIndex? function) =>
        VerseMangling.FunctionName(function?.ResolvedObject?.Name.Text ?? function?.ToString().SubstringAfterLast(':').Trim('\'') ?? "?");

    #endregion

    #region calls

    private R Library(EX_CallMath call, bool failable)
    {
        var name = VerseCode.CalleeName(call);
        var parameters = call.Parameters;

        switch (name)
        {
            case "CallFunction" when parameters.Length >= 1:
                return CallFunction(parameters[0], parameters[1..], failable);
            case "CallFinalFunctionWithContext" when parameters.Length >= 2:
                return FinalCallWithContext(parameters, failable);
            case "InstanceFunction" when parameters.Length == 2:
                return FunctionValue(parameters[0], parameters[1]);
            case "MakeClassVarProxy" when parameters.Length >= 3 && parameters[2] is EX_NameConst field:
                return Member(parameters[1], field.Value.Text);
            case "AccessStructMemberViaClassVarProxy" when parameters.Length >= 2 && parameters[1] is EX_NameConst field:
                return Member(parameters[0], field.Value.Text);
            case "Query" when parameters.Length == 1:
                return new R($"{Postfix(parameters[0])}?", PPostfix);
            case "IsOptionSet" when parameters.Length == 1:
                return new R($"{Postfix(parameters[0])}?", PPostfix);
            case "MakeOptionFromValue" when parameters.Length >= 1:
                return Primary($"option{{{Expr(parameters[^1])}}}");
            case "MakeUnsetOption":
                return Primary("false");
            case "Length" when parameters.Length == 1:
                return new R($"{Postfix(parameters[0])}.Length", PPostfix);
            case "Negate" when parameters.Length == 1:
                return new R($"-{Wrap(parameters[0], PUnary)}", PUnary);
            case "DynamicCastClassOrInterfaceType" when parameters.Length == 2:
                return new R($"{Postfix(parameters[1])}[{Expr(parameters[0])}]", PPostfix);
            case "GetCurrentlyInstantiatedObject":
                return Primary("Self");
            case "TaskMake" when parameters.Length >= 1:
                return new R($"spawn {{ {Expr(parameters[0])} }}", PLow);
        }

        if (name == "ConvertToDynamicallyTypedValue" && parameters is [EX_CallMath { Parameters: [EX_ObjectConst enumeration] } runtime, var constant] &&
            VerseCode.CalleeName(runtime) == "MakeRuntimeTypeEnum" && EnumLiteral(enumeration.Value, constant) is { } literal)
            return new R(literal, PPostfix);

        if (Transparent.Contains(name) && parameters.Length >= 1)
            return Render(parameters[^1], failable);

        if (Indexers.Contains(name) && parameters.Length == 2)
            return new R($"{Postfix(parameters[0])}[{Expr(parameters[1])}]", PPostfix);

        if (name.StartsWith("MakeRuntimeType", StringComparison.Ordinal))
            return Primary(string.Empty);

        if (Binary.TryGetValue(name, out var op) && parameters.Length == 2)
            return Infix(Render(parameters[0], false), op.Op, op.Prec, Render(parameters[1], false));

        return new R($"{name}({string.Join(", ", parameters.Select(Expr).Where(p => p.Length > 0))})", PPostfix);
    }

    private static FPackageIndex? EnumOf(FProperty property) => property switch
    {
        FEnumProperty e => e.Enum,
        FByteProperty { Enum: { } b } => b,
        FOptionalProperty { ValueProperty: { } inner } => EnumOf(inner),
        _ => null
    };

    /// <summary>enum_type.Member for a constant compared against or stored into an enum</summary>
    private static string? EnumLiteral(FPackageIndex? type, KismetExpression constant)
    {
        long value;
        switch (constant)
        {
            case EX_ByteConst b: value = b.Value; break;
            case EX_IntConst i: value = i.Value; break;
            case EX_Int64Const l: value = l.Value; break;
            default: return null;
        }

        if (type?.ResolvedObject?.Object?.Value is not UEnum enumeration) return null;
        foreach (var (key, number) in enumeration.Names ?? [])
        {
            if (number != value) continue;
            var member = VerseMangling.UnmangleCasedName(key.Text.SubstringAfterLast(':'));
            return $"{VerseTypeResolver.EnumName(enumeration)}.{member}";
        }

        return null;
    }

    private static R Infix(R left, string op, int prec, R right)
    {
        var l = left.Prec < prec || prec == PCmp && left.Prec == PCmp ? $"({left.Text})" : left.Text;
        var r = right.Prec <= prec ? $"({right.Text})" : right.Text;
        return new R($"{l} {op} {r}", prec);
    }

    /// <summary>the callee a temp holds, taken out of the environment so it is not printed on its own</summary>
    private KismetExpression Callee(KismetExpression callee)
    {
        if (_code.FrameVariable(callee) is { } name && _env.TryGetValue(name, out var pending) && pending.Value is { } value &&
            value is EX_CallMath function && VerseCode.CalleeName(function) == "InstanceFunction")
        {
            _env.Remove(name);
            return value;
        }

        return callee;
    }

    private R FunctionValue(KismetExpression context, KismetExpression name)
    {
        var function = name is EX_NameConst constant ? VerseMangling.FunctionName(constant.Value.Text) : Expr(name);
        if (function.StartsWith("operator.", StringComparison.Ordinal)) function = function["operator.".Length..];
        var receiver = Receiver(context);
        return receiver is null ? Primary(function) : new R($"{receiver}.{function}", PPostfix);
    }

    private string? Receiver(KismetExpression context)
    {
        switch (context)
        {
            case EX_Self:
                return null;
            case EX_ClassContext { ObjectExpression: EX_ObjectConst module, ContextExpression: EX_Self }:
                return ModuleQualifier(module.Value);
            case EX_Context { ContextExpression: EX_Self } owner:
            {
                var r = Render(owner.ObjectExpression, false);
                if (r.Text is "Self" or "") return null;
                return r.Prec < PPostfix ? $"({r.Text})" : r.Text;
            }
            default:
            {
                var r = Render(context, false);
                return r.Text is "Self" or "" ? null : r.Prec < PPostfix ? $"({r.Text})" : r.Text;
            }
        }
    }

    private R CallFunction(KismetExpression callee, KismetExpression[] arguments, bool failable)
    {
        // a call into a suspends function passes the calling task and its resume state first
        if (arguments is [EX_Self or EX_NoObject, EX_Int64Const, EX_Int64Const, ..]) arguments = arguments[3..];

        callee = Callee(callee);
        if (callee is not EX_CallMath { Parameters: [var context, EX_NameConst mangled] } function ||
            VerseCode.CalleeName(function) != "InstanceFunction")
        {
            var target = Render(callee, false);
            var args = arguments.Length == 1 ? Arguments(arguments[0], null) : arguments.ToList();
            var open = failable ? '[' : '(';
            var close = failable ? ']' : ')';
            var head = target.Prec < PPostfix ? $"({target.Text})" : target.Text;
            return new R($"{head}{open}{string.Join(", ", args.Select(Expr).Where(a => a.Length > 0))}{close}", PPostfix);
        }

        var decoded = VerseMangling.StripOwnerQualifier(VerseMangling.Decode(mangled.Value.Text));
        var receiver = Receiver(context);
        return FormatCall(receiver, decoded, arguments.Length == 1 ? arguments[0] : null, failable);
    }

    private R FinalCallWithContext(KismetExpression[] parameters, bool failable)
    {
        var arguments = parameters[2..];
        if (arguments is [EX_Self or EX_NoObject, EX_Int64Const, EX_Int64Const, ..]) arguments = arguments[3..];

        var function = parameters[1] is EX_ObjectConst constant ? constant.Value?.ResolvedObject : null;
        var cooked = function?.Name.Text ?? "?";
        var decoded = VerseMangling.StripOwnerQualifier(VerseMangling.Decode(cooked));

        // a call pinned to one implementation from inside an override is a call to the parent's
        var ownerName = function?.Outer?.Name.Text;
        var receiver = Receiver(parameters[0]);
        if (receiver is null && ownerName is not null && ownerName != _owner) receiver = "(super:)";
        var call = FormatCall(receiver, decoded, arguments.Length == 1 ? arguments[0] : null, failable);
        return receiver == "(super:)" ? new R(call.Text.Replace("(super:).", "(super:)"), PPostfix) : call;
    }

    private R FormatCall(string? receiver, string decoded, KismetExpression? argument, bool failable)
    {
        var name = decoded;
        var parameters = new List<string>();
        var open = decoded.IndexOf('(');
        var knownParameters = open >= 0;
        if (knownParameters)
        {
            name = decoded[..open];
            var close = MatchingParen(decoded, open);
            var inside = decoded[(open + 1)..close];
            var where = inside.IndexOf(" where ", StringComparison.Ordinal);
            if (where >= 0) inside = inside[..where];
            parameters = VerseSignatureParser.SplitParameters(inside);
            if (parameters is [":tuple()"]) parameters.Clear();
            if (decoded.AsSpan(close).Contains("<decides>", StringComparison.Ordinal)) failable = true;
        }

        var args = Arguments(argument, knownParameters ? parameters.Count : null);
        var o = failable ? "[" : "(";
        var c = failable ? "]" : ")";

        // operator'+' and friends
        if (name.StartsWith("operator", StringComparison.Ordinal) && !name.StartsWith("operator.", StringComparison.Ordinal))
        {
            var op = name["operator".Length..].Trim('\'');
            if (args.Count == 2 && op.Length > 0)
            {
                var prec = op switch
                {
                    "*" or "/" => PMul,
                    "+" or "-" => PAdd,
                    "()" or "[]" => -1,
                    _ => PCmp
                };
                if (prec >= 0) return Infix(Render(args[0], false), op, prec, Render(args[1], false));
            }

            if (args.Count == 1 && op == "-") return new R($"-{Wrap(args[0], PUnary)}", PUnary);
        }

        // (Receiver:type).Name(rest) is lowered to operator'.Name'(Receiver, rest)
        if (name.StartsWith("operator.", StringComparison.Ordinal) && args.Count >= 1)
        {
            var method = name["operator.".Length..].Trim('\'');
            var rest = args.Skip(1).ToList();
            var restType = parameters.Count > 1 ? parameters[1] : null;
            if (rest.Count == 1 && restType is not null && restType.StartsWith(":tuple(", StringComparison.Ordinal))
            {
                var inner = VerseSignatureParser.SplitParameters(restType[":tuple(".Length..^1]);
                rest = Arguments(rest[0], inner.Count);
                parameters = [parameters[0], .. inner];
            }

            var self = Wrap(args[0], PPostfix);
            return new R($"{self}.{method}{o}{string.Join(", ", Named(rest, parameters.Skip(1).ToList()))}{c}", PPostfix);
        }

        var call = $"{name}{o}{string.Join(", ", Named(args, parameters))}{c}";
        return receiver is null ? new R(call, PPostfix) : new R($"{receiver}.{call}", PPostfix);
    }

    /// <summary>named arguments the caller left out are passed unset, the rest by name</summary>
    private IEnumerable<string> Named(List<KismetExpression> args, List<string> parameters)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var parameter = i < parameters.Count ? parameters[i] : null;
            if (parameter is not null && VerseSignatureParser.IsNamed(parameter))
            {
                if (args[i] is EX_CallMath unset && VerseCode.CalleeName(unset) == "MakeUnsetOption") continue;
                if (IsUnsetTemp(args[i])) continue;

                var label = VerseSignatureParser.NamedLabel(parameter);
                var value = args[i] is EX_CallMath made && VerseCode.CalleeName(made) == "MakeOptionFromValue"
                    ? made.Parameters[^1]
                    : args[i];
                var text = Expr(value);
                if (text.StartsWith("option{", StringComparison.Ordinal) && text.EndsWith('}')) text = text[7..^1];
                yield return $"{label} := {text}";
                continue;
            }

            var rendered = Expr(args[i]);
            if (rendered.Length > 0) yield return rendered;
        }
    }

    private bool IsUnsetTemp(KismetExpression argument)
    {
        if (_code.FrameVariable(argument) is not { } name || !_env.TryGetValue(name, out var pending) ||
            pending.Value is not EX_CallMath unset || VerseCode.CalleeName(unset) != "MakeUnsetOption")
            return false;

        _env.Remove(name);
        return true;
    }

    /// <summary>the argument list a packed argument stands for</summary>
    private List<KismetExpression> Arguments(KismetExpression? argument, int? count)
    {
        if (argument is null || count == 0)
        {
            // an unused empty tuple temp
            if (argument is not null && _code.FrameVariable(argument) is { } empty) _env.Remove(empty);
            return [];
        }

        if (IsEmptyTuple(argument)) return [];
        if (count == 1) return [argument];

        // the function's own packed argument passed on whole is its parameter list
        if (_structure.ArgumentPath(argument) == string.Empty && _parameters.Count > 1 &&
            (count is null || count == _parameters.Count))
            return _parameters.Select(p => (KismetExpression) new VxText(p.Name ?? "_", PPrimary)).ToList();

        if (_code.FrameVariable(argument) is { } name && VerseCode.IsTemp(name))
        {
            if (_env.TryGetValue(name, out var pending) && pending.Elements is { } elements && pending.Value is null)
            {
                _env.Remove(name);
                return elements.Values.ToList();
            }

            // a tuple nothing was ever stored into is the empty argument list
            if (_usage.DefCount.GetValueOrDefault(name) == 0 && !_usage.TupleTemps.Contains(name) && !_env.ContainsKey(name))
                return [];
        }

        return [argument];
    }

    /// <summary>a temp nothing was ever stored into stands for the empty tuple</summary>
    private bool IsEmptyTuple(KismetExpression argument)
    {
        // looked at through the temp holding it, which is then used up
        if (_code.FrameVariable(argument) is { } holder && _env.TryGetValue(holder, out var pending) && pending.Value is { } held &&
            IsEmptyTuple(held))
        {
            _env.Remove(holder);
            return true;
        }

        if (argument is EX_CallMath { Parameters: [_, var inner] } convert && VerseCode.CalleeName(convert) == "ConvertToDynamicallyTypedValue")
            argument = inner;
        return _code.FrameVariable(argument) is { } name && VerseCode.IsTemp(name) && _usage.DefCount.GetValueOrDefault(name) == 0 &&
               !_usage.TupleTemps.Contains(name) && !_env.ContainsKey(name);
    }

    private static int MatchingParen(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i;
        }

        return text.Length - 1;
    }

    #endregion
}
