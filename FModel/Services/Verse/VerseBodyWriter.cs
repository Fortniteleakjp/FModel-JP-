using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.UObject;
using Serilog;

namespace FModel.Services.Verse;

/// <summary>
/// Re-synthesises the body of a Verse function from the Kismet bytecode it was cooked into.
///
/// The transactional copy of the function is normalised, its failure contexts and loops are rebuilt
/// from the STM brackets and jumps the compiler emitted, and the compiler's temporaries are folded
/// back into the expressions they were split out of. A suspends function is only a stub that makes
/// a task, so its body is read from that task's Update instead. When a function steps outside the
/// shapes this understands, it falls back to the plain statement listing.
/// </summary>
public class VerseBodyWriter
{
    private readonly VerseTypeResolver _resolver;
    private readonly VerseLegacyBodyWriter _legacy;

    /// <summary>list the cooked statements as they are instead of rebuilding the source</summary>
    public bool Listing { get; init; }

    public VerseBodyWriter(VerseTypeResolver resolver)
    {
        _resolver = resolver;
        _legacy = new VerseLegacyBodyWriter(resolver);
    }

    /// <summary>
    /// the body of a function, indented one level deeper than indent; names and defaults it recovers
    /// for the parameters are written back into them
    /// </summary>
    public string Write(UFunction function, string indent, IReadOnlyList<VerseParameter> parameters, string owner, bool decides)
    {
        var bytecode = function.ScriptBytecode;
        if (bytecode is null || bytecode.Length == 0)
            return $"{indent}    # the cook holds no bytecode for this function\n";

        if (Listing)
        {
            // what the compiler generated: the function itself, and for a suspends function the
            // Update of the task it makes, where its body actually runs
            var listing = _legacy.Write(function, indent);
            if (TaskUpdate(bytecode) is { } lowered && lowered.Update.ScriptBytecode is { Length: > 0 })
                listing += $"{indent}    # ---- {lowered.Class.Name}.Update, the task this suspends function runs in\n" +
                           _legacy.Write(lowered.Update, indent);
            return listing;
        }

        var source = function;
        UStruct? frame = null;
        var taskMode = false;
        if (TaskUpdate(bytecode) is { } task)
        {
            source = task.Update;
            frame = task.Class;
            taskMode = true;
        }

        if (source.ScriptBytecode is not { Length: > 0 } body)
            return $"{indent}    # the cook holds no bytecode for this function\n";

        try
        {
            var code = new VerseCode(VerseLegacyBodyWriter.WithoutNonTransactionalPath(body), taskMode);
            var structure = new VerseStructurer(code);
            var nodes = structure.Parse();
            var usage = new VerseUsage(code, structure.Consumed);
            var printer = new VersePrinter(code, usage, structure, _resolver, owner, parameters,
                name => LocalProperty(source, frame, name), decides);
            var text = printer.Print(nodes, indent + "    ");
            return text.Length == 0 ? $"{indent}    block {{}}\n" : text;
        }
        catch (VerseStructureException e)
        {
            Log.Debug("Verse body of {Function} kept as a listing: {Reason}", function.Name, e.Message);
            return $"{indent}    # control flow could not be fully rebuilt ({e.Message}), statements are listed as cooked\n" +
                   _legacy.Write(source, indent);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not rebuild the Verse body of {Function}", function.Name);
            return $"{indent}    # could not rebuild this body ({e.GetType().Name}), statements are listed as cooked\n" +
                   _legacy.Write(source, indent);
        }
    }

    /// <summary>
    /// the values the class default object initialises its fields with, rebuilt from $InitCDO;
    /// keyed by the cooked field name
    /// </summary>
    public IReadOnlyDictionary<string, string> FieldInitialisers(UClass @class)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        UFunction? init = null;
        foreach (var child in @class.Children ?? [])
        {
            if (child.TryLoad(out var export) && export is UFunction { Name: "$InitCDO" } function)
            {
                init = function;
                break;
            }
        }

        if (init?.ScriptBytecode is not { Length: > 0 } body) return result;

        try
        {
            var code = new VerseCode(VerseLegacyBodyWriter.WithoutNonTransactionalPath(body), false);
            var structure = new VerseStructurer(code);
            var nodes = structure.Parse();
            var usage = new VerseUsage(code, structure.Consumed);
            var printer = new VersePrinter(code, usage, structure, _resolver, @class.Name, [],
                name => LocalProperty(init, null, name), false) { FieldInitialisers = result };
            printer.Print(nodes, string.Empty);
        }
        catch (Exception e)
        {
            Log.Debug(e, "Field initialisers of {Class} could not be rebuilt", @class.Name);
        }

        return result;
    }

    private static FProperty? LocalProperty(UStruct function, UStruct? frame, string cooked)
    {
        foreach (var owner in new[] { function, frame })
        {
            if (owner?.ChildProperties is null) continue;
            foreach (var field in owner.ChildProperties)
                if (field is FProperty property && property.Name.Text == cooked) return property;
        }

        return null;
    }

    /// <summary>the task class a suspends function is lowered into, when its bytecode is loaded</summary>
    public static UClass? SuspendsTask(UFunction function) =>
        function.ScriptBytecode is { Length: > 0 } bytecode ? TaskUpdate(bytecode)?.Class : null;

    /// <summary>
    /// a suspends function is cooked as <c>RetVal = TaskMake(task_Owner$Name, ...)</c>; its real body is the
    /// task class's Update, a resumable state machine
    /// </summary>
    private static (UFunction Update, UClass Class)? TaskUpdate(KismetExpression[] bytecode)
    {
        foreach (var statement in bytecode)
        {
            var value = statement switch
            {
                EX_Let let => let.Assignment,
                EX_LetBase let => let.Assignment,
                _ => null
            };

            if (value is not EX_CallMath { Parameters: [EX_ObjectConst { Value: { } taskClass }, ..] } make ||
                VerseCode.CalleeName(make) != "TaskMake")
                continue;

            if (taskClass.ResolvedObject?.Object?.Value is not UClass @class) return null;
            foreach (var child in @class.Children ?? [])
            {
                if (child.TryLoad(out var export) && export is UFunction { Name: "Update" } update)
                    return (update, @class);
            }

            return null;
        }

        return null;
    }
}
