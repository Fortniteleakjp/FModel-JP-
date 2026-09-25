using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;

namespace FModel.Services.Verse;

/// <summary>
/// Re-synthesises the body of a Verse function from the Kismet bytecode it was cooked into.
///
/// The control flow is real: it comes from the jumps the compiler emitted, reduced back to Verse
/// blocks where that reduction is provable. Where it is not, the body says so on its own line
/// rather than inventing a shape. The expression level is NOT the author's source text - local
/// names are gone from the cook, so parameters read Arg0..ArgN and temporaries keep their cooked
/// names.
/// </summary>
public class VerseBodyWriter
{
    private const int MaxDepth = 12;

    private readonly VerseTypeResolver _resolver;

    public VerseBodyWriter(VerseTypeResolver resolver)
    {
        _resolver = resolver;
    }

    public string Write(UFunction function, string indent)
    {
        var bytecode = function.ScriptBytecode;
        if (bytecode is null || bytecode.Length == 0)
            return $"{indent}    # the cook holds no bytecode for this function\n";

        var statements = WithoutNonTransactionalPath(bytecode);
        var writer = new VerseExpressionWriter(_resolver, LocalNames(function), ReturnSlot(function));
        var builder = new StringBuilder();

        var body = new Emitter(writer, builder, statements);
        body.Block(0, statements.Count, indent + "    ", 0);

        return builder.Length == 0 ? $"{indent}    # no statement survived lowering\n" : builder.ToString();
    }

    /// <summary>
    /// a Verse function is cooked twice, once for the transactional runtime and once without it,
    /// behind a leading "if (!StmEnabled()) goto <non transactional copy>" - the two are the same
    /// source, so the second copy is dropped
    /// </summary>
    private static List<KismetExpression> WithoutNonTransactionalPath(KismetExpression[] bytecode)
    {
        var statements = bytecode.ToList();
        if (statements.FirstOrDefault() is not EX_JumpIfNot { BooleanExpression: EX_FinalFunction check } guard)
            return statements;
        if (VerseExpressionWriter.CalleeName(check) != "StmEnabled")
            return statements;

        var copyStart = statements.FindIndex(s => s.StatementIndex == guard.CodeOffset);
        if (copyStart <= 0) return statements;

        statements.RemoveRange(copyStart, statements.Count - copyStart);
        statements.RemoveAt(0);
        return statements;
    }

    /// <summary>
    /// the cook keeps no parameter names, so the packed argument tuple is mapped onto Arg0..ArgN
    /// the same way the declaration writes it, and the return slot onto the value it yields
    /// </summary>
    private static Dictionary<string, string> LocalNames(UFunction function)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var parameters = (function.ChildProperties ?? [])
            .OfType<FProperty>()
            .Where(p => p.PropertyFlags.HasFlag(EPropertyFlags.Parm) && !p.PropertyFlags.HasFlag(EPropertyFlags.ReturnParm))
            .ToArray();

        for (var i = 0; i < parameters.Length; i++)
        {
            var cooked = VerseMangling.UnmangleCasedName(parameters[i].Name.Text);
            names[cooked] = $"Arg{i}";
        }

        // a multi parameter function packs them into one tuple, reached as .Elem0 .. .ElemN
        // the receiver is renamed first, so the element map is keyed on the renamed form too
        if (parameters.Length == 1)
        {
            var cooked = VerseMangling.UnmangleCasedName(parameters[0].Name.Text);
            for (var element = 0; element < 16; element++)
            {
                names[$"{cooked}.Elem{element}"] = $"Arg{element}";
                names[$"Arg0.Elem{element}"] = $"Arg{element}";
            }
        }

        return names;
    }

    /// <summary>the cooked slot a function writes its result into, if it has one</summary>
    private static string? ReturnSlot(UFunction function) => (function.ChildProperties ?? [])
        .OfType<FProperty>()
        .FirstOrDefault(p => p.PropertyFlags.HasFlag(EPropertyFlags.ReturnParm))
        ?.Name.Text is { } cooked
        ? VerseMangling.UnmangleCasedName(cooked)
        : null;

    /// <summary>
    /// walks a run of statements and reduces the jumps inside it back to Verse blocks
    /// </summary>
    private sealed class Emitter
    {
        private readonly VerseExpressionWriter _writer;
        private readonly StringBuilder _builder;
        private readonly List<KismetExpression> _statements;

        public Emitter(VerseExpressionWriter writer, StringBuilder builder, List<KismetExpression> statements)
        {
            _writer = writer;
            _builder = builder;
            _statements = statements;
        }

        private int IndexOfOffset(uint offset)
        {
            for (var i = 0; i < _statements.Count; i++)
                if (_statements[i].StatementIndex == offset) return i;
            return -1;
        }

        private void Line(string indent, string text)
        {
            if (!string.IsNullOrWhiteSpace(text)) _builder.Append(indent).Append(text).Append('\n');
        }

        public void Block(int start, int end, string indent, int depth)
        {
            var i = start;
            while (i < end)
            {
                var statement = _statements[i];
                if (VerseExpressionWriter.IsHousekeeping(statement)) { i++; continue; }

                switch (statement)
                {
                    case EX_JumpIfNot conditional when depth < MaxDepth:
                    {
                        i = Conditional(conditional, i, end, indent, depth);
                        continue;
                    }
                    case EX_Jump jump:
                    {
                        var target = IndexOfOffset(jump.CodeOffset);
                        // a jump forward out of this run is the tail of a block we already closed
                        if (target >= end || target < 0) { i++; continue; }
                        if (target <= i)
                        {
                            Line(indent, $"# <unstructured: control re-enters an earlier block at offset {jump.CodeOffset}, not reducible to a Verse construct>");
                            i++;
                            continue;
                        }

                        i = target;
                        continue;
                    }
                    case EX_Return ret:
                    {
                        var value = _writer.Write(ret.ReturnExpression);
                        Line(indent, value);
                        i++;
                        continue;
                    }
                    case EX_AutoRtfmTransact transact:
                    {
                        // the transactional wrapper of a block, its body is the block itself
                        foreach (var inner in transact.Parameters)
                            Line(indent, _writer.Write(inner));
                        i++;
                        continue;
                    }
                    default:
                    {
                        Line(indent, _writer.Write(statement));
                        i++;
                        continue;
                    }
                }
            }
        }

        /// <summary>
        /// index of the conditional that is the only real statement of a run, -1 when there is more to it
        /// </summary>
        private int OnlyConditional(int start, int end)
        {
            var found = -1;
            for (var i = start; i < end; i++)
            {
                if (VerseExpressionWriter.IsHousekeeping(_statements[i])) continue;
                if (_statements[i] is not EX_JumpIfNot conditional) return -1;
                found = i;
                break;
            }

            if (found < 0) return -1;

            var conditionalEnd = ConditionalEnd((EX_JumpIfNot) _statements[found], found, end);
            if (conditionalEnd < 0) return -1;

            // A chained `else if` is valid only when the nested conditional consumes the rest of
            // the else block. Housekeeping instructions may trail it because Block() drops them.
            for (var i = conditionalEnd; i < end; i++)
                if (!VerseExpressionWriter.IsHousekeeping(_statements[i])) return -1;

            return found;
        }

        /// <summary>
        /// index immediately after the block controlled by a conditional, or -1 when its jump
        /// cannot be reduced inside the supplied run
        /// </summary>
        private int ConditionalEnd(EX_JumpIfNot conditional, int index, int end)
        {
            var elseStart = IndexOfOffset(conditional.CodeOffset);
            if (elseStart < 0 || elseStart > end || elseStart <= index) return -1;

            // With an else branch the then path ends in a forward jump over that branch.
            if (elseStart - 1 > index && _statements[elseStart - 1] is EX_Jump tail and not EX_JumpIfNot)
            {
                var afterElse = IndexOfOffset(tail.CodeOffset);
                if (afterElse > elseStart && afterElse <= end) return afterElse;
            }

            return elseStart;
        }

        /// <summary>
        /// reduces "jump over the then-part when the condition fails" back into if / else if / else
        /// </summary>
        private int Conditional(EX_JumpIfNot conditional, int index, int end, string indent, int depth, bool chained = false)
        {
            var condition = _writer.Write(conditional.BooleanExpression);
            if (string.IsNullOrEmpty(condition)) condition = "true";

            var elseStart = IndexOfOffset(conditional.CodeOffset);
            if (elseStart < 0 || elseStart > end || elseStart <= index)
            {
                Line(indent, $"# <unstructured: conditional jumps to offset {conditional.CodeOffset}, outside this block>");
                Line(indent, condition);
                return index + 1;
            }

            // the then-part ends with a jump past the else-part when there is one
            var thenEnd = elseStart;
            var elseEnd = -1;
            if (thenEnd - 1 > index && _statements[thenEnd - 1] is EX_Jump tail and not EX_JumpIfNot)
            {
                var afterElse = IndexOfOffset(tail.CodeOffset);
                if (afterElse > elseStart && afterElse <= end)
                {
                    thenEnd--;
                    elseEnd = afterElse;
                }
            }

            Line(indent, $"{(chained ? "else if" : "if")} ({condition}):");
            Block(index + 1, thenEnd, indent + "    ", depth + 1);
            if (thenEnd == index + 1) Line(indent + "    ", "# <no statements on this path>");

            if (elseEnd < 0) return elseStart;

            // an else whose whole body is another conditional reads as "else if"
            var onlyStatement = OnlyConditional(elseStart, elseEnd);
            if (onlyStatement >= 0)
            {
                var chainedConditional = (EX_JumpIfNot) _statements[onlyStatement];
                return Conditional(chainedConditional, onlyStatement, elseEnd, indent, depth, true);
            }

            Line(indent, "else:");
            Block(elseStart, elseEnd, indent + "    ", depth + 1);
            if (elseEnd == elseStart) Line(indent + "    ", "# <no statements on this path>");

            return elseEnd;
        }
    }
}
