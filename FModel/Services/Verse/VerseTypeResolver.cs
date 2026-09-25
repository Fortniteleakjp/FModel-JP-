using System;
using System.Collections.Generic;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;

namespace FModel.Services.Verse;

/// <summary>
/// Turns the cooked UE reflection of a Verse type back into the way it is spelled in Verse.
/// Everything here is read from the cooked data, nothing is inferred.
/// </summary>
public class VerseTypeResolver
{
    /// <summary>verse path of the package the declarations are written for, e.g. /author@epic.com/Island</summary>
    private readonly string _localPackage;
    private readonly Dictionary<string, string> _pathCache = new();

    public VerseTypeResolver(string localPackage)
    {
        _localPackage = localPackage;
    }

    /// <summary>
    /// verse path of a cooked type, qualified unless it lives in the package being written
    /// e.g. <c>(/Fortnite.com/Devices:)analytics_device</c>, or just <c>event_daily_time</c> when local
    /// </summary>
    public string NameOf(UStruct? type)
    {
        if (type is null) return "any";
        if (_pathCache.TryGetValue(type.Name, out var cached)) return cached;

        // the cooked struct of a tuple type is named after its mangled spelling, tuple_L_R and so on
        if (type.Name.StartsWith("tuple_", StringComparison.Ordinal)) return Cache(type.Name, VerseMangling.Decode(type.Name));

        var relative = type.GetOrDefault<string>("PackageRelativeVersePath");
        if (string.IsNullOrEmpty(relative))
        {
            // not a Verse type (a /Script class, say), the UE name is the best we have; a Verse class
            // whose path was not cooked is still named Module-name
            return Cache(type.Name, type.Name.Contains('-') ? VerseMangling.UnmangleCasedName(type.Name.SubstringAfterLast('-')) : type.Name);
        }

        var package = VerseMangling.UnmangleCasedName(type.GetOrDefault<FName>("MangledPackageVersePath").Text);
        var name = relative.SubstringAfterLast('/');
        var scope = relative.Contains('/') ? $"{package}/{relative.SubstringBeforeLast('/')}" : package;

        return Cache(type.Name, package == _localPackage ? name : $"({scope}:){name}");
    }

    private string Cache(string key, string value)
    {
        _pathCache[key] = value;
        return value;
    }

    private UStruct? Load(FPackageIndex? index) => index?.ResolvedObject?.Object?.Value as UStruct;

    /// <summary>an enum is named by the qualified Verse name it carries</summary>
    public static string EnumName(UEnum? enumeration)
    {
        if (enumeration is null) return "any";
        if (enumeration.GetOrDefault<string>("QualifiedName") is { Length: > 0 } qualified)
            return VerseMangling.StripOwnerQualifier(qualified);
        return VerseMangling.UnmangleCasedName(enumeration.Name).SubstringAfterLast('-');
    }

    private static string EnumName(FPackageIndex? index) => EnumName(index?.ResolvedObject?.Object?.Value as UEnum);

    /// <summary>
    /// how a field or parameter of this property is spelled in Verse
    /// </summary>
    public string Resolve(FProperty? property) => property switch
    {
        null => "any",
        FInt64Property or FIntProperty or FInt16Property or FInt8Property => "int",
        FUInt64Property or FUInt32Property or FUInt16Property => "int",
        FDoubleProperty or FFloatProperty => "float",
        FBoolProperty => "logic",
        FStrProperty or FUtf8StrProperty or FVerseStringProperty or FNameProperty => "string",
        FTextProperty => "message",
        FArrayProperty array => $"[]{Resolve(array.Inner)}",
        FSetProperty set => $"[]{Resolve(set.ElementProp)}",
        FMapProperty map => $"[{Resolve(map.KeyProp)}]{Resolve(map.ValueProp)}",
        FOptionalProperty optional => $"?{Resolve(optional.ValueProperty)}",
        FEnumProperty enumeration => EnumName(enumeration.Enum),
        FByteProperty b when b.Enum is not null => EnumName(b.Enum),
        FByteProperty => "int",
        FStructProperty structure => NameOf(Load(structure.Struct)),
        FClassProperty cls => $"type{{{NameOf(Load(cls.MetaClass))}}}",
        FObjectProperty obj => NameOf(Load(obj.PropertyClass)),
        FVerseFunctionProperty => "type{_()<transacts>:any}",
        FInterfaceProperty iface => NameOf(Load(iface.InterfaceClass)),
        _ => "any"
    };
}
