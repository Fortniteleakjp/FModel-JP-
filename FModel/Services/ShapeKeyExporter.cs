using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Engine.Animation;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Exporters;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.PoseAsset;
using CUE4Parse_Conversion.Writers.ActorX.Structs.Animations;
using FModel.ViewModels;
using Newtonsoft.Json;

namespace FModel.Services;

/// <param name="MeshFile">the .uemodel, null for pose assets whose mesh wasn't found</param>
/// <param name="PoseFiles">the .uepose of pose assets whose mesh wasn't found</param>
/// <param name="PoseCount">expressions baked into the mesh as shape keys, or the poses of the .uepose files</param>
public sealed record ShapeKeyExportedMesh(string ObjectPath, string MeshFile, string ShapeKeyFile, int MorphTargetCount,
    IReadOnlyList<string> PoseFiles, int PoseCount);

public sealed record ShapeKeyExportSummary(
    IReadOnlyList<ShapeKeyExportedMesh> Exported,
    IReadOnlyList<(string ObjectPath, string Reason)> Skipped,
    IReadOnlyList<(string ObjectPath, Exception Error)> Failed);

/// <summary>
/// Exports the shape keys of the head and face of a blueprint as a UEFormat (https://github.com/h4lfheart/UEFormat)
/// .uemodel, whose morph targets its Blender importer turns into shape keys. Besides the mesh's own morph targets, the
/// facial expressions are baked in (<see cref="UEModelPoseBaker"/>): the pose assets its animation blueprint plays, or
/// for a MetaHuman style head the poses rebuilt from its RigLogic DNA (<see cref="RigLogicDna"/>). Pose assets whose
/// mesh isn't known go out as .uepose. Next to each mesh goes a list of its shape keys and the components using it.
/// </summary>
public static class ShapeKeyExporter
{
    private const int _MAX_DEPTH = 4;

    // properties leading from a component or character part to the skeletal mesh it shows
    private static readonly string[] _meshProperties = ["SkeletalMeshAsset", "SkeletalMesh", "SkinnedAsset"];
    // properties leading to the animation blueprint playing on a mesh: a character part's AdditionalData.AnimClass, a mesh's post process one
    private static readonly string[] _animClassProperties = ["AnimClass", "PostProcessAnimBlueprint"];

    private sealed class Target
    {
        public USkinnedAsset Mesh { get; init; }
        public List<string> Owners { get; } = [];
        public Dictionary<string, UPoseAsset> Poses { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The RigLogic DNA of a MetaHuman style head and the poses rebuilt from it.</summary>
        public FortniteDna Dna { get; set; }
        public CPoseAsset RigLogicPoses { get; set; }

        public bool HasShapeKeys => Mesh?.MorphTargets.Length > 0 || Poses.Count > 0 || RigLogicPoses?.Poses.Count > 0;
    }

    /// <param name="options">export settings, with the morph targets turned on and the UEFormat mesh format (pose assets have no other)</param>
    public static async Task<ShapeKeyExportSummary> ExportAsync(IFileProvider provider, string blueprintName,
        IReadOnlyCollection<BlueprintMeshReference> references, string outputDirectory, ExportOptions options, CancellationToken cancellationToken)
    {
        var skipped = new List<(string, string)>();
        var failed = new List<(string, Exception)>();
        var targets = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
        var looseTarget = new Target(); // pose assets whose mesh isn't known

        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (mesh, poses) = Resolve(provider, reference.ObjectPath);
                if (mesh == null && poses.Count == 0)
                {
                    // an animation blueprint is only looked at for its pose assets, the mesh it plays on is reported on its own
                    if (!reference.ObjectPath.EndsWith("_C", StringComparison.Ordinal)) Skip(reference.ObjectPath, "no skeletal mesh or pose asset");
                    continue;
                }

                var target = looseTarget;
                if (mesh != null)
                {
                    var key = mesh.GetPathName();
                    if (!targets.TryGetValue(key, out target)) targets[key] = target = new Target { Mesh = mesh };
                }

                if (!target.Owners.Contains(reference.Owner)) target.Owners.Add(reference.Owner);
                foreach (var pose in poses) target.Poses.TryAdd(pose.GetPathName(), pose);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failed.Add((reference.ObjectPath, e));
            }
        }

        // pose assets found apart from their mesh (a component's AnimClass, an animation blueprint's own poses) belong
        // to it when there's a single mesh
        if (targets.Count == 1 && looseTarget.Poses.Count > 0)
        {
            var only = targets.Values.First();
            foreach (var (path, pose) in looseTarget.Poses) only.Poses.TryAdd(path, pose);
            foreach (var owner in looseTarget.Owners.Where(o => !only.Owners.Contains(o))) only.Owners.Add(owner);
            looseTarget.Poses.Clear();
        }

        foreach (var (path, target) in targets.ToList())
        {
            // a MetaHuman style head: its expressions come from RigLogic, rebuilt as poses
            if (DnaOf(target.Mesh) is { } dna)
            {
                try
                {
                    target.RigLogicPoses = RigLogicDna.BuildPoses(dna, out _);
                    target.Dna = dna;
                }
                catch (Exception e)
                {
                    failed.Add((dna.GetPathName(), e));
                }
            }

            if (target.HasShapeKeys) continue;
            Skip(path, "no morph targets nor pose asset");
            targets.Remove(path);
        }

        var exported = new List<ShapeKeyExportedMesh>();
        var all = targets.Values.Append(looseTarget).Where(t => t.Mesh != null || t.Poses.Count > 0).ToList();
        if (all.Count == 0) return new ShapeKeyExportSummary(exported, skipped, failed);

        // the poses of a known mesh are baked into it as morph targets, only the ones whose mesh wasn't found go out as .uepose
        var session = new ExportSession();
        var exporters = new Dictionary<string, (Target Target, bool IsMesh)>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in all)
        {
            if (target.Mesh != null)
            {
                var exporter = new SkinnedAssetExporter(target.Mesh);
                exporters[exporter.ObjectPath] = (target, true);
                session.Add(exporter);
                continue;
            }

            foreach (var pose in target.Poses.Values)
            {
                var exporter = new PoseAssetExporter(pose);
                if (exporters.TryAdd(exporter.ObjectPath, (target, false))) session.Add(exporter);
            }
        }

        var results = await session.RunAsync(outputDirectory, options, ct: cancellationToken).ConfigureAwait(false);

        // the materials and textures of the meshes come back too, only the meshes and poses are reported
        var meshFiles = new Dictionary<Target, string>();
        var poseFiles = all.ToDictionary(t => t, _ => new List<(UPoseAsset Pose, string File)>());
        var posesByPath = all.SelectMany(t => t.Poses.Values).GroupBy(p => new PoseAssetExporter(p).ObjectPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var result in results)
        {
            if (!exporters.TryGetValue(result.ObjectPath, out var entry)) continue;
            if (!result.Success || result.DiskFilePaths is not { Count: > 0 } files)
            {
                failed.Add((result.ObjectPath, result.Error ?? new Exception("nothing was written")));
                continue;
            }

            if (entry.IsMesh) meshFiles[entry.Target] = files[0];
            else poseFiles[entry.Target].Add((posesByPath[result.ObjectPath], files[0]));
        }

        foreach (var target in all)
        {
            var meshFile = meshFiles.GetValueOrDefault(target);
            var written = poseFiles[target];
            if (meshFile == null && written.Count == 0) continue;

            var morphTargets = target.Mesh != null && meshFile != null ? MorphTargets(target.Mesh) : [];
            var poses = written.Select(w => new { Asset = w.Pose.GetPathName(), File = Path.GetFileName(w.File), Poses = PoseNames(w.Pose) }).ToList();
            var bakedCount = 0;

            if (meshFile != null)
            {
                var poseSets = new List<(string Asset, CPoseAsset Poses)>();
                foreach (var pose in target.Poses.Values)
                {
                    if (pose.TryConvert(out var converted)) poseSets.Add((pose.GetPathName(), converted));
                    else failed.Add((pose.GetPathName(), new Exception("the pose asset could not be converted (not additive, or holds no pose)")));
                }

                if (target.RigLogicPoses is { Poses.Count: > 0 } rigLogicPoses) poseSets.Add((target.Dna.GetPathName(), rigLogicPoses));

                if (poseSets.Count > 0)
                {
                    try
                    {
                        bakedCount = UEModelPoseBaker.Bake(meshFile, target.Mesh, poseSets.Select(p => p.Poses).ToList());
                        poses.AddRange(poseSets.Select(p => new { p.Asset, File = Path.GetFileName(meshFile), Poses = p.Poses.Poses.Select(q => q.PoseName).ToList() }));
                    }
                    catch (Exception e)
                    {
                        failed.Add((meshFile, e));
                    }
                }
            }

            var anchor = meshFile ?? written[0].File;
            var name = target.Mesh?.Name ?? Path.GetFileNameWithoutExtension(anchor);
            var listFile = Path.Combine(Path.GetDirectoryName(anchor)!, $"{name}_ShapeKeys.json");
            await File.WriteAllTextAsync(listFile, JsonConvert.SerializeObject(new
            {
                Blueprint = blueprintName,
                Mesh = target.Mesh?.GetPathName(),
                UsedBy = target.Owners,
                MeshFile = meshFile == null ? null : Path.GetFileName(meshFile),
                MorphTargets = morphTargets,
                BakedPoseShapeKeys = bakedCount,
                PoseAssets = poses
            }, Formatting.Indented), cancellationToken).ConfigureAwait(false);

            exported.Add(new ShapeKeyExportedMesh(target.Mesh?.GetPathName() ?? poses[0].Asset, meshFile, listFile, morphTargets.Count,
                written.Select(w => w.File).ToList(), meshFile != null ? bakedCount : poses.Sum(p => p.Poses.Count)));
        }

        return new ShapeKeyExportSummary(exported, skipped, failed);

        // several components can point at the same asset
        void Skip(string path, string reason)
        {
            if (skipped.All(s => s.Item1 != path)) skipped.Add((path, reason));
        }
    }

    /// <summary>
    /// The skeletal mesh and pose assets behind an object path: a mesh (and the pose assets of its post process animation
    /// blueprint), a pose asset, an animation blueprint (its pose assets), or a character part (its mesh and the pose
    /// assets of the animation blueprint its AdditionalData names).
    /// </summary>
    private static (USkinnedAsset Mesh, List<UPoseAsset> Poses) Resolve(IFileProvider provider, string objectPath)
    {
        var poses = new List<UPoseAsset>();
        if (!provider.TryLoadPackageObject(objectPath, out var loaded)) return (null, poses);

        switch (loaded)
        {
            case UPoseAsset pose:
                poses.Add(pose);
                return (null, poses);
            case UClass animBlueprint:
                poses.AddRange(PosesOf(animBlueprint));
                return (null, poses);
        }

        var current = loaded;
        for (var depth = 0; current != null && depth < _MAX_DEPTH; depth++)
        {
            AddAnimBlueprintPoses(current, poses);
            if (current is USkinnedAsset mesh) return (mesh, poses);
            current = FirstObject(current, _meshProperties);
        }

        return (null, poses);
    }

    /// <summary>Pose assets of the animation blueprint an object names, directly or through its AdditionalData (a character part's).</summary>
    private static void AddAnimBlueprintPoses(UObject owner, List<UPoseAsset> poses)
    {
        foreach (var holder in new[] { owner, FirstObject(owner, ["AdditionalData"]) })
        {
            if (holder == null || FirstObject(holder, _animClassProperties) is not UClass animBlueprint) continue;
            foreach (var pose in PosesOf(animBlueprint))
                if (!poses.Contains(pose)) poses.Add(pose);
        }
    }

    /// <summary>
    /// Pose assets an animation blueprint's nodes play (AnimGraphNode_PoseBlendNode.PoseAsset...), read from its class
    /// default object and the constant data a child animation blueprint overrides them with. A child that overrides
    /// nothing plays the ones of its parent (F_MED_X_Head_AnimBP -> Fortnite_Base_3L_Head_AnimBP).
    /// </summary>
    private static List<UPoseAsset> PosesOf(UClass animBlueprint)
    {
        var poses = new List<UPoseAsset>();
        for (var (current, depth) = (animBlueprint, 0); current != null && poses.Count == 0 && depth < _MAX_DEPTH * 2; depth++)
        {
            if (current.ClassDefaultObject is { IsNull: false } pointer && pointer.TryLoad(out var defaults))
            {
                foreach (var property in defaults.Properties.Concat(defaults.SerializedSparseClassData?.Properties ?? []))
                    Collect(property.Tag?.GenericValue, 0);
            }

            current = current.SuperStruct is { IsNull: false } super && super.TryLoad(out var parent) ? parent as UAnimBlueprintGeneratedClass : null;
        }

        return poses;

        void Collect(object value, int depth)
        {
            if (depth > _MAX_DEPTH) return;
            switch (value)
            {
                case FPackageIndex { IsNull: false } index when index.ResolvedObject?.Class?.Name.Text == "PoseAsset":
                    if (index.TryLoad(out var hard) && hard is UPoseAsset hardPose && !poses.Contains(hardPose)) poses.Add(hardPose);
                    return;
                case FSoftObjectPath soft when !soft.AssetPathName.IsNone:
                    if (soft.TryLoad(out var loaded) && loaded is UPoseAsset softPose && !poses.Contains(softPose)) poses.Add(softPose);
                    return;
                case UScriptArray array:
                    foreach (var item in array.Properties) Collect(item?.GenericValue, depth + 1);
                    return;
                case FScriptStruct { StructType: FStructFallback fallback }:
                    foreach (var member in fallback.Properties) Collect(member.Tag?.GenericValue, depth + 1);
                    return;
            }
        }
    }

    /// <summary>The object the first set property of <paramref name="names"/> points at, hard or soft.</summary>
    private static UObject FirstObject(UObject owner, string[] names)
    {
        foreach (var name in names)
        {
            var value = owner.Properties.FirstOrDefault(p => p.Name.Text == name)?.Tag?.GenericValue;
            if (value is FPackageIndex { IsNull: false } index && index.TryLoad(out var hard)) return hard;
            if (value is FSoftObjectPath soft && !soft.AssetPathName.IsNone && soft.TryLoad(out var loaded)) return loaded;
        }

        return null;
    }

    /// <summary>
    /// The RigLogic DNA of a MetaHuman style head, whose expressions come from it rather than from shape keys: held by
    /// the mesh's DNAAssetUserData.
    /// </summary>
    private static FortniteDna DnaOf(USkinnedAsset mesh)
    {
        foreach (var data in mesh.AssetUserData ?? [])
        {
            if (data.ResolvedObject?.Class?.Name.Text.Contains("DNA", StringComparison.OrdinalIgnoreCase) != true || !data.TryLoad(out var userData)) continue;
            if (userData is FortniteDna direct) return direct;
            if (FirstObject(userData, ["DNAAsset"]) is FortniteDna dna) return dna;
        }

        return null;
    }

    /// <summary>Names of the morph targets and how many vertices each moves in the first LOD, read after the export decompressed them.</summary>
    private static List<object> MorphTargets(USkinnedAsset mesh)
    {
        var morphTargets = new List<object>();
        foreach (var pointer in mesh.MorphTargets)
        {
            var vertices = 0;
            try
            {
                if (pointer.TryLoad(out UMorphTarget morph) && morph.MorphLODModels is { Length: > 0 } lods)
                    vertices = lods[0].Vertices?.Length ?? 0;
            }
            catch (Exception)
            {
                // the name is still listed
            }

            morphTargets.Add(new { pointer.Name, Vertices = vertices });
        }

        return morphTargets;
    }

    private static List<string> PoseNames(UPoseAsset pose)
    {
        try
        {
            return pose.PoseContainer?.GetPoseNames().ToList() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }
}
