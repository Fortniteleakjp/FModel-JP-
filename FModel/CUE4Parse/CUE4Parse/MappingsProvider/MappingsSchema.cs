using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CUE4Parse.UE4.Objects.UObject;
using Serilog;

namespace CUE4Parse.MappingsProvider
{
    public class Struct
    {
        public readonly TypeMappings? Context;
        public string Name;
        public string? SuperType;
        public Lazy<Struct?> Super;
        public Dictionary<int, PropertyInfo> Properties;
        public int PropertyCount;

        private static bool TryResolveTypeMapping(TypeMappings? context, string typeName, out Struct? mapping)
        {
            mapping = null;
            if (context == null || string.IsNullOrEmpty(typeName)) return false;

            static string ExtractSimpleTypeName(string name)
            {
                var slash = name.LastIndexOf('/');
                if (slash >= 0 && slash + 1 < name.Length) name = name[(slash + 1)..];

                var dot = name.LastIndexOf('.');
                if (dot >= 0 && dot + 1 < name.Length) name = name[(dot + 1)..];

                return name;
            }

            static bool TryByKey(TypeMappings localContext, string key, out Struct? resolved)
            {
                resolved = null;
                if (string.IsNullOrEmpty(key)) return false;
                if (localContext.Types.TryGetValue(key, out resolved)) return true;

                var withUPrefix = key.StartsWith("U", StringComparison.Ordinal) ? key : "U" + key;
                if (!ReferenceEquals(withUPrefix, key) && localContext.Types.TryGetValue(withUPrefix, out resolved)) return true;

                if (key.StartsWith("U", StringComparison.Ordinal) && key.Length > 1)
                {
                    var withoutUPrefix = key[1..];
                    if (localContext.Types.TryGetValue(withoutUPrefix, out resolved)) return true;
                }

                return false;
            }

            if (TryByKey(context, typeName, out mapping)) return true;

            var simpleName = ExtractSimpleTypeName(typeName);
            if (!string.Equals(simpleName, typeName, StringComparison.Ordinal) && TryByKey(context, simpleName, out mapping)) return true;

            return false;
        }

        public Struct(TypeMappings? context, string name, int propertyCount)
        {
            Context = context;
            Name = name;
            PropertyCount = propertyCount;
        }

        public Struct(TypeMappings? context, string name, string? superType, Dictionary<int, PropertyInfo> properties, int propertyCount) : this(context, name, propertyCount)
        {
            SuperType = superType;
            Super = new Lazy<Struct?>(() =>
            {
                if (SuperType != null && TryResolveTypeMapping(Context, SuperType, out var superStruct))
                {
                    return superStruct;
                }

                return null;
            });
            Properties = properties;
        }

        public bool TryGetValue(int i, out PropertyInfo info)
        {
            if (!Properties.TryGetValue(i, out info))
            {
                return i >= PropertyCount && Super.Value != null &&
                       Super.Value.TryGetValue(i - PropertyCount, out info);
            }

            return true;
        }

        public int CountProperties(bool includeSuper)
        {
            int total = 0;
            var current = this;

            while (current != null)
            {
                total += current.PropertyCount;
                current = includeSuper ? current.Super.Value : null;
            }

            return total;
        }
    }

    public class SerializedStruct : Struct
    {
        private static int GetChildPropertyCount(UStruct struc) => struc.ChildProperties?.Length ?? 0;

        public SerializedStruct(TypeMappings? context, UStruct struc) : base(context, struc.Name, GetChildPropertyCount(struc))
        {
            Super = new Lazy<Struct?>(() =>
            {
                UStruct? superStruct = null;
                if (struc.SuperStruct is { IsNull: false })
                {
                    // Use TryLoad to avoid NullReference/parse failures while building fallback mappings.
                    struc.SuperStruct.TryLoad(out superStruct);
                }

                if (superStruct == null)
                {
                    return null;
                }

                if (superStruct is UScriptClass)
                {
                    if (Context != null && Context.Types.TryGetValue(superStruct.Name, out var scriptStruct))
                    {
                        return scriptStruct;
                    }

                    Log.Warning("Missing prop mappings for type {0}", superStruct.Name);
                    return null;
                }

                return new SerializedStruct(Context, superStruct);
            });
            Properties = new Dictionary<int, PropertyInfo>();
            var childProperties = struc.ChildProperties;
            if (childProperties == null)
            {
                return;
            }

            for (var i = 0; i < childProperties.Length; i++)
            {
                var prop = (FProperty) childProperties[i];
                var propInfo = new PropertyInfo(Math.Min(i, prop.ArrayDim - 1), prop.Name.Text, new PropertyType(prop), prop.ArrayDim);
                for (var j = 0; j < prop.ArrayDim; j++)
                {
                    Properties[i + j] = propInfo;
                }
            }
        }
    }

    public class PropertyInfo : ICloneable
    {
        public int Index;
        public string Name;
        public int? ArraySize;
        public PropertyType MappingType;

        public PropertyInfo(int index, string name, PropertyType mappingType, int? arraySize = null)
        {
            Index = index;
            Name = name;
            ArraySize = arraySize;
            MappingType = mappingType;
        }

        public override string ToString() => $"{Index + 1}/{ArraySize} -> {Name}";
        public object Clone() => this.MemberwiseClone();
    }

    public class PropertyType
    {
        public string Type;
        public string? StructType;
        public PropertyType? InnerType;
        public PropertyType? ValueType;
        public string? EnumName;
        public bool? IsEnumAsByte;
        public bool? Bool;
        public UStruct? Struct;
        public UEnum? Enum;

        public PropertyType(string type, string? structType = null, PropertyType? innerType = null, PropertyType? valueType = null, string? enumName = null, bool? isEnumAsByte = null, bool? b = null)
        {
            Type = type;
            StructType = structType;
            InnerType = innerType;
            ValueType = valueType;
            EnumName = enumName;
            IsEnumAsByte = isEnumAsByte;
            Bool = b;
        }

        public PropertyType(FProperty prop)
        {
            Type = prop.GetType().Name[1..];
            switch (prop)
            {
                case FArrayProperty array:
                    var inner = array.Inner;
                    if (inner != null) InnerType = new PropertyType(inner);
                    break;
                case FByteProperty b:
                    ApplyEnum(prop, b.Enum);
                    break;
                case FEnumProperty e:
                    ApplyEnum(prop, e.Enum);
                    break;
                case FMapProperty map:
                    var key = map.KeyProp;
                    var value = map.ValueProp;
                    if (key != null) InnerType = new PropertyType(key);
                    if (value != null) ValueType = new PropertyType(value);
                    break;
                case FSetProperty set:
                    var element = set.ElementProp;
                    if (element != null) InnerType = new PropertyType(element);
                    break;
                case FStructProperty struc:
                    var structObj = struc.Struct.ResolvedObject;
                    Struct = structObj?.Object?.Value as UStruct;
                    StructType = structObj?.Name.Text;
                    break;
                case FOptionalProperty optional:
                    value = optional.ValueProperty;
                    if (value != null) InnerType = new PropertyType(value);
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ApplyEnum(FProperty prop, FPackageIndex enumIndex)
        {
            var enumObj = enumIndex.ResolvedObject;
            Enum = enumObj?.Object?.Value as UEnum;
            EnumName = enumObj?.Name.Text;
            InnerType = prop.ElementSize switch
            {
                4 => new PropertyType("IntProperty"),
                _ => null
            };
        }
    }
}
