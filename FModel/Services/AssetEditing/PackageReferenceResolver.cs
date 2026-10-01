using System;
using System.Collections.Generic;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using CuePackageIndex = CUE4Parse.UE4.Objects.UObject.FPackageIndex;

namespace FModel.Services.AssetEditing;

/// <summary>
/// turns the {"ObjectName": "Class'Name'", "ObjectPath": "/Game/Package.3"} objects FModel prints back into package indexes,
/// importing the referenced object when the package didn't reference it before
/// </summary>
public sealed class PackageReferenceResolver
{
    private const string CoreUObject = "/Script/CoreUObject";

    private readonly UAsset _asset;
    private readonly IPackage _source;
    private readonly IFileProvider _provider;
    private Dictionary<string, FPackageIndex> _knownCache;

    public PackageReferenceResolver(UAsset asset, IPackage source, IFileProvider provider)
    {
        _asset = asset;
        _source = source;
        _provider = provider;
    }

    /// <summary>
    /// the json of every index this package already has, so unchanged references map back to the exact same index
    /// only built when a reference is actually edited, big maps have a lot of exports to describe
    /// </summary>
    private Dictionary<string, FPackageIndex> Known
    {
        get
        {
            if (_knownCache != null) return _knownCache;

            _knownCache = new Dictionary<string, FPackageIndex>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < Math.Min(_asset.Imports.Count, _source.ImportMapLength); i++)
                Remember(_source.ResolvePackageIndex(new CuePackageIndex(_source, -i - 1)), FPackageIndex.FromImport(i));
            for (var i = 0; i < Math.Min(_asset.Exports.Count, _source.ExportMapLength); i++)
                Remember(_source.ResolvePackageIndex(new CuePackageIndex(_source, i + 1)), FPackageIndex.FromExport(i));
            return _knownCache;
        }
    }

    private void Remember(ResolvedObject resolved, FPackageIndex index)
    {
        if (resolved == null) return;
        try
        {
            var key = Key(JToken.Parse(JsonConvert.SerializeObject(resolved)));
            if (key != null) _knownCache.TryAdd(key, index);
        }
        catch
        {
            // an import we can't describe can't be typed back by the user either
        }
    }

    private static string Key(JToken token) => token is JObject obj
        ? $"{obj["ObjectPath"]?.ToString()}\n{obj["ObjectName"]?.ToString()}"
        : null;

    public bool TryResolve(JToken token, out FPackageIndex index, out string error)
    {
        error = null;
        index = FPackageIndex.FromRawIndex(0);
        if (token == null || token.Type == JTokenType.Null) return true;
        if (token.Type == JTokenType.String && token.ToString() is "None" or "") return true;

        if (token is not JObject obj || obj["ObjectPath"]?.ToString() is not { Length: > 0 } objectPath)
        {
            error = "参照は {\"ObjectName\": \"Class'Name'\", \"ObjectPath\": \"/Game/...\"} 形式か null で指定してください";
            return false;
        }

        if (Known.TryGetValue(Key(obj), out index)) return true;

        var objectName = obj["ObjectName"]?.ToString() ?? string.Empty;
        var dot = objectPath.LastIndexOf('.');
        if (dot > 0 && int.TryParse(objectPath[(dot + 1)..], out var exportIndex))
        {
            var packageName = objectPath[..dot];
            if (packageName.Equals(_source.Name, StringComparison.OrdinalIgnoreCase))
            {
                if (exportIndex < 0 || exportIndex >= _asset.Exports.Count)
                {
                    error = $"このパッケージにエクスポート {exportIndex} はありません";
                    return false;
                }
                index = FPackageIndex.FromExport(exportIndex);
                return true;
            }

            if (!_provider.TryLoadPackage(packageName, out var package))
            {
                error = $"参照先のパッケージ '{packageName}' を読み込めません";
                return false;
            }
            if (exportIndex < 0 || exportIndex >= package.ExportMapLength)
            {
                error = $"'{packageName}' にエクスポート {exportIndex} はありません";
                return false;
            }

            index = Import(package.ResolvePackageIndex(new CuePackageIndex(package, exportIndex + 1)));
            Known[Key(obj)] = index;
            return true;
        }

        // script objects have no export index: "Class'Actor'" + "/Script/Engine"
        var quote = objectName.IndexOf('\'');
        if (quote <= 0 || !objectName.EndsWith('\''))
        {
            error = "ObjectName は Class'Name' 形式で指定してください";
            return false;
        }

        var className = objectName[..quote];
        var path = objectName[(quote + 1)..^1];
        var outer = PackageImport(objectPath);
        var segments = path.Split('.', ':');
        for (var i = 0; i < segments.Length; i++)
        {
            var last = i == segments.Length - 1;
            outer = FindOrAdd(last ? GuessClassPackage(className) : CoreUObject, last ? className : "Class", outer, segments[i]);
        }

        index = outer;
        Known[Key(obj)] = index;
        return true;
    }

    private FPackageIndex Import(ResolvedObject resolved)
    {
        var outer = resolved.Outer;
        if (outer == null) return PackageImport(resolved.Name.Text);

        var outerIndex = Import(outer);
        var cls = resolved.Class;
        var className = cls?.Name.Text ?? "Object";
        var classPackage = cls != null ? Outermost(cls).Name.Text : CoreUObject;
        if (cls != null && cls.Outer == null) classPackage = CoreUObject; // a class without outer is a script class we couldn't fully resolve
        return FindOrAdd(classPackage, className, outerIndex, resolved.Name.Text);
    }

    private static ResolvedObject Outermost(ResolvedObject obj)
    {
        while (obj.Outer != null) obj = obj.Outer;
        return obj;
    }

    private FPackageIndex PackageImport(string packageName) => FindOrAdd(CoreUObject, "Package", FPackageIndex.FromRawIndex(0), packageName);

    private FPackageIndex FindOrAdd(string classPackage, string className, FPackageIndex outer, string objectName)
    {
        for (var i = 0; i < _asset.Imports.Count; i++)
        {
            var import = _asset.Imports[i];
            if (import.OuterIndex.Index == outer.Index &&
                string.Equals(import.ObjectName?.ToString(), objectName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(import.ClassName?.ToString(), className, StringComparison.OrdinalIgnoreCase))
                return FPackageIndex.FromImport(i);
        }

        return _asset.AddImport(new Import(classPackage, className, outer, objectName, false, _asset));
    }

    private string GuessClassPackage(string className)
    {
        // a class this package already imports tells us where it lives
        foreach (var import in _asset.Imports)
        {
            if (!string.Equals(import.ObjectName?.ToString(), className, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(import.ClassName?.ToString(), "Class", StringComparison.OrdinalIgnoreCase)) continue;
            if (import.OuterIndex.IsImport() && import.OuterIndex.ToImport(_asset) is { } outer) return outer.ObjectName.ToString();
        }

        // core types live in CoreUObject, anything else is most likely an engine class
        return className is "Class" or "ScriptStruct" or "Enum" or "Function" or "Package" or "Object" ? CoreUObject : "/Script/Engine";
    }
}
