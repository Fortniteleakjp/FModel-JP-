using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;
using UAssetAPI.Unversioned;

namespace FModel.Services.AssetEditing;

/// <summary>
/// .usmap files only describe native types, an instance of a blueprint class (actors placed in a map, anim notifies, BP based data...)
/// needs the layout of that class, which lives in another package. CUE4Parse loads it from there, this gives UAssetAPI the same schemas.
/// </summary>
public static class BlueprintSchemas
{
    public static void Add(Usmap usmap, IPackage source)
    {
        if (usmap == null) return;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < source.ExportMapLength; i++)
        {
            ResolvedObject cls;
            try
            {
                cls = source.ResolvePackageIndex(new FPackageIndex(source, i + 1))?.Class;
            }
            catch
            {
                continue;
            }

            // classes defined in this package are read from its own exports by UAssetAPI
            if (cls == null || cls.Package == source || usmap.Schemas.ContainsKey(cls.Name.Text) || !visited.Add(cls.Name.Text)) continue;
            try
            {
                if (cls.Object?.Value is UStruct struc) AddStruct(usmap, struc, visited);
            }
            catch
            {
                // the class package can't be loaded, UAssetAPI will report the export as unparsed
            }
        }
    }

    private static void AddStruct(Usmap usmap, UStruct struc, HashSet<string> visited)
    {
        if (struc is UScriptClass || usmap.Schemas.ContainsKey(struc.Name)) return;

        string superName = null;
        if (struc.SuperStruct is { IsNull: false } superIndex && superIndex.Load<UStruct>() is { } super)
        {
            superName = super.Name;
            if (visited.Add(super.Name)) AddStruct(usmap, super, visited);
        }

        var properties = new ConcurrentDictionary<int, UsmapProperty>();
        var schemaIndex = 0;
        foreach (var field in struc.ChildProperties ?? [])
        {
            if (field is not FProperty property) continue;

            var data = ToUsmap(usmap, new PropertyType(property), visited);
            var arraySize = Math.Max(1, property.ArrayDim);
            for (var k = 0; k < arraySize; k++)
                properties[schemaIndex + k] = new UsmapProperty(property.Name.Text, schemaIndex + k, k, arraySize, data);
            schemaIndex += arraySize;
        }

        usmap.Schemas[struc.Name] = new UsmapSchema(struc.Name, superName, schemaIndex, properties, true, null);
    }

    private static UsmapPropertyData ToUsmap(Usmap usmap, PropertyType type, HashSet<string> visited)
    {
        switch (type.Type)
        {
            case "StructProperty":
                if (type.Struct != null && type.StructType != null && !usmap.Schemas.ContainsKey(type.StructType) && visited.Add(type.StructType))
                    AddStruct(usmap, type.Struct, visited);
                return new UsmapStructData(type.StructType);
            case "EnumProperty":
            case "ByteProperty" when type.EnumName != null:
                AddEnum(usmap, type);
                return new UsmapEnumData
                {
                    Name = type.EnumName,
                    InnerType = new UsmapPropertyData(type.InnerType?.Type == "IntProperty" ? UsmapPropertyType.IntProperty : UsmapPropertyType.ByteProperty)
                };
            case "ArrayProperty":
            case "SetProperty":
            case "OptionalProperty":
                return new UsmapArrayData(Kind(type.Type)) { InnerType = type.InnerType != null ? ToUsmap(usmap, type.InnerType, visited) : new UsmapPropertyData(UsmapPropertyType.ByteProperty) };
            case "MapProperty":
                return new UsmapMapData
                {
                    InnerType = type.InnerType != null ? ToUsmap(usmap, type.InnerType, visited) : new UsmapPropertyData(UsmapPropertyType.ByteProperty),
                    ValueType = type.ValueType != null ? ToUsmap(usmap, type.ValueType, visited) : new UsmapPropertyData(UsmapPropertyType.ByteProperty)
                };
            default:
                return new UsmapPropertyData(Kind(type.Type));
        }
    }

    private static void AddEnum(Usmap usmap, PropertyType type)
    {
        if (type.EnumName == null || usmap.EnumMap.ContainsKey(type.EnumName) || type.Enum == null) return;

        // mappings store bare names, user defined enums are serialized as "E_Foo::NewEnumerator0"
        var values = new ConcurrentDictionary<long, string>();
        foreach (var (name, value) in type.Enum.Names)
        {
            var text = name.Text;
            values[value] = text.Contains("::") ? text[(text.LastIndexOf("::", StringComparison.Ordinal) + 2)..] : text;
        }
        usmap.EnumMap[type.EnumName] = new UsmapEnum(type.EnumName, values);
    }

    private static UsmapPropertyType Kind(string type) => type switch
    {
        "ClassProperty" => UsmapPropertyType.ObjectProperty,
        "SoftClassProperty" => UsmapPropertyType.SoftObjectProperty,
        "MulticastInlineDelegateProperty" or "MulticastSparseDelegateProperty" => UsmapPropertyType.MulticastDelegateProperty,
        "VerseStringProperty" => UsmapPropertyType.StrProperty,
        _ => Enum.TryParse<UsmapPropertyType>(type, out var kind) ? kind : UsmapPropertyType.Unknown
    };
}
