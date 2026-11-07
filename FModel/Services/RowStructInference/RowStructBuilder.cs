#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CUE4Parse.MappingsProvider;

namespace FModel.Services.RowStructInference;

/// <summary>
/// Turns a <see cref="UnversionedLayoutSolver.SolvedLayout"/> into mapping structs CUE4Parse can read the rows with.
/// Nested structs are matched against a few well known engine structs (by the types of their properties), the others
/// get a made up struct. Text properties are named after their localization key when it is "{RowName}_{Property}"
/// (DataTable texts usually are), the rest after their type and schema index.
/// </summary>
internal static partial class RowStructBuilder
{
    private static readonly string[] KnownStructs =
    [
        "GameplayTag", "PrimaryAssetId", "DataTableRowHandle", "CurveTableRowHandle", "ScalableFloat"
    ];

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierRegex();

    public static List<Struct> Build(UnversionedLayoutSolver.SolvedLayout layout, TypeMappings mappings, string rowStructName)
    {
        var created = new List<Struct>();
        var context = new BuildContext(layout, mappings, created);
        created.Insert(0, context.BuildStruct(0, rowStructName, true));
        return created;
    }

    private sealed class BuildContext(UnversionedLayoutSolver.SolvedLayout layout, TypeMappings mappings, List<Struct> created)
    {
        private readonly HashSet<string> _usedStructNames = new(StringComparer.OrdinalIgnoreCase);

        public Struct BuildStruct(int path, string name, bool isRow)
        {
            _usedStructNames.Add(name);
            var properties = new Dictionary<int, PropertyInfo>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var maxIndex = -1;

            foreach (var index in layout.ObservedIndices(path).Order())
            {
                maxIndex = Math.Max(maxIndex, index);
                var child = layout.Child(path, index);
                var shape = child is { } c ? layout.Shape(c) : null;
                if (child is null || shape is null)
                {
                    // only ever zero: nothing tells its type
                    properties[index] = new PropertyInfo(0, Unique(usedNames, $"Unknown_{index}"), new PropertyType("IntProperty"), 1);
                    continue;
                }

                var type = ResolveType(child.Value, shape.Value, $"{name}_{index}");
                var propertyName = isRow && shape == ValueShape.Text ? TextKeyName(child.Value) : null;
                propertyName = propertyName is not null && usedNames.Add(propertyName)
                    ? propertyName
                    : Unique(usedNames, $"{Label(type)}_{index}");
                properties[index] = new PropertyInfo(0, propertyName, type, 1);
            }

            var result = new Struct(mappings, name, null, properties, maxIndex + 1);
            if (!isRow) created.Add(result);
            return result;
        }

        private static string Unique(HashSet<string> used, string name)
        {
            var candidate = name;
            for (var i = 2; !used.Add(candidate); i++)
                candidate = $"{name}_{i}";
            return candidate;
        }

        private static string Label(PropertyType type) => type.Type switch
        {
            "StructProperty" => type.StructType ?? "Struct",
            "ArrayProperty" => "Array",
            "NameProperty" => "Name",
            "StrProperty" => "String",
            "TextProperty" => "Text",
            "SoftObjectProperty" => "SoftObject",
            _ => type.Type.EndsWith("Property") ? type.Type[..^"Property".Length] : type.Type
        };

        private string? TextKeyName(int path)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (pos, row) in layout.Samples(path))
            {
                var key = layout.TextKey(pos);
                var prefix = layout.RowName(row) + "_";
                if (key is null || !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var name = key[prefix.Length..];
                if (!IdentifierRegex().IsMatch(name)) continue;
                counts[name] = counts.GetValueOrDefault(name) + 1;
            }

            return counts.Count == 0 ? null : counts.MaxBy(kv => kv.Value).Key;
        }

        private PropertyType ResolveType(int path, ValueShape shape, string syntheticName)
        {
            switch (shape)
            {
                case ValueShape.Name: return new PropertyType("NameProperty");
                case ValueShape.String: return new PropertyType("StrProperty");
                case ValueShape.Text: return new PropertyType("TextProperty");
                case ValueShape.SoftObjectPath: return new PropertyType("SoftObjectProperty");
                case ValueShape.InstancedStruct: return new PropertyType("StructProperty", "InstancedStruct");
                case ValueShape.Fixed1:
                    return layout.Samples(path).All(s => layout.Data[s.Pos] <= 1)
                        ? new PropertyType("BoolProperty")
                        : new PropertyType("ByteProperty");
                case ValueShape.Fixed4:
                {
                    var bits = layout.Samples(path).Select(s => layout.U32(s.Pos)).ToList();
                    if (bits.All(b => Math.Abs((long) unchecked((int) b)) <= 1 << 24)) return new PropertyType("IntProperty");
                    return bits.All(b => PlausibleFloat(BitConverter.UInt32BitsToSingle(b)))
                        ? new PropertyType("FloatProperty")
                        : new PropertyType("IntProperty");
                }
                case ValueShape.Fixed8:
                {
                    var bits = layout.Samples(path).Select(s => layout.U64(s.Pos)).ToList();
                    if (bits.All(b => unchecked((long) b) is > -(1L << 40) and < 1L << 40)) return new PropertyType("Int64Property");
                    return bits.All(b => PlausibleDouble(BitConverter.UInt64BitsToDouble(b)))
                        ? new PropertyType("DoubleProperty")
                        : new PropertyType("Int64Property");
                }
                case ValueShape.Fixed12:
                case ValueShape.Fixed24:
                    return new PropertyType("StructProperty", "Vector");
                case ValueShape.Fixed16:
                {
                    var samples = layout.Samples(path);
                    if (layout.LargeWorldCoordinates && samples.All(s =>
                            PlausibleDouble(BitConverter.UInt64BitsToDouble(layout.U64(s.Pos))) &&
                            PlausibleDouble(BitConverter.UInt64BitsToDouble(layout.U64(s.Pos + 8)))))
                        return new PropertyType("StructProperty", "Vector2D");
                    if (samples.All(s => Enumerable.Range(0, 4).All(i => PlausibleFloat(BitConverter.UInt32BitsToSingle(layout.U32(s.Pos + i * 4))))))
                        return new PropertyType("StructProperty", "LinearColor");
                    return new PropertyType("StructProperty", "Guid");
                }
                case ValueShape.Struct:
                {
                    var known = KnownStructs.FirstOrDefault(name => mappings.Types.TryGetValue(name, out var st) && Matches(path, st, name));
                    if (known is not null) return new PropertyType("StructProperty", known);

                    var name = syntheticName;
                    for (var i = 2; _usedStructNames.Contains(name) || mappings.Types.ContainsKey(name); i++)
                        name = $"{syntheticName}_{i}";
                    BuildStruct(path, name, false);
                    return new PropertyType("StructProperty", name);
                }
                case ValueShape.Array:
                {
                    var element = layout.Child(path, -1);
                    var elementShape = element is { } e ? layout.Shape(e) : null;
                    // FGameplayTagContainer is serialized natively as a count and the names of the tags
                    if (elementShape == ValueShape.Name && mappings.Types.ContainsKey("GameplayTagContainer") &&
                        layout.Samples(element!.Value).All(s => layout.NameText(s.Pos).Contains('.')))
                        return new PropertyType("StructProperty", "GameplayTagContainer");
                    var inner = element is null || elementShape is null
                        ? new PropertyType("IntProperty")
                        : ResolveType(element.Value, elementShape.Value, syntheticName + "_Element");
                    return new PropertyType("ArrayProperty", innerType: inner);
                }
                default:
                    return new PropertyType("IntProperty");
            }
        }

        private static bool PlausibleFloat(float f) =>
            f == 0 || (float.IsFinite(f) && Math.Abs(f) is >= 1e-5f and <= 1e8f);

        private static bool PlausibleDouble(double d) =>
            d == 0 || (double.IsFinite(d) && Math.Abs(d) is >= 1e-5 and <= 1e12);

        /// <summary>Whether a known mapping struct reads the observed nested struct the same way</summary>
        private bool Matches(int path, Struct known, string knownName, int depth = 0)
        {
            if (depth > 4) return false;
            var indices = layout.ObservedIndices(path);
            if (indices.Count == 0 || known.CountProperties(true) <= indices.Max()) return false;

            foreach (var index in indices)
            {
                if (!known.TryGetValue(index, out var info)) return false;
                var child = layout.Child(path, index);
                var shape = child is { } c ? layout.Shape(c) : null;
                if (child is null || shape is null) continue;
                if (!Compatible(child.Value, shape.Value, info.MappingType, depth)) return false;

                // GameplayTag and the other single-name structs only differ by their content
                if (knownName == "GameplayTag" &&
                    (shape != ValueShape.Name || !layout.Samples(child.Value).All(s => layout.NameText(s.Pos).Contains('.'))))
                    return false;
            }

            return true;
        }

        private bool Compatible(int path, ValueShape shape, PropertyType type, int depth)
        {
            switch (shape)
            {
                case ValueShape.Name: return type.Type == "NameProperty";
                case ValueShape.String: return type.Type == "StrProperty";
                case ValueShape.Text: return type.Type == "TextProperty";
                case ValueShape.InstancedStruct:
                    return type is { Type: "StructProperty", StructType: "InstancedStruct" };
                case ValueShape.SoftObjectPath:
                    return type.Type is "SoftObjectProperty" or "SoftClassProperty" ||
                           type is { Type: "StructProperty", StructType: "SoftObjectPath" or "SoftClassPath" };
                case ValueShape.Fixed1:
                    return type.Type is "BoolProperty" or "ByteProperty" or "Int8Property" ||
                           type is { Type: "EnumProperty", InnerType: null or { Type: "ByteProperty" } };
                case ValueShape.Fixed4:
                    return type.Type is "IntProperty" or "UInt32Property" or "FloatProperty" or "ObjectProperty" or "ClassProperty" ||
                           type is { Type: "EnumProperty", InnerType.Type: "IntProperty" or "UInt32Property" };
                case ValueShape.Fixed8:
                    return type.Type is "Int64Property" or "UInt64Property" or "DoubleProperty";
                case ValueShape.Fixed12:
                case ValueShape.Fixed24:
                    return type is { Type: "StructProperty", StructType: "Vector" or "Rotator" or "Vector3f" };
                case ValueShape.Fixed16:
                    return type is { Type: "StructProperty", StructType: "Guid" or "LinearColor" or "Vector2D" or "Vector4f" or "Quat4f" };
                case ValueShape.Struct:
                    return type is { Type: "StructProperty", StructType: { } structType } &&
                           mappings.Types.TryGetValue(structType, out var nested) &&
                           Matches(path, nested, structType, depth + 1);
                case ValueShape.Array:
                {
                    var element = layout.Child(path, -1);
                    var elementShape = element is { } e ? layout.Shape(e) : null;
                    if (type is { Type: "StructProperty", StructType: "GameplayTagContainer" })
                        return elementShape is null or ValueShape.Name;
                    if (type is not { Type: "ArrayProperty", InnerType: { } inner }) return false;
                    return element is null || elementShape is null || Compatible(element.Value, elementShape.Value, inner, depth);
                }
                default:
                    return false;
            }
        }
    }
}
