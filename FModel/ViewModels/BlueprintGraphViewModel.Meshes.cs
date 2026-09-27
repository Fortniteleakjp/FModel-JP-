using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.Engine.Animation;
using CUE4Parse.UE4.Objects.UObject;

namespace FModel.ViewModels;

/// <summary>
/// An asset the blueprint points at that carries shape keys: the skeletal mesh a component template draws (its morph
/// targets), a character part holding one, the pose asset of a face (its poses, turned into shape keys by UEFormat)
/// or the animation blueprint that plays them. Kept as an object path so the graph holds no package,
/// <see cref="Services.ShapeKeyExporter"/> loads it.
/// </summary>
public sealed class BlueprintMeshReference
{
    /// <summary>Component or class default the asset is set on.</summary>
    public string Owner { get; init; }

    /// <summary>/Game/.../SK_Head.SK_Head</summary>
    public string ObjectPath { get; init; }

    /// <summary>The owner or the asset is named after a head or a face, the parts carrying the facial shape keys.</summary>
    public bool IsHeadOrFace { get; init; }

    public string AssetName
    {
        get
        {
            var cut = ObjectPath.LastIndexOf('.');
            return cut >= 0 ? ObjectPath[(cut + 1)..] : ObjectPath[(ObjectPath.LastIndexOf('/') + 1)..];
        }
    }
}

public static partial class BlueprintGraphBuilder
{
    // mesh properties of the skinned mesh components, SkeletalMesh was renamed SkeletalMeshAsset in 5.1
    private static readonly string[] _meshProperties = ["SkeletalMeshAsset", "SkeletalMesh", "SkinnedAsset"];
    private static readonly string[] _animClassProperties = ["AnimClass"];
    // classes a class default can point at to give facial shape keys
    private static readonly string[] _shapeKeySources = ["SkeletalMesh", "SkinnedAsset", "CharacterPart", "PoseAsset", "AnimBlueprintGeneratedClass"];
    // words of a name: SKM_Head, FaceAcc, headColliderSocket -> SKM Head / Face Acc / head Collider Socket
    private static readonly Regex _words = new("[^A-Za-z]+|(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled);
    private static readonly HashSet<string> _headOrFaceWords = new(["head", "heads", "face", "faces", "facial"], StringComparer.OrdinalIgnoreCase);
    private static readonly Regex _animBlueprintSuffix = new("_(AnimBP|AnimBlueprint|ABP)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const int _MAX_ARCHETYPES = 8;
    private const int _MAX_VALUE_DEPTH = 4;

    /// <summary>A name holding the word head or face, not a longer word containing it (Surface, Heading, Overhead).</summary>
    public static bool IsHeadOrFace(string name) => !string.IsNullOrEmpty(name) && _words.Split(name).Any(_headOrFaceWords.Contains);

    /// <summary>
    /// Class of the object a reference points at. Empty when an import's class can't be read (the export checks it
    /// once loaded), null for a soft reference whose package can't be found (it could not be exported either).
    /// </summary>
    private static string ReferencedClass(object value, IFileProvider provider)
    {
        try
        {
            return value switch
            {
                FPackageIndex index => index.ResolvedObject?.Class?.Name.Text ?? string.Empty,
                FSoftObjectPath soft => SoftClassName(soft, provider),
                _ => null
            };
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static bool IsShapeKeySource(string className) =>
        className != null && (className.Length == 0 || _shapeKeySources.Any(s => className.Contains(s, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Object path a component template sets in one of <paramref name="properties"/>. A template that leaves it unset
    /// takes the one of its archetype (the parent blueprint's template it overrides, the native class default subobject).
    /// </summary>
    private static string TemplateReference(UObject template, string[] properties)
    {
        var current = template;
        for (var depth = 0; current != null && depth < _MAX_ARCHETYPES; depth++)
        {
            foreach (var name in properties)
            {
                if (current.Properties.FirstOrDefault(p => p.Name.Text == name) is { } property && ObjectPathOf(property.Tag?.GenericValue) is { } path)
                    return path;
            }

            try
            {
                current = current.Template?.Load();
            }
            catch (Exception)
            {
                return null;
            }
        }

        return null;
    }

    private static string MeshOf(UObject template) => TemplateReference(template, _meshProperties);

    /// <summary>The animation blueprint a mesh component plays, the one holding a face's pose asset.</summary>
    private static string AnimClassOf(UObject template) => TemplateReference(template, _animClassProperties);

    private static BlueprintMeshReference MeshReference(string owner, string path) => new()
    {
        Owner = owner,
        ObjectPath = path,
        IsHeadOrFace = IsHeadOrFace(owner) || IsHeadOrFace(path[(path.LastIndexOf('/') + 1)..])
    };

    /// <summary>
    /// Shape key sources the class defaults refer to under a head or face name (HeadMesh, CharacterParts[0] -> CP_Head,
    /// Face_PoseAsset...), inside arrays and structs too. A head or face animation blueprint gives all its pose assets and
    /// the mesh next to it (F_MED_X_Head_AnimBP -> F_MED_X_Head), which none of its values points at, and itself.
    /// </summary>
    private static IEnumerable<BlueprintMeshReference> DefaultsMeshes(UClass blueprint, UObject defaults)
    {
        if (defaults == null) yield break;

        var provider = defaults.Owner?.Provider;
        var ownName = DisplayClassName(blueprint.Name);
        var headAnimBlueprint = blueprint is UAnimBlueprintGeneratedClass && IsHeadOrFace(ownName);
        if (headAnimBlueprint)
        {
            // the blueprint itself, the export takes the pose assets it or its parent animation blueprint plays
            yield return new BlueprintMeshReference { Owner = ownName, ObjectPath = blueprint.GetPathName(), IsHeadOrFace = true };
            if (SiblingMesh(blueprint, provider) is { } sibling)
                yield return new BlueprintMeshReference { Owner = ownName, ObjectPath = sibling, IsHeadOrFace = true };
        }

        foreach (var property in defaults.Properties)
        {
            var references = new List<(string Name, string Path, string Class)>();
            Collect(property.Name.Text, property.Tag?.GenericValue, 0, references);
            foreach (var (name, path, className) in references)
            {
                var reference = MeshReference(name, path);
                if (reference.IsHeadOrFace)
                    yield return reference;
                else if (headAnimBlueprint && className.Contains("PoseAsset", StringComparison.OrdinalIgnoreCase))
                    yield return new BlueprintMeshReference { Owner = name, ObjectPath = path, IsHeadOrFace = true };
            }
        }

        void Collect(string name, object value, int depth, List<(string, string, string)> references)
        {
            if (depth > _MAX_VALUE_DEPTH) return;

            switch (value)
            {
                // a subobject of the class default object is a component, drawn in the Components graph
                case FPackageIndex index when index.ResolvedObject?.Outer?.Name.Text == defaults.Name:
                    return;
                case FPackageIndex or FSoftObjectPath when ObjectPathOf(value) is { } path:
                    if (ReferencedClass(value, provider) is { } className && IsShapeKeySource(className)) references.Add((name, path, className));
                    return;
                case UScriptArray array:
                    for (var i = 0; i < array.Properties.Count; i++)
                        Collect($"{name}[{i}]", array.Properties[i]?.GenericValue, depth + 1, references);
                    return;
                case FScriptStruct { StructType: FStructFallback fallback }:
                    foreach (var member in fallback.Properties)
                        Collect($"{name}.{member.Name.Text}", member.Tag?.GenericValue, depth + 1, references);
                    return;
            }
        }
    }

    /// <summary>The mesh an animation blueprint is named after, in its own folder: .../X_Head_AnimBP -> .../X_Head.X_Head.</summary>
    private static string SiblingMesh(UClass blueprint, IFileProvider provider)
    {
        var classPath = blueprint.GetPathName(); // /Game/.../X_Head_AnimBP.X_Head_AnimBP_C
        var package = classPath[..Math.Max(0, classPath.LastIndexOf('.'))];
        var name = _animBlueprintSuffix.Replace(package[(package.LastIndexOf('/') + 1)..], string.Empty);
        if (provider == null || name.Length == 0 || package.EndsWith(name, StringComparison.Ordinal)) return null;

        var meshPackage = $"{package[..(package.LastIndexOf('/') + 1)]}{name}";
        return provider.TryGetGameFile($"{meshPackage}.uasset", out _) ? $"{meshPackage}.{name}" : null;
    }

    /// <summary>Class of the object a soft reference points at, read from the export map of its package without loading it.</summary>
    private static string SoftClassName(FSoftObjectPath soft, IFileProvider provider)
    {
        var path = soft.AssetPathName.Text;
        var dot = path.LastIndexOf('.');
        provider ??= soft.Owner?.Provider;
        if (dot < 0 || provider == null || !provider.TryLoadPackage(path[..dot], out var package)) return null;

        var name = path[(dot + 1)..];
        for (var i = 0; i < package.ExportMapLength; i++)
        {
            if (new FPackageIndex(package, i + 1).ResolvedObject is { } export && export.Name.Text == name)
                return export.Class?.Name.Text ?? string.Empty;
        }

        return null;
    }

    private static string ObjectPathOf(object value)
    {
        switch (value)
        {
            case FPackageIndex { IsNull: false } index:
                try
                {
                    var path = index.ResolvedObject?.GetPathName();
                    return string.IsNullOrEmpty(path) || path == "None" ? null : path;
                }
                catch (Exception)
                {
                    return null;
                }
            case FSoftObjectPath soft:
                var asset = soft.AssetPathName.Text;
                return string.IsNullOrEmpty(asset) || asset == "None" ? null : asset;
            default:
                return null;
        }
    }
}
