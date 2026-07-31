using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Assimp;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Options;

namespace CUE4Parse_Conversion.Formats.Meshes;

/// <summary>
/// FBX export backed by Assimp. FBX is a scene format, so it is intentionally
/// generated from the conversion DTOs instead of attempting to relabel PSK or UEModel data.
/// </summary>
public sealed class FbxMeshFormat : IMeshExportFormat
{
    private const float UnitScale = 0.01f;

    public string DisplayName => "Autodesk FBX";

    public IReadOnlyList<ExportFile> BuildStaticMesh(string objectName, ExportOptions options, StaticMeshDto dto, IReadOnlyDictionary<string, string>? materialPaths = null)
    {
        var results = new List<ExportFile>();
        var (start, end) = options.MeshQuality.GetRange(dto.LODs.Count);
        for (var i = start; i < end; i++)
        {
            var scene = CreateScene(objectName, dto.Materials);
            AddStaticMesh(scene, objectName, dto.LODs[i]);
            results.Add(new ExportFile("fbx", Export(scene), i == 0 ? "" : $"_LOD{i}"));
        }
        return results;
    }

    public IReadOnlyList<ExportFile> BuildSkeletalMesh(string objectName, ExportOptions options, SkeletalMeshDto dto, IReadOnlyDictionary<string, string>? materialPaths = null)
    {
        var results = new List<ExportFile>();
        var (start, end) = options.MeshQuality.GetRange(dto.LODs.Count);
        for (var i = start; i < end; i++)
        {
            var scene = CreateScene(objectName, dto.Materials);
            var boneNodes = AddSkeleton(scene.RootNode, dto.Bones);
            AddSkeletalMesh(scene, objectName, dto.LODs[i], dto.Bones, boneNodes);
            results.Add(new ExportFile("fbx", Export(scene), i == 0 ? "" : $"_LOD{i}"));
        }
        return results;
    }

    public IReadOnlyList<ExportFile> BuildSkeleton(string objectName, ExportOptions options, SkeletonDto dto)
    {
        var scene = CreateScene(objectName, dto.Materials);
        AddSkeleton(scene.RootNode, dto.Bones);
        return [new ExportFile("fbx", Export(scene))];
    }

    private static Scene CreateScene(string objectName, IReadOnlyList<MeshMaterialDto> materials)
    {
        var scene = new Scene { RootNode = new Node(objectName) };
        if (materials.Count == 0)
        {
            scene.Materials.Add(new Material { Name = "DefaultMaterial" });
            return scene;
        }

        foreach (var material in materials)
            scene.Materials.Add(new Material { Name = material.SlotName });
        return scene;
    }

    private static void AddStaticMesh(Scene scene, string objectName, MeshLodDto<MeshVertex> lod)
    {
        foreach (var section in lod.Sections)
        {
            if (!section.IsValid || section.NumFaces <= 0) continue;
            var mesh = CreateMesh($"{objectName}_Material{section.MaterialIndex}", lod, section);
            mesh.MaterialIndex = Math.Clamp(section.MaterialIndex, 0, scene.Materials.Count - 1);
            scene.RootNode.MeshIndices.Add(scene.Meshes.Count);
            scene.Meshes.Add(mesh);
        }
    }

    private static void AddSkeletalMesh(Scene scene, string objectName, MeshLodDto<SkinnedMeshVertex> lod, IReadOnlyList<MeshBoneDto> bones, Node[] boneNodes)
    {
        var inverseBindMatrices = GetInverseBindMatrices(bones, boneNodes);
        foreach (var section in lod.Sections)
        {
            if (!section.IsValid || section.NumFaces <= 0) continue;
            var mesh = CreateMesh($"{objectName}_Material{section.MaterialIndex}", lod, section);
            mesh.MaterialIndex = Math.Clamp(section.MaterialIndex, 0, scene.Materials.Count - 1);
            AddBoneWeights(mesh, lod, bones, inverseBindMatrices);
            scene.RootNode.MeshIndices.Add(scene.Meshes.Count);
            scene.Meshes.Add(mesh);
        }
    }

    private static Mesh CreateMesh<TVertex>(string name, MeshLodDto<TVertex> lod, MeshSectionDto section) where TVertex : struct, IMeshVertex
    {
        var mesh = new Mesh(name, PrimitiveType.Triangle);
        foreach (var vertex in lod.Vertices)
        {
            mesh.Vertices.Add(ConvertPosition(vertex.Position));
            mesh.Normals.Add(ConvertDirection(vertex.Normal));
            mesh.TextureCoordinateChannels[0].Add(new Vector3D(vertex.Uv.U, 1.0f - vertex.Uv.V, 0));
        }
        mesh.UVComponentCount[0] = 2;

        for (var faceIndex = 0; faceIndex < section.NumFaces; faceIndex++)
        {
            var index = section.FirstIndex + faceIndex * 3;
            if (index < 0 || index + 2 >= lod.Indices.Length) break;
            mesh.Faces.Add(new Face([
                (int) lod.Indices[index],
                (int) lod.Indices[index + 1],
                (int) lod.Indices[index + 2]
            ]));
        }
        return mesh;
    }

    private static Node[] AddSkeleton(Node root, IReadOnlyList<MeshBoneDto> bones)
    {
        var nodes = new Node[bones.Count];
        for (var i = 0; i < bones.Count; i++)
        {
            var parent = bones[i].ParentIndex >= 0 && bones[i].ParentIndex < i ? nodes[bones[i].ParentIndex] : root;
            var node = new Node(bones[i].Name, parent) { Transform = ConvertTransform(bones[i]) };
            parent.Children.Add(node);
            nodes[i] = node;
        }
        return nodes;
    }

    private static Assimp.Matrix4x4[] GetInverseBindMatrices(IReadOnlyList<MeshBoneDto> bones, Node[] nodes)
    {
        var globalMatrices = new Assimp.Matrix4x4[bones.Count];
        for (var i = 0; i < bones.Count; i++)
        {
            var parent = bones[i].ParentIndex;
            globalMatrices[i] = parent >= 0 && parent < i
                ? globalMatrices[parent] * nodes[i].Transform
                : nodes[i].Transform;
        }

        for (var i = 0; i < globalMatrices.Length; i++)
            globalMatrices[i].Inverse();
        return globalMatrices;
    }

    private static void AddBoneWeights(Mesh mesh, MeshLodDto<SkinnedMeshVertex> lod, IReadOnlyList<MeshBoneDto> bones, IReadOnlyList<Assimp.Matrix4x4> inverseBindMatrices)
    {
        var weightsByBone = new List<VertexWeight>[bones.Count];
        for (var i = 0; i < bones.Count; i++) weightsByBone[i] = [];

        for (var vertexIndex = 0; vertexIndex < lod.Vertices.Length; vertexIndex++)
        {
            foreach (var influence in lod.Vertices[vertexIndex].Influences)
            {
                if (influence.Bone >= 0 && influence.Bone < bones.Count && influence.Weight > 0)
                    weightsByBone[influence.Bone].Add(new VertexWeight(vertexIndex, influence.Weight));
            }
        }

        for (var boneIndex = 0; boneIndex < bones.Count; boneIndex++)
        {
            if (weightsByBone[boneIndex].Count == 0) continue;
            var bone = new Bone
            {
                Name = bones[boneIndex].Name,
                OffsetMatrix = inverseBindMatrices[boneIndex]
            };
            bone.VertexWeights.AddRange(weightsByBone[boneIndex]);
            mesh.Bones.Add(bone);
        }
    }

    private static Assimp.Matrix4x4 ConvertTransform(MeshBoneDto bone)
    {
        var rotation = new System.Numerics.Quaternion(bone.Transform.Rotation.X, bone.Transform.Rotation.Z, bone.Transform.Rotation.Y, -bone.Transform.Rotation.W);
        var matrix = System.Numerics.Matrix4x4.CreateScale(bone.Transform.Scale3D.X, bone.Transform.Scale3D.Z, bone.Transform.Scale3D.Y)
            * System.Numerics.Matrix4x4.CreateFromQuaternion(rotation)
            * System.Numerics.Matrix4x4.CreateTranslation(bone.Transform.Translation.X * UnitScale, bone.Transform.Translation.Z * UnitScale, bone.Transform.Translation.Y * UnitScale);

        return new Assimp.Matrix4x4(
            matrix.M11, matrix.M21, matrix.M31, matrix.M41,
            matrix.M12, matrix.M22, matrix.M32, matrix.M42,
            matrix.M13, matrix.M23, matrix.M33, matrix.M43,
            matrix.M14, matrix.M24, matrix.M34, matrix.M44);
    }

    private static Vector3D ConvertPosition(CUE4Parse.UE4.Objects.Core.Math.FVector vector) => new(vector.X * UnitScale, vector.Z * UnitScale, vector.Y * UnitScale);
    private static Vector3D ConvertDirection(CUE4Parse.UE4.Objects.Core.Math.FVector4 vector) => new(vector.X, vector.Z, vector.Y);

    private static byte[] Export(Scene scene)
    {
        var path = Path.Combine(Path.GetTempPath(), $"FModel-{Guid.NewGuid():N}.fbx");
        try
        {
            using var context = new AssimpContext();
            if (!context.ExportFile(scene, path, "fbx"))
                throw new InvalidOperationException("Assimp could not export the FBX scene.");
            return File.ReadAllBytes(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
