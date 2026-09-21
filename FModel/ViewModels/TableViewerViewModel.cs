using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.Engine.Curves;
using CUE4Parse.UE4.Objects.UObject;
using Newtonsoft.Json;

namespace FModel.ViewModels;

public enum ETableKind
{
    Data,
    Curve
}

/// <summary>
/// One key of a curve table row.
/// </summary>
public readonly record struct TableCurveKey(float Time, float Value);

public class TableCurve
{
    public string Name { get; init; }
    public string InterpMode { get; init; }
    public IReadOnlyList<TableCurveKey> Keys { get; init; } = [];

    public float MinTime { get; init; }
    public float MaxTime { get; init; }
    public float MinValue { get; init; }
    public float MaxValue { get; init; }
}

/// <summary>
/// A row of the grid. Cells are plain strings indexed the same way as <see cref="TableDocument.Columns"/>,
/// which keeps a hundred thousand rows cheap to hold and free to filter (the rows are only referenced, never copied).
/// </summary>
public sealed class TableRow(string[] cells)
{
    public string[] Cells { get; } = cells;
    public string Name => Cells.Length > 0 ? Cells[0] : string.Empty;

    public bool Contains(string text)
    {
        foreach (var cell in Cells)
        {
            if (cell != null && cell.Contains(text, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

/// <summary>
/// A single data or curve table ready to be displayed as a grid.
/// </summary>
public class TableDocument
{
    public string Name { get; init; }
    public string TypeName { get; init; }
    public string RowStructName { get; init; }
    public ETableKind Kind { get; init; }

    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<TableRow> Rows { get; init; } = [];

    /// <summary>Only filled for curve tables, keyed by row name.</summary>
    public IReadOnlyDictionary<string, TableCurve> Curves { get; init; } = new Dictionary<string, TableCurve>();

    public int RowCount => Rows.Count;
    public int ColumnCount => Columns.Count;
    public override string ToString() => $"{Name} ({TypeName})";
}

/// <summary>
/// Turns UDataTable and UCurveTable exports into grids, the way the editor shows them.
/// Everything is done in one pass over the rows so that very large tables stay openable.
/// </summary>
public static class TableDocumentBuilder
{
    public const string ROW_NAME_COLUMN = "Row Name";

    /// <summary>
    /// Cells are cut off at this length. Nested structs serialize to json that can run into
    /// kilobytes per cell, which no grid can show and which would blow up memory on a big table.
    /// </summary>
    private const int _MAX_CELL_LENGTH = 200;

    public static List<TableDocument> Build(IEnumerable<UObject> exports, Action<string> progress = null, CancellationToken cancellationToken = default)
    {
        var documents = new List<TableDocument>();
        foreach (var export in exports)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (export)
            {
                case UCurveTable curveTable:
                    documents.Add(BuildCurveTable(curveTable, progress, cancellationToken));
                    break;
                case UDataTable dataTable:
                    documents.Add(BuildDataTable(dataTable, progress, cancellationToken));
                    break;
            }
        }

        return documents;
    }

    public static bool IsTable(UObject export) => export is UDataTable or UCurveTable;

    private static TableDocument BuildDataTable(UDataTable table, Action<string> progress, CancellationToken cancellationToken)
    {
        var rowMap = table.RowMap ?? [];

        // first pass: the column layout, every row may carry a different set of properties
        var columnIndex = new Dictionary<string, int>(StringComparer.Ordinal) { [ROW_NAME_COLUMN] = 0 };
        var columns = new List<string> { ROW_NAME_COLUMN };
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (_, row) in rowMap)
        {
            cancellationToken.ThrowIfCancellationRequested();

            seen.Clear();
            foreach (var property in row.Properties)
            {
                var column = ColumnName(property, seen);
                if (columnIndex.ContainsKey(column)) continue;

                columnIndex[column] = columns.Count;
                columns.Add(column);
            }
        }

        // second pass: the values
        var rows = new List<TableRow>(rowMap.Count);
        var done = 0;
        foreach (var (name, row) in rowMap)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cells = new string[columns.Count];
            cells[0] = name.Text;

            seen.Clear();
            foreach (var property in row.Properties)
            {
                var column = ColumnName(property, seen);
                if (columnIndex.TryGetValue(column, out var index))
                    cells[index] = Format(property.Tag);
            }

            rows.Add(new TableRow(cells));

            if (++done % 5000 == 0) progress?.Invoke($"{done} / {rowMap.Count} rows");
        }

        return new TableDocument
        {
            Name = table.Name,
            TypeName = table.ExportType,
            RowStructName = table.RowStructName,
            Kind = ETableKind.Data,
            Columns = columns,
            Rows = rows
        };
    }

    private static TableDocument BuildCurveTable(UCurveTable table, Action<string> progress, CancellationToken cancellationToken)
    {
        string[] columns = [ROW_NAME_COLUMN, "Keys", "Time", "Value", "Interpolation"];

        var rowMap = table.RowMap ?? [];
        var rows = new List<TableRow>(rowMap.Count);
        var curves = new Dictionary<string, TableCurve>(rowMap.Count);
        var done = 0;

        foreach (var (name, row) in rowMap)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var curve = ReadCurve(name.Text, row);
            curves[curve.Name] = curve;

            rows.Add(new TableRow([
                curve.Name,
                curve.Keys.Count.ToString(CultureInfo.InvariantCulture),
                curve.Keys.Count == 0 ? string.Empty : $"{curve.MinTime:0.###} → {curve.MaxTime:0.###}",
                curve.Keys.Count == 0 ? string.Empty : $"{curve.MinValue:0.###} → {curve.MaxValue:0.###}",
                curve.InterpMode
            ]));

            if (++done % 5000 == 0) progress?.Invoke($"{done} / {rowMap.Count} curves");
        }

        return new TableDocument
        {
            Name = table.Name,
            TypeName = table.ExportType,
            RowStructName = table.CurveTableMode.ToString(),
            Kind = ETableKind.Curve,
            Columns = columns,
            Rows = rows,
            Curves = curves
        };
    }

    private static TableCurve ReadCurve(string name, FStructFallback row)
    {
        var keys = new List<TableCurveKey>();
        var interpMode = string.Empty;

        // rich curves carry per key interpolation, simple curves carry one mode for the whole curve
        if (row.TryGetValue(out FRichCurveKey[] richKeys, "Keys") && richKeys.Length > 0)
        {
            keys.AddRange(richKeys.Select(k => new TableCurveKey(k.Time, k.Value)));
            interpMode = string.Join(", ", richKeys.Select(k => k.InterpMode.ToString()).Distinct());
        }
        else if (row.TryGetValue(out FSimpleCurveKey[] simpleKeys, "Keys"))
        {
            keys.AddRange(simpleKeys.Select(k => new TableCurveKey(k.Time, k.Value)));
            interpMode = row.GetOrDefault("InterpMode", ERichCurveInterpMode.RCIM_Linear).ToString();
        }

        float minTime = 0, maxTime = 0, minValue = 0, maxValue = 0;
        if (keys.Count > 0)
        {
            minTime = maxTime = keys[0].Time;
            minValue = maxValue = keys[0].Value;
            foreach (var key in keys)
            {
                if (key.Time < minTime) minTime = key.Time;
                if (key.Time > maxTime) maxTime = key.Time;
                if (key.Value < minValue) minValue = key.Value;
                if (key.Value > maxValue) maxValue = key.Value;
            }
        }

        return new TableCurve
        {
            Name = name, Keys = keys, InterpMode = interpMode,
            MinTime = minTime, MaxTime = maxTime, MinValue = minValue, MaxValue = maxValue
        };
    }

    /// <summary>
    /// Static arrays repeat the same property name, they get an index suffix so every value keeps a column.
    /// </summary>
    private static string ColumnName(FPropertyTag property, Dictionary<string, int> seen)
    {
        var name = property.Name.Text;
        if (!seen.TryGetValue(name, out var count))
        {
            seen[name] = 1;
            return name;
        }

        seen[name] = count + 1;
        return $"{name}[{count}]";
    }

    /// <summary>
    /// Formats a property the way a spreadsheet cell should read: plain for simple values, compact json otherwise.
    /// </summary>
    public static string Format(FPropertyTagType tag) => Truncate(FormatFull(tag));

    private static string FormatFull(FPropertyTagType tag)
    {
        if (tag == null) return string.Empty;

        try
        {
            switch (tag.GenericValue)
            {
                case null: return string.Empty;
                case string s: return s;
                case bool b: return b ? "true" : "false";
                case FName name: return name.Text;
                case FPackageIndex index: return index.Name;
                case ResolvedObject resolved: return resolved.Name.Text;
                case IFormattable formattable and (sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal):
                    return formattable.ToString(null, CultureInfo.InvariantCulture);
                default:
                    return JsonConvert.SerializeObject(tag, Formatting.None);
            }
        }
        catch (Exception)
        {
            return tag.GenericValue?.ToString() ?? string.Empty;
        }
    }

    private static string Truncate(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        if (value.Length > _MAX_CELL_LENGTH)
            value = string.Concat(value.AsSpan(0, _MAX_CELL_LENGTH), "…");

        return value.ReplaceLineEndings(" ");
    }

    /// <summary>
    /// Writes the rows as csv, quoting the way excel expects it.
    /// Streams straight into the writer so exporting a huge table never builds the file in memory.
    /// </summary>
    public static void WriteCsv(TextWriter writer, IReadOnlyList<string> columns, IEnumerable<TableRow> rows, CancellationToken cancellationToken = default)
    {
        writer.WriteLine(string.Join(",", columns.Select(Escape)));

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var i = 0; i < columns.Count; i++)
            {
                if (i > 0) writer.Write(',');
                writer.Write(Escape(i < row.Cells.Length ? row.Cells[i] : string.Empty));
            }

            writer.WriteLine();
        }
    }

    private static string Escape(string value)
    {
        value ??= string.Empty;
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
            return value;

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
