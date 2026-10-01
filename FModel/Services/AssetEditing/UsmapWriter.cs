using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CUE4Parse.MappingsProvider;

namespace FModel.Services.AssetEditing;

/// <summary>
/// writes the mappings CUE4Parse already loaded back into an uncompressed .usmap
/// UAssetAPI can only read uncompressed/zstd .usmap, while the loaded file may be oodle/brotli or a .jmap,
/// re-emitting what CUE4Parse parsed also guarantees both libraries agree on the schemas
/// </summary>
public static class UsmapWriter
{
    private const ushort Magic = 0x30C4;
    private const byte VersionExplicitEnumValues = 4; // Initial, PackageVersioning, LongFName, LargeEnums, ExplicitEnumValues

    public static byte[] Write(TypeMappings mappings)
    {
        var names = new List<string>();
        var nameIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        int Name(string name)
        {
            if (name == null) return -1;
            if (!nameIndex.TryGetValue(name, out var index))
            {
                index = names.Count;
                names.Add(name);
                nameIndex[name] = index;
            }
            return index;
        }

        using var body = new MemoryStream();
        using (var w = new BinaryWriter(body, Encoding.UTF8, true))
        {
            w.Write(mappings.Enums.Count);
            foreach (var (enumName, values) in mappings.Enums)
            {
                w.Write(Name(enumName));
                w.Write((ushort) values.Count);
                foreach (var (value, valueName) in values)
                {
                    w.Write(value);
                    w.Write(Name(valueName));
                }
            }

            w.Write(mappings.Types.Count);
            foreach (var (structName, schema) in mappings.Types)
            {
                // CUE4Parse expands static arrays into one entry per element, usmap stores the first element only
                var serializable = new List<(int SchemaIndex, PropertyInfo Info)>();
                foreach (var (schemaIndex, info) in schema.Properties.OrderBy(p => p.Key))
                {
                    if (serializable.Count > 0)
                    {
                        var (lastIndex, lastInfo) = serializable[^1];
                        var lastSize = Math.Max(1, lastInfo.ArraySize ?? 1);
                        if (lastInfo.Name == info.Name && schemaIndex < lastIndex + lastSize) continue;
                    }
                    serializable.Add((schemaIndex, info));
                }

                w.Write(Name(structName));
                w.Write(Name(schema.SuperType));
                w.Write((ushort) schema.PropertyCount);
                w.Write((ushort) serializable.Count);
                foreach (var (schemaIndex, info) in serializable)
                {
                    w.Write((ushort) schemaIndex);
                    w.Write((byte) Math.Max(1, info.ArraySize ?? 1));
                    w.Write(Name(info.Name));
                    WriteType(w, info.MappingType, Name);
                }
            }
        }

        using var result = new MemoryStream();
        using (var w = new BinaryWriter(result, Encoding.UTF8, true))
        {
            w.Write(Magic);
            w.Write(VersionExplicitEnumValues);
            w.Write(0); // no package versioning, the asset/engine version decides

            using var payload = new MemoryStream();
            using (var pw = new BinaryWriter(payload, Encoding.UTF8, true))
            {
                pw.Write(names.Count);
                foreach (var name in names)
                {
                    var bytes = Encoding.UTF8.GetBytes(name);
                    pw.Write((short) bytes.Length);
                    pw.Write(bytes);
                }
                pw.Write(body.ToArray());
            }

            var data = payload.ToArray();
            w.Write((byte) 0); // no compression
            w.Write((uint) data.Length);
            w.Write((uint) data.Length);
            w.Write(data);
        }

        return result.ToArray();
    }

    private static void WriteType(BinaryWriter w, PropertyType type, Func<string, int> name)
    {
        var kind = ToUsmapType(type.Type);
        w.Write(kind);
        switch (kind)
        {
            case 26: // EnumProperty
                WriteType(w, type.InnerType ?? new PropertyType("ByteProperty"), name);
                w.Write(name(type.EnumName));
                break;
            case 9: // StructProperty
                w.Write(name(type.StructType));
                break;
            case 8: // ArrayProperty
            case 25: // SetProperty
            case 28: // OptionalProperty
                WriteType(w, type.InnerType ?? new PropertyType("ByteProperty"), name);
                break;
            case 24: // MapProperty
                WriteType(w, type.InnerType ?? new PropertyType("ByteProperty"), name);
                WriteType(w, type.ValueType ?? new PropertyType("ByteProperty"), name);
                break;
        }
    }

    // UAssetAPI's UsmapPropertyType stops at AnsiStrProperty, the CUE4Parse-only kinds are folded into what they serialize as
    private static byte ToUsmapType(string type) => type switch
    {
        "ByteProperty" => 0,
        "BoolProperty" => 1,
        "IntProperty" => 2,
        "FloatProperty" => 3,
        "ObjectProperty" or "ClassProperty" => 4,
        "NameProperty" => 5,
        "DelegateProperty" => 6,
        "DoubleProperty" => 7,
        "ArrayProperty" => 8,
        "StructProperty" => 9,
        "StrProperty" or "VerseStringProperty" => 10,
        "TextProperty" => 11,
        "InterfaceProperty" => 12,
        "MulticastDelegateProperty" or "MulticastInlineDelegateProperty" or "MulticastSparseDelegateProperty" => 13,
        "WeakObjectProperty" => 14,
        "LazyObjectProperty" => 15,
        "AssetObjectProperty" => 16,
        "SoftObjectProperty" or "SoftClassProperty" => 17,
        "UInt64Property" => 18,
        "UInt32Property" => 19,
        "UInt16Property" => 20,
        "Int64Property" => 21,
        "Int16Property" => 22,
        "Int8Property" => 23,
        "MapProperty" => 24,
        "SetProperty" => 25,
        "EnumProperty" => 26,
        "FieldPathProperty" => 27,
        "OptionalProperty" => 28,
        "Utf8StrProperty" => 29,
        "AnsiStrProperty" => 30,
        _ => 0xFF
    };
}
