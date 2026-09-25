using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Kismet;

namespace FModel.Services.Verse;

internal abstract class VNode;

/// <summary>one normalised statement</summary>
internal sealed class VStatement(int index) : VNode
{
    public readonly int Index = index;
}

/// <summary>an expression that fails the enclosing failure context when it fails</summary>
internal sealed class VCheck(KismetExpression condition, int index) : VNode
{
    public readonly KismetExpression Condition = condition;
    public readonly int Index = index;
}

internal sealed class VNot(List<VNode> items) : VNode
{
    public readonly List<VNode> Items = items;
}

internal sealed class VIf(List<VNode> condition, int conditionEnd, List<VNode> then, List<VNode>? @else) : VNode
{
    public readonly List<VNode> Condition = condition;
    public readonly int ConditionEnd = conditionEnd;
    public readonly List<VNode> Then = then;
    public readonly List<VNode>? Else = @else;
}

/// <summary><c>(Left) or (Right)</c> inside a failure context</summary>
internal sealed class VOr(List<VNode> left, List<VNode> right) : VNode
{
    public readonly List<VNode> Left = left;
    public readonly List<VNode> Right = right;
}

internal sealed class VLoop(List<VNode> body) : VNode
{
    public readonly List<VNode> Body = body;
}

internal enum ForKind { Array, Map, Range }

internal sealed class VFor : VNode
{
    public ForKind Kind;
    public KismetExpression? Source;
    public KismetExpression? RangeFrom, RangeTo;
    public string? Key, Item;
    public List<VNode> Filters = [];
    public int FiltersEnd;
    public List<VNode> Body = [];
    public KismetExpression? ResultTarget;
}

/// <summary>race: / sync: with one entry per branch</summary>
internal sealed class VConcurrent(string kind, List<List<VNode>> branches) : VNode
{
    public readonly string Kind = kind;
    public readonly List<List<VNode>> Branches = branches;
}

internal sealed class VBreak : VNode;

internal sealed class VReturn : VNode;

internal sealed class VFail : VNode;

internal sealed class VerseStructureException(string message) : Exception(message);

/// <summary>
/// Rebuilds Verse control flow from the transactional copy of a function.
///
/// Every failure context is bracketed by the software transactional memory calls the compiler emits
/// for it: StmBegin opens it, a failing expression jumps to a StmRollback, and success runs into a
/// StmCommit (an if / a for filter) or into a StmRollback followed by a jump (a not). Loops are the
/// backward jumps, and the for forms are recognised from the counters they keep. Anything outside
/// those shapes throws, and the caller falls back to the plain listing.
/// </summary>
internal sealed class VerseStructurer
{
    private sealed class FailRef
    {
        public int Target = -1;
    }

    private sealed record Ctx(FailRef? Fail, int LoopExit, int End, bool Top = false, int LoopHead = -1);

    private sealed class ForInfo
    {
        public ForKind Kind;
        public int PreStart, Head, Tail, Exit, After, BodyStart, Increment;
        public KismetExpression? Source, RangeFrom, RangeTo;
        public string? Key, Item, ForResult;
        public KismetExpression? ResultTarget;
        public List<int> Prelude = [];
    }

    private readonly VerseCode _code;
    private readonly List<KismetExpression> _s;
    private readonly Dictionary<int, ForInfo> _forByStart = new();
    private readonly Dictionary<int, int> _loopTail = new();

    /// <summary>statements the structure consumed, the usage analysis must not count them</summary>
    public readonly HashSet<int> Consumed = [];

    /// <summary>the value each named parameter defaults to, keyed by its element path</summary>
    public readonly Dictionary<string, KismetExpression> Defaults = new(StringComparer.Ordinal);

    public VerseStructurer(VerseCode code)
    {
        _code = code;
        _s = code.S;
        FindLoops();
    }

    public List<VNode> Parse()
    {
        var start = ParameterDefaults(0);
        var i = start;
        return Sequence(ref i, _s.Count, new Ctx(null, -1, _s.Count, true), false, out _);
    }

    #region landmarks

    private int Canon(int index)
    {
        while (index < _s.Count && _code.IsNoise(index)) index++;
        return index;
    }

    private int Target(EX_Jump jump) => Canon(_code.IndexOf(jump.CodeOffset));

    private bool IsReturnStatement(int index) => index < _s.Count && _s[index] is EX_Return;

    private static bool IsRetVal(KismetExpression target) => target is EX_LocalOutVariable out_ && VerseCode.VarName(out_.Variable) == "RetVal";

    private enum Exit { None, Return, Fail, Suspend }

    /// <summary>
    /// what leaving the frame at an index means: a plain return, the failure of a decides function
    /// (RetVal left unset), or in a task the suspension (RetVal 1) as opposed to completion (RetVal 0)
    /// </summary>
    private Exit ExitAt(int index, out int after)
    {
        after = index;
        index = Canon(index);
        if (index >= _s.Count) return Exit.None;

        var kind = Exit.Return;
        if (_s[index] is VxAssign assign && IsRetVal(assign.Target))
        {
            if (assign.Value is EX_CallMath unset && VerseCode.CalleeName(unset) == "MakeUnsetOption") kind = Exit.Fail;
            else if (_code.TaskMode && assign.Value is EX_Int64Const state) kind = state.Value == 0 ? Exit.Return : Exit.Suspend;
            else return Exit.None;
            index = Canon(index + 1);
        }

        if (index >= _s.Count || VerseCode.Stm(_s[index]) != StmKind.LeaveFrame) return Exit.None;
        var ret = Canon(index + 1);
        if (!IsReturnStatement(ret)) return Exit.None;

        after = ret + 1;
        return kind;
    }

    private void FindLoops()
    {
        for (var j = 0; j < _s.Count; j++)
        {
            if (_s[j] is not EX_Jump jump || jump is EX_JumpIfNot) continue;
            var head = Target(jump);
            if (head > j) continue;

            if (TryArrayFor(head, j, out var info) || TryMapFor(head, j, out info) || TryRangeFor(head, j, out info))
            {
                _forByStart[info!.PreStart] = info;
                continue;
            }

            if (!_loopTail.TryGetValue(head, out var tail) || tail < j) _loopTail[head] = j;
        }
    }

    private int PreviousReal(int index)
    {
        index--;
        while (index >= 0 && (_code.IsNoise(index) || VerseCode.Stm(_s[index]) == StmKind.Begin)) index--;
        return index;
    }

    private int NextReal(int index)
    {
        index++;
        while (index < _s.Count && _code.IsNoise(index)) index++;
        return index;
    }

    private string? Var(KismetExpression? expression) => _code.FrameVariable(expression);

    private static bool IsIncrement(KismetExpression statement, string counter) =>
        statement is VxAssign { Value: EX_CallMath add } assign && VerseCode.CalleeName(add) == "Add" &&
        VerseCode.VariableOf(assign.Target) == counter && add.Parameters.Length == 2 &&
        VerseCode.VariableOf(add.Parameters[0]) == counter;

    /// <summary>the StmBegin run that opens a for, and the markers that close it past its exit</summary>
    private void Brackets(ForInfo info, int firstPreheader)
    {
        // a for opens two contexts before its counters; what is evaluated between them and the
        // counters is the iterable being computed, and is printed ahead of the for
        var start = firstPreheader;
        var begins = 0;
        var prelude = new List<int>();
        for (var k = firstPreheader - 1; k >= 0 && begins < 2; k--)
        {
            if (_code.IsNoise(k)) continue;
            if (VerseCode.Stm(_s[k]) == StmKind.Begin)
            {
                begins++;
                start = k;
                continue;
            }

            if (_s[k] is not (VxAssign or EX_JumpIfNot) || begins > 0) break;
            prelude.Add(k);
        }

        if (begins == 2)
        {
            prelude.Reverse();
            info.Prelude = prelude;
        }
        else
        {
            start = firstPreheader;
            for (var k = firstPreheader - 1; k >= 0; k--)
            {
                if (_code.IsNoise(k)) continue;
                if (VerseCode.Stm(_s[k]) != StmKind.Begin) break;
                start = k;
            }
        }

        info.PreStart = start;

        // the counter compare fails into a rollback run that ends the loop, then the result is moved out
        var after = info.Exit;
        var commits = 0;
        while (after < _s.Count)
        {
            if (_code.IsNoise(after) || VerseCode.Stm(_s[after]) == StmKind.Rollback)
            {
                after++;
                continue;
            }

            if (VerseCode.Stm(_s[after]) == StmKind.Commit && commits++ == 0)
            {
                after++;
                continue;
            }

            break;
        }
        if (after < _s.Count && _s[after] is VxAssign { Value: var moved } move && Var(moved) is { } result &&
            result.StartsWith("$ForResult", StringComparison.Ordinal))
        {
            info.ForResult = result;
            info.ResultTarget = move.Target;
            after++;
        }

        info.After = after;
    }

    /// <summary>
    /// $ForIndex = 0; $ForLength = Length(X); head: if (!($ForIndex &lt; $ForLength)) goto exit;
    /// Item = X[$ForIndex]; ...; $ForIndex += 1; goto head
    /// </summary>
    private bool TryArrayFor(int head, int tail, out ForInfo? info)
    {
        info = null;
        if (_s[head] is not EX_JumpIfNot { BooleanExpression: EX_CallMath less } exit || VerseCode.CalleeName(less) != "PredicateLess" ||
            less.Parameters.Length != 2 || Var(less.Parameters[0]) is not { } index ||
            Var(less.Parameters[1]) is not { } length || !length.StartsWith("$ForLength", StringComparison.Ordinal))
            return false;

        var lengthAt = PreviousReal(head);
        if (lengthAt < 0 || _s[lengthAt] is not VxAssign { Value: EX_CallMath lengthCall } lengthStore || Var(lengthStore.Target) != length ||
            VerseCode.CalleeName(lengthCall) != "Length" || lengthCall.Parameters.Length != 1)
            return false;

        var indexAt = PreviousReal(lengthAt);
        if (indexAt < 0 || _s[indexAt] is not VxAssign indexStore || Var(indexStore.Target) != index) return false;

        var source = lengthCall.Parameters[0];
        var first = indexAt;
        if (Var(source) is { } iterable && iterable.StartsWith("$ForIterable", StringComparison.Ordinal))
        {
            var iterableAt = PreviousReal(indexAt);
            if (iterableAt < 0 || _s[iterableAt] is not VxAssign iterableStore || Var(iterableStore.Target) != iterable) return false;
            source = iterableStore.Value;
            first = iterableAt;
        }

        var bind = NextReal(head);
        if (bind >= tail || _s[bind] is not VxAssign { Value: var element } item || Var(item.Target) is not { } itemName ||
            element is not (EX_ArrayGetByRef or EX_CallMath { Parameters.Length: 2 }) ||
            element is EX_CallMath indexer && VerseCode.CalleeName(indexer) != "UncheckedCall")
            return false;

        var increment = PreviousReal(tail);
        if (increment <= bind || !IsIncrement(_s[increment], index)) return false;

        info = new ForInfo
        {
            Kind = ForKind.Array, Head = head, Tail = tail, Exit = Target(exit), Source = source, Item = itemName,
            BodyStart = bind + 1, Increment = increment
        };

        // for (Index -> Item : X) counts with the named index itself
        if (!index.StartsWith("$ForIndex", StringComparison.Ordinal)) info.Key = index;

        // for (Index -> Item : X) binds the counter as well
        var keyAt = NextReal(bind);
        if (keyAt < tail && _s[keyAt] is VxAssign key && Var(key.Target) is { } keyName && !VerseCode.IsTemp(keyName) &&
            ReadsOnly(key.Value, index))
        {
            info.Key = keyName;
            info.BodyStart = keyAt + 1;
        }

        Brackets(info, first);
        return true;
    }

    private bool ReadsOnly(KismetExpression value, string variable) => value switch
    {
        EX_CallMath convert when convert.Parameters.Length == 1 => ReadsOnly(convert.Parameters[0], variable),
        _ => Var(value) == variable
    };

    /// <summary>
    /// $ForIndex = 0; head: $ForIndex = GetNextValidIndex(Map, $ForIndex); if ($ForIndex == -1) goto exit;
    /// Key = GetKeyByIndex(...); Value = GetValueByIndex(...); ...; $ForIndex += 1; goto head
    /// </summary>
    private bool TryMapFor(int head, int tail, out ForInfo? info)
    {
        info = null;
        if (_s[head] is not VxAssign { Value: EX_CallMath next } advance || VerseCode.CalleeName(next) != "GetNextValidIndex" ||
            next.Parameters.Length != 2 || Var(advance.Target) is not { } index)
            return false;

        var test = NextReal(head);
        if (test >= tail || _s[test] is not EX_JumpIfNot exit) return false;

        var indexAt = PreviousReal(head);
        if (indexAt < 0 || _s[indexAt] is not VxAssign indexStore || Var(indexStore.Target) != index) return false;

        var source = next.Parameters[0];
        var first = indexAt;
        if (Var(source) is { } iterable && iterable.StartsWith("$ForIterable", StringComparison.Ordinal))
        {
            var iterableAt = PreviousReal(indexAt);
            if (iterableAt < 0 || _s[iterableAt] is not VxAssign iterableStore || Var(iterableStore.Target) != iterable) return false;
            source = iterableStore.Value;
            first = iterableAt;
        }

        string? key = null, value = null;
        var body = NextReal(test);
        while (body < tail && _s[body] is VxAssign { Value: EX_CallMath lookup } bind && VerseCode.CalleeName(lookup) is "GetKeyByIndex" or "GetValueByIndex")
        {
            if (VerseCode.CalleeName(lookup) == "GetKeyByIndex") key = Var(bind.Target);
            else value = Var(bind.Target);
            body = NextReal(body);
        }

        var increment = PreviousReal(tail);
        if (increment < body || !IsIncrement(_s[increment], index)) return false;

        info = new ForInfo
        {
            Kind = ForKind.Map, Head = head, Tail = tail, Exit = Target(exit), Source = source, Key = key, Item = value,
            BodyStart = body, Increment = increment
        };
        Brackets(info, first);
        return true;
    }

    /// <summary>
    /// It.Left = A; It.Right = B; I = It.Left; head: if (!(I &lt;= It.Right)) goto exit; ...; I += 1; goto head
    /// </summary>
    private bool TryRangeFor(int head, int tail, out ForInfo? info)
    {
        info = null;
        if (_s[head] is not EX_JumpIfNot { BooleanExpression: EX_CallMath lessEqual } exit ||
            VerseCode.CalleeName(lessEqual) != "PredicateLessEqual" || lessEqual.Parameters.Length != 2 ||
            Var(lessEqual.Parameters[0]) is not { } item ||
            lessEqual.Parameters[1] is not EX_StructMemberContext { StructExpression: var rangeRight } ||
            Var(rangeRight) is not { } range || !range.StartsWith("$ForIterable", StringComparison.Ordinal))
            return false;

        var itemAt = PreviousReal(head);
        if (itemAt < 0 || _s[itemAt] is not VxAssign itemStore || Var(itemStore.Target) != item) return false;
        var rightAt = PreviousReal(itemAt);
        if (rightAt < 0 || _s[rightAt] is not VxAssign { Target: EX_StructMemberContext right } rightStore || Var(right.StructExpression) != range) return false;
        var leftAt = PreviousReal(rightAt);
        if (leftAt < 0 || _s[leftAt] is not VxAssign { Target: EX_StructMemberContext left } leftStore || Var(left.StructExpression) != range) return false;

        var increment = PreviousReal(tail);
        if (!IsIncrement(_s[increment], item)) return false;

        info = new ForInfo
        {
            Kind = ForKind.Range, Head = head, Tail = tail, Exit = Target(exit), Item = item,
            RangeFrom = leftStore.Value, RangeTo = rightStore.Value, BodyStart = NextReal(head), Increment = increment
        };
        Brackets(info, leftAt);
        return true;
    }

    /// <summary>
    /// a named parameter the caller left out is filled in at the top of the function:
    /// <c>if (!IsOptionSet(Arg.ElemN)) { Arg.ElemN = option{Default} }</c>
    /// </summary>
    private int ParameterDefaults(int start)
    {
        var i = Canon(start);
        while (i < _s.Count && _s[i] is EX_JumpIfNot { BooleanExpression: EX_CallMath { Parameters: [var parameter] } test } missing &&
               VerseCode.CalleeName(test) == "IsOptionSet" && ArgumentPath(parameter) is { } path)
        {
            var skip = NextReal(i);
            if (skip >= _s.Count || _s[skip] is not EX_Jump over || over is EX_JumpIfNot) break;

            var fill = Target(missing);
            var resume = Target(over);
            if (fill != NextReal(skip) || resume <= fill) break;

            for (var k = fill; k < resume; k++)
            {
                if (_s[k] is VxAssign { Value: EX_CallMath { Parameters: [_, var value] } made } store &&
                    VerseCode.CalleeName(made) == "MakeOptionFromValue" && ArgumentPath(store.Target) == path)
                    Defaults[path] = value;
            }

            for (var k = i; k < resume; k++) Consumed.Add(k);
            i = resume;
        }

        return i;
    }

    /// <summary>"" for the argument itself, "Elem1" / "Elem1.Elem0" for the tuple element a parameter was packed into</summary>
    public string? ArgumentPath(KismetExpression? expression)
    {
        switch (expression)
        {
            case EX_StructMemberContext member when ArgumentPath(member.StructExpression) is { } owner &&
                                                    VerseCode.IsElement(VerseCode.VarName(member.Property), out var element):
                return owner.Length == 0 ? $"Elem{element}" : $"{owner}.Elem{element}";
            default:
                return _code.FrameVariable(expression) is { } name && VerseMangling.UnmangleCasedName(name) == "Argument"
                    ? string.Empty
                    : null;
        }
    }

    #endregion

    #region parsing

    private List<VNode> Parse(int start, int end, Ctx ctx)
    {
        var i = start;
        return Sequence(ref i, end, ctx, false, out _);
    }

    /// <summary>
    /// the statements from i up to end; inside a failure context also stops at the commit or rollback
    /// that ends it, which is returned through terminator
    /// </summary>
    private List<VNode> Sequence(ref int i, int end, Ctx ctx, bool inContext, out (StmKind Kind, int Index) terminator)
    {
        terminator = (StmKind.None, -1);
        var nodes = new List<VNode>();

        while (i < end)
        {
            if (_code.IsNoise(i) || Consumed.Contains(i))
            {
                i++;
                continue;
            }

            if (_forByStart.TryGetValue(i, out var loop) && loop.After <= Math.Max(end, ctx.End))
            {
                foreach (var index in loop.Prelude)
                    nodes.Add(_s[index] is EX_JumpIfNot check ? new VCheck(check.BooleanExpression, index) : new VStatement(index));
                nodes.Add(ParseFor(loop, ctx));
                i = loop.After;
                continue;
            }

            if (_loopTail.TryGetValue(i, out var tail) && tail < end)
            {
                var exit = Canon(tail + 1);
                nodes.Add(new VLoop(Parse(i, tail, new Ctx(null, exit, tail, false, i))));
                i = tail + 1;
                continue;
            }

            switch (ExitAt(i, out var afterExit))
            {
                case Exit.Return:
                    // the fall through end of the function, or a return written out in place
                    if (ctx.Top && !inContext) return nodes;
                    nodes.Add(new VReturn());
                    i = afterExit;
                    continue;
                case Exit.Fail:
                    nodes.Add(new VFail());
                    i = afterExit;
                    continue;
                case Exit.Suspend:
                    i = afterExit;
                    continue;
            }

            if (_code.TaskMode && _s[i] is VxAssign { Target: var flagTarget, Value: var flagValue } && Var(flagTarget) is { } flag)
            {
                if (flag.StartsWith("$AsyncEndCount", StringComparison.Ordinal) && flagValue is EX_Int64Const { Value: 0 })
                {
                    nodes.Add(ParseSync(ref i, flag));
                    continue;
                }

                if (flag.StartsWith("$AsyncBeginCount", StringComparison.Ordinal) && flagValue is EX_True)
                {
                    nodes.Add(ParseRace(ref i, flag));
                    continue;
                }
            }

            var statement = _s[i];
            switch (VerseCode.Stm(statement))
            {
                case StmKind.Begin:
                    nodes.AddRange(ParseContext(ref i, end, ctx));
                    continue;
                case StmKind.Commit or StmKind.Rollback:
                    if (inContext)
                    {
                        terminator = (VerseCode.Stm(statement), i);
                        return nodes;
                    }

                    i++;
                    continue;
                case StmKind.EnterFrame or StmKind.LeaveFrame:
                    i++;
                    continue;
            }

            switch (statement)
            {
                case EX_JumpIfNot conditional:
                {
                    var target = Target(conditional);
                    if (ctx.Fail is { } fail && target > i && (fail.Target < 0 || fail.Target == target))
                    {
                        fail.Target = target;
                        nodes.Add(new VCheck(conditional.BooleanExpression, i));
                    }
                    else if (ExitAt(target, out _) == Exit.Fail)
                    {
                        nodes.Add(new VCheck(conditional.BooleanExpression, i));
                    }
                    else throw new VerseStructureException($"conditional jump at {statement.StatementIndex} to {conditional.CodeOffset}");

                    i++;
                    continue;
                }
                case EX_Jump jump:
                {
                    var target = Target(jump);
                    if (target == Canon(end) || target == Canon(ctx.End))
                    {
                        // falls through to the join after the block this closes
                    }
                    else if (target == ctx.LoopExit) nodes.Add(new VBreak());
                    else if (target == ctx.LoopHead)
                    {
                        // back to the top of the loop: the rest of this iteration is skipped, which
                        // only happens where the iteration ends anyway
                    }
                    else if (ExitAt(target, out _) is Exit.Return) nodes.Add(new VReturn());
                    else if (ExitAt(target, out _) is Exit.Fail) nodes.Add(new VFail());
                    else throw new VerseStructureException($"jump at {statement.StatementIndex} to {jump.CodeOffset}");

                    i++;
                    continue;
                }
                case EX_Return:
                    i++;
                    continue;
            }

            nodes.Add(new VStatement(i));
            i++;
        }

        return nodes;
    }

    /// <summary>a StmBegin opens a failure context: an if, a not, or a block that cannot fail</summary>
    private List<VNode> ParseContext(ref int i, int end, Ctx ctx)
    {
        var fail = new FailRef();
        var k = i + 1;
        var items = Sequence(ref k, end, ctx with { Fail = fail }, true, out var terminator);
        if (terminator.Kind == StmKind.None)
            throw new VerseStructureException($"failure context at {_s[i].StatementIndex} never ends");

        if (terminator.Kind == StmKind.Rollback)
        {
            // not: the inner context succeeding rolls back and fails the outer one
            var jumpAt = NextReal(terminator.Index);
            if (jumpAt >= _s.Count || _s[jumpAt] is not EX_Jump failed || failed is EX_JumpIfNot)
                throw new VerseStructureException($"rollback at {_s[terminator.Index].StatementIndex} is not a not");

            var outer = Target(failed);
            if (ctx.Fail is { } enclosing && (enclosing.Target < 0 || enclosing.Target == outer)) enclosing.Target = outer;
            else if (ExitAt(outer, out _) != Exit.Fail)
                throw new VerseStructureException($"not at {_s[terminator.Index].StatementIndex} fails to {failed.CodeOffset}");

            if (fail.Target < 0) throw new VerseStructureException($"not at {_s[i].StatementIndex} cannot fail");
            if (VerseCode.Stm(_s[fail.Target]) != StmKind.Rollback)
                throw new VerseStructureException($"not at {_s[i].StatementIndex} lands on a non rollback");

            i = fail.Target + 1;
            return [new VNot(items)];
        }

        var afterCommit = terminator.Index + 1;
        if (fail.Target < 0)
        {
            // nothing in it can fail, it is just a block; a jump over a rollback that only the
            // runtime's own checks could reach skips that dead path
            i = afterCommit;
            var skipAt = NextReal(terminator.Index);
            if (skipAt < _s.Count && _s[skipAt] is EX_Jump deadSkip and not EX_JumpIfNot)
            {
                var landing = NextReal(skipAt);
                var join = Target(deadSkip);
                if (landing < _s.Count && VerseCode.Stm(_s[landing]) == StmKind.Rollback && join > landing && join <= Canon(end))
                {
                    for (var dead = skipAt; dead < join; dead++) Consumed.Add(dead);
                    i = join;
                }
            }

            return items;
        }

        var rollback = fail.Target;
        if (VerseCode.Stm(_s[rollback]) != StmKind.Rollback || rollback < afterCommit)
            throw new VerseStructureException($"if at {_s[i].StatementIndex} fails to {_s[rollback].StatementIndex}");

        var thenEnd = rollback;
        var next = rollback + 1;
        List<VNode>? @else = null;

        // the then part may be laid out after the else part: the commit jumps straight over the
        // rollback to it, and it runs on to the end of the enclosing block
        var first = NextReal(terminator.Index);
        if (ctx.Fail is null && first < rollback && _s[first] is EX_Jump over and not EX_JumpIfNot)
        {
            var thenStart = Target(over);
            if (thenStart > rollback && thenStart <= Canon(end))
            {
                var elsewhere = Parse(rollback + 1, thenStart, ctx with { Fail = null, End = thenStart });
                var laterThen = Parse(thenStart, end, ctx with { Fail = null });
                i = end;
                return [new VIf(items, terminator.Index, laterThen, elsewhere.Count > 0 ? elsewhere : null)];
            }
        }

        var last = rollback - 1;
        while (last >= afterCommit && _code.IsNoise(last)) last--;
        if (last >= afterCommit && _s[last] is EX_Jump skip and not EX_JumpIfNot)
        {
            var join = Target(skip);
            if (join > rollback && join <= Canon(end))
            {
                thenEnd = last;
                if (join > Canon(rollback + 1))
                {
                    if (ctx.Fail is not null && !HasReal(afterCommit, thenEnd))
                    {
                        // inside a condition, if (A) {} else {B} where B fails the enclosing context is A or B
                        var right = Parse(rollback + 1, join, ctx with { End = join });
                        i = join;
                        return [new VOr(items, right)];
                    }

                    @else = Parse(rollback + 1, join, ctx with { Fail = null, End = join });
                }

                next = join;
            }
        }

        var then = Parse(afterCommit, thenEnd, ctx with { Fail = null, End = thenEnd });
        i = next;
        return [new VIf(items, terminator.Index, then, @else)];
    }

    private bool IsFlagSet(int index, bool value, out string flag)
    {
        flag = string.Empty;
        if (index >= _s.Count || _s[index] is not VxAssign { Target: var target, Value: var set } || Var(target) is not { } name ||
            !name.StartsWith("$AsyncBeginCount", StringComparison.Ordinal))
            return false;

        flag = name;
        return value ? set is EX_True : set is EX_False;
    }

    /// <summary>
    /// race: every branch runs until its first suspension in turn, each guarded by a begin flag, and
    /// the first to finish jumps to the join, which clears the race flag and cancels the others
    /// </summary>
    private VConcurrent ParseRace(ref int i, string race)
    {
        var join = -1;
        for (var k = i + 1; k < _s.Count; k++)
        {
            if (_s[k] is EX_JumpIfNot { BooleanExpression: var test } && Var(test) == race && IsFlagSet(NextReal(k), false, out var cleared) &&
                cleared == race)
            {
                join = k;
                break;
            }
        }

        if (join < 0 || !IsFlagSet(NextReal(i), true, out var first))
            throw new VerseStructureException($"race at {_s[i].StatementIndex} has no join");

        var seen = new HashSet<string>(StringComparer.Ordinal) { race, first };
        var branches = new List<List<VNode>>();
        var start = NextReal(NextReal(i));
        while (true)
        {
            var end = -1;
            for (var k = start; k < join; k++)
            {
                if (_s[k] is EX_Jump done and not EX_JumpIfNot && Target(done) == Canon(join))
                {
                    end = k;
                    break;
                }
            }

            if (end < 0) throw new VerseStructureException($"race branch at {_s[start].StatementIndex} never finishes");
            branches.Add(Parse(start, end, new Ctx(null, -1, end)));

            var next = -1;
            for (var k = end + 1; k < join; k++)
            {
                if (IsFlagSet(k, true, out var flag) && seen.Add(flag))
                {
                    next = k;
                    break;
                }
            }

            if (next < 0) break;
            start = NextReal(next);
        }

        // after the join: the race flag is cleared and the other branches are cancelled
        var after = NextReal(NextReal(join));
        while (after < _s.Count && (_code.IsNoise(after) || _s[after] is EX_CallMath cancel && VerseCode.CalleeName(cancel) == "TaskCancel"))
            after++;

        i = after;
        return new VConcurrent("race", branches);
    }

    /// <summary>
    /// sync: every branch counts itself done in an end counter, and the join waits for the counter to
    /// reach the number of branches
    /// </summary>
    private VConcurrent ParseSync(ref int i, string counter)
    {
        var branches = new List<List<VNode>>();
        var flagAt = NextReal(i);
        while (true)
        {
            if (!IsFlagSet(flagAt, true, out _)) throw new VerseStructureException($"sync branch at {_s[flagAt].StatementIndex} has no flag");
            var start = NextReal(flagAt);
            var done = -1;
            for (var k = start; k < _s.Count; k++)
            {
                if (IsIncrement(_s[k], counter))
                {
                    done = k;
                    break;
                }
            }

            if (done < 0) throw new VerseStructureException($"sync branch at {_s[start].StatementIndex} never finishes");
            branches.Add(Parse(start, done, new Ctx(null, -1, done)));

            var jumpAt = NextReal(done);
            if (jumpAt >= _s.Count || _s[jumpAt] is not EX_Jump onward || onward is EX_JumpIfNot)
                throw new VerseStructureException($"sync branch at {_s[start].StatementIndex} does not move on");

            var next = Target(onward);
            if (IsFlagSet(next, true, out _))
            {
                flagAt = next;
                continue;
            }

            if (_s[next] is EX_JumpIfNot { BooleanExpression: EX_CallMath { Parameters: [var count, _] } } && Var(count) == counter)
            {
                i = next + 1;
                return new VConcurrent("sync", branches);
            }

            throw new VerseStructureException($"sync at {_s[i].StatementIndex} has no join");
        }
    }

    private bool HasReal(int start, int end)
    {
        for (var k = start; k < end; k++)
            if (!_code.IsNoise(k) && VerseCode.Stm(_s[k]) == StmKind.None) return true;
        return false;
    }

    private VFor ParseFor(ForInfo info, Ctx ctx)
    {
        var node = new VFor
        {
            Kind = info.Kind, Source = info.Source, RangeFrom = info.RangeFrom, RangeTo = info.RangeTo,
            Key = info.Key, Item = info.Item, ResultTarget = info.ResultTarget
        };

        // filters run up to the commit that ends the per iteration failure context
        var fail = new FailRef();
        var k = info.BodyStart;
        var inner = new Ctx(fail, -1, info.Increment);
        node.Filters = Sequence(ref k, info.Increment, inner, true, out var terminator);
        if (terminator.Kind == StmKind.Rollback) throw new VerseStructureException("for filter ends in a rollback");

        if (terminator.Kind == StmKind.None)
        {
            // no commit: everything was body
            node.Body = node.Filters;
            node.Filters = [];
            node.FiltersEnd = info.BodyStart;
        }
        else
        {
            node.FiltersEnd = terminator.Index;
            k = terminator.Index + 1;
            while (k < info.Increment && (VerseCode.Stm(_s[k]) == StmKind.Commit || _code.IsNoise(k))) k++;

            var bodyEnd = info.Increment;
            while (bodyEnd > k && (VerseCode.Stm(_s[bodyEnd - 1]) == StmKind.Begin || _code.IsNoise(bodyEnd - 1))) bodyEnd--;
            node.Body = Parse(k, bodyEnd, new Ctx(null, -1, bodyEnd));
        }

        if (fail.Target >= 0 && (fail.Target <= info.Tail || fail.Target >= info.Exit))
            throw new VerseStructureException("for filter fails outside its continue landing");

        return node;
    }

    #endregion
}
