#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;
using Serilog;

namespace FModel.Services.RowStructInference;

/// <summary>
/// UDataTable that can still read its rows when the row struct is missing from the mappings: the mappings of a game
/// are dumped from what was loaded at the time, so the structs of a game feature plugin that wasn't loaded (e.g.
/// Fortnite's DelilahTreasurePrizeRow in 43.00) are absent and CUE4Parse gives up with "Missing prop mappings".
/// The layout is then guessed from the rows themselves (<see cref="UnversionedLayoutSolver"/>) and added to the mappings.
/// </summary>
public class InferringDataTable : UDataTable
{
    /// <summary>Must run before any package is loaded.</summary>
    public static void Register() => ObjectTypeRegistry.RegisterClass("DataTable", typeof(InferringDataTable));

    /// <summary>Set when the rows were read with a guessed row struct</summary>
    [JsonIgnore] public bool RowStructGuessed { get; private set; }

    public override void Deserialize(FAssetArchive Ar, long validPos)
    {
        if (Ar.HasUnversionedProperties && string.IsNullOrEmpty(RowStructName) && Ar.Game != EGame.GAME_HonorofKingsWorld)
            MissingRowStructInference.TryPrepare(this, Ar, validPos);

        base.Deserialize(Ar, validPos);
        RowStructGuessed = RowStructName is not null && MissingRowStructInference.IsGuessed(Ar.Owner?.Mappings, RowStructName);
    }

    protected override void WriteJson(JsonWriter writer, JsonSerializer serializer)
    {
        base.WriteJson(writer, serializer);
        if (!RowStructGuessed) return;

        writer.WritePropertyName("RowStructGuessed");
        writer.WriteValue($"{RowStructName} is missing from the mappings, the types and names of its properties were guessed from the rows");
    }
}

internal static class MissingRowStructInference
{
    private sealed class State
    {
        public readonly ConcurrentDictionary<string, bool> Results = new(StringComparer.OrdinalIgnoreCase); // true: guessed, false: failed
    }

    private static readonly ConditionalWeakTable<TypeMappings, State> _states = new();

    public static bool IsGuessed(TypeMappings? mappings, string structName) =>
        mappings is not null && _states.TryGetValue(mappings, out var state) &&
        state.Results.TryGetValue(structName, out var guessed) && guessed;

    /// <summary>
    /// Reads the table's properties ahead (on a throwaway object) to find the row struct, and when the mappings don't have
    /// it, solves the layout of the rows and registers it. The archive is left where it was.
    /// </summary>
    public static void TryPrepare(UObject table, FAssetArchive Ar, long validPos)
    {
        if (Ar.Owner?.Mappings is not { } mappings) return;

        var start = Ar.Position;
        string? structName = null;
        var missing = false; // until then, a table that can't be read ahead is left to UDataTable as it is
        try
        {
            if (!TryReadProperties(table, Ar, validPos, out var rowStruct)) return;
            structName = rowStruct.Name;
            if (string.IsNullOrEmpty(structName) || mappings.Types.ContainsKey(structName)) return;

            var state = _states.GetValue(mappings, _ => new State());
            if (state.Results.ContainsKey(structName)) return;
            // a struct asset (UserDefinedStruct) carries its own layout, only native structs need the mappings
            if (rowStruct.TryLoad<UStruct>(out var loaded) && loaded is not UScriptClass) return;
            missing = true;
            if (!TryReadRows(Ar, validPos, out var numRows, out var rows, out var options)) return;

            var stopwatch = Stopwatch.StartNew();
            var layout = SolveOnLargeStack(() => UnversionedLayoutSolver.SolveWithFallback(rows, numRows, options));
            if (layout is null)
            {
                state.Results[structName] = false;
                Log.Warning("Row struct {Struct} of {Table} is missing from the mappings and no layout fits its {Rows} rows",
                    structName, table.Name, numRows);
                return;
            }

            var structs = RowStructBuilder.Build(layout, mappings, structName);
            lock (mappings.Types)
            {
                foreach (var st in structs)
                    mappings.Types.TryAdd(st.Name, st);
            }

            state.Results[structName] = true;
            Log.Warning("Row struct {Struct} of {Table} is missing from the mappings, guessed {Count} properties from its {Rows} rows in {Elapsed} ms{Unproven}: {Properties}",
                structName, table.Name, structs[0].Properties.Count, numRows, stopwatch.ElapsedMilliseconds,
                layout.Unproven ? " (no other layout found in time)" : "",
                string.Join(", ", structs[0].Properties.OrderBy(p => p.Key).Select(p => $"{p.Value.Name}:{Describe(p.Value.MappingType)}")));
        }
        catch (InferenceAbortedException e)
        {
            Remember(mappings, structName, false);
            Log.Warning("Row struct {Struct} of {Table} is missing from the mappings, gave up guessing it: {Reason}", structName, table.Name, e.Message);
        }
        catch (Exception e) when (missing)
        {
            Remember(mappings, structName, false);
            Log.Warning(e, "Row struct {Struct} of {Table} is missing from the mappings, could not guess it", structName, table.Name);
        }
        catch (Exception e)
        {
            Log.Debug(e, "Could not read the properties of {Table} ahead", table.Name);
        }
        finally
        {
            Ar.Position = start;
        }
    }

    internal static UnversionedLayoutSolver.SolvedLayout? SolveOnLargeStack(Func<UnversionedLayoutSolver.SolvedLayout?> solve)
    {
        UnversionedLayoutSolver.SolvedLayout? layout = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                layout = solve();
            }
            catch (Exception e)
            {
                error = e;
            }
        }, UnversionedLayoutSolver.RecommendedStackSize)
        {
            IsBackground = true,
            Name = "Row struct inference"
        };
        thread.Start();
        thread.Join();

        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        return layout;
    }

    /// <summary>Reads the properties of the table on a throwaway object, leaving the archive on the rows</summary>
    internal static bool TryReadProperties(UObject table, FAssetArchive Ar, long validPos, [NotNullWhen(true)] out FPackageIndex? rowStruct)
    {
        var probe = new UObject
        {
            Name = table.Name,
            Class = table.Class,
            Outer = table.Outer,
            Super = table.Super,
            Template = table.Template,
            Flags = table.Flags
        };
        probe.Deserialize(Ar, validPos);

        rowStruct = probe.GetOrDefault<FPackageIndex?>("RowStruct");
        return rowStruct is { IsNull: false };
    }

    /// <summary>Reads the row count and the bytes of the rows, from where <see cref="TryReadProperties"/> left the archive</summary>
    internal static bool TryReadRows(FAssetArchive Ar, long validPos, out int numRows, out byte[] rows, out LayoutSolverOptions options)
    {
        numRows = 0;
        rows = [];
        options = null!;
        if (Ar.Owner is not { } owner || validPos - Ar.Position < 4) return false;
        numRows = Ar.Read<int>();
        if (numRows <= 0 || validPos - Ar.Position > int.MaxValue) return false;
        rows = Ar.ReadBytes((int) (validPos - Ar.Position));

        options = new LayoutSolverOptions
        {
            Names = owner.NameMap.Select(entry => entry.Name ?? string.Empty).ToArray(),
            SoftObjectPathHasTwoNames = Ar.Ver >= EUnrealEngineObjectUE5Version.FSOFTOBJECTPATH_REMOVE_ASSET_PATH_FNAMES,
            SoftObjectPathIsIndex = !Ar.IsFilterEditorOnly && Ar.Ver >= EUnrealEngineObjectUE5Version.ADD_SOFTOBJECTPATH_LIST &&
                                    owner is Package { SoftObjectPaths.Length: > 0 },
            LargeWorldCoordinates = Ar.Ver >= EUnrealEngineObjectUE5Version.LARGE_WORLD_COORDINATES,
            ImportCount = owner.ImportMapLength,
            ExportCount = owner.ExportMapLength
        };
        return true;
    }

    private static void Remember(TypeMappings mappings, string? structName, bool guessed)
    {
        if (structName is not null)
            _states.GetValue(mappings, _ => new State()).Results[structName] = guessed;
    }

    private static string Describe(PropertyType type) => type.Type switch
    {
        "StructProperty" => $"Struct<{type.StructType}>",
        "ArrayProperty" => $"Array<{(type.InnerType is null ? "?" : Describe(type.InnerType))}>",
        _ => type.Type.Replace("Property", "")
    };
}
