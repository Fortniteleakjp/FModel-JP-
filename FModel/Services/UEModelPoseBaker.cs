using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse_Conversion.Writers.ActorX.Structs.Animations;
using ZstdSharp;

namespace FModel.Services;

/// <summary>
/// Bakes poses (a face's pose asset, or the ones rebuilt from its RigLogic DNA) into an exported .uemodel as morph
/// targets, so importing the model alone gives the shape keys. Each pose is applied on top of the reference pose, the
/// mesh skinned with it (linear blend skinning, as the engine does) and the vertex offsets kept. Poses driven by the curves
/// of other poses are their weighted sum, like UEFormat's .uepose importer builds them ("curve_" when the name is taken). The file is read and written back as UEFormat lays it out: nested attribute sets, optionally compressed.
/// </summary>
public static class UEModelPoseBaker
{
    private const string _MAGIC = "UEFORMAT";
    private const float _MIN_OFFSET = 1e-4f; // cm

    private sealed record Bone(string Name, int Parent, Vector3 Position, Quaternion Rotation, Vector3 Scale);

    private sealed class Attribute(string name, byte[] data)
    {
        public string Name { get; } = name;
        public byte[] Data { get; set; } = data;
    }

    /// <summary>Adds the poses as morph targets to every LOD of the model. Returns how many were added to the first LOD.</summary>
    /// <param name="mesh">the exported mesh, its reference pose keeps the bone scales the file folds into translations</param>
    public static int Bake(string modelFile, USkinnedAsset mesh, IReadOnlyList<CPoseAsset> poseAssets)
    {
        var file = File.ReadAllBytes(modelFile);
        var (header, body, compression) = ReadHeader(file);

        var root = ReadAttributes(body, 0, out _);
        var skeleton = root.FirstOrDefault(a => a.Name == "SKELETON") ?? throw new InvalidDataException("the model has no skeleton");
        var bones = ReadBones(ReadAttributes(skeleton.Data, 0, out _).FirstOrDefault(a => a.Name == "BONES")?.Data
                              ?? throw new InvalidDataException("the skeleton has no bones"));
        var reference = ReferencePose(bones, mesh);
        var lods = root.FirstOrDefault(a => a.Name == "LODS") ?? throw new InvalidDataException("the model has no LODs");

        var baked = -1;
        var reader = new Reader(lods.Data);
        var lodCount = reader.Int();
        var writer = new BinaryWriter(new MemoryStream());
        writer.Write(lodCount);
        for (var i = 0; i < lodCount; i++)
        {
            var name = reader.FString();
            var attributes = ReadAttributes(lods.Data, reader.Position, out var end);
            reader.Position = end;

            var morphs = BakeLod(attributes, bones, reference, poseAssets);
            if (baked < 0) baked = morphs.Count;
            AppendMorphTargets(attributes, morphs);

            WriteFString(writer, name);
            WriteAttributes(writer, attributes);
        }

        lods.Data = ((MemoryStream) writer.BaseStream).ToArray();
        var newBody = new BinaryWriter(new MemoryStream());
        WriteAttributes(newBody, root);
        File.WriteAllBytes(modelFile, WriteFile(header, ((MemoryStream) newBody.BaseStream).ToArray(), compression));
        return Math.Max(baked, 0);
    }

    private static List<(string Name, List<(int Vertex, Vector3 Offset)> Deltas)> BakeLod(List<Attribute> attributes, Bone[] bones,
        Matrix4x4[] referenceLocal, IReadOnlyList<CPoseAsset> poseAssets)
    {
        var vertices = ReadVertices(attributes.FirstOrDefault(a => a.Name == "VERTICES")?.Data);
        var influences = ReadWeights(attributes.FirstOrDefault(a => a.Name == "WEIGHTS")?.Data, vertices.Length);
        var referenceGlobal = Globals(bones, referenceLocal);
        var inverseReference = referenceGlobal.Select(m => Matrix4x4.Invert(m, out var inverse) ? inverse : Matrix4x4.Identity).ToArray();
        var boneIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < bones.Length; i++) boneIndex.TryAdd(bones[i].Name, i);

        var morphs = new List<(string Name, List<(int, Vector3)> Deltas)>();
        var byName = new Dictionary<string, Vector3[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var poseAsset in poseAssets)
        {
            // bone poses first, the curve driven ones are sums of them
            foreach (var pose in poseAsset.Poses)
            {
                var local = (Matrix4x4[]) referenceLocal.Clone();
                var moved = new bool[bones.Length];
                foreach (var key in pose.Keys)
                {
                    if (!boneIndex.TryGetValue(key.BoneName, out var bone)) continue;
                    local[bone] = Posed(referenceLocal[bone], key);
                    moved[bone] = true;
                }

                if (!moved.Any(m => m)) continue;

                var posedGlobal = Globals(bones, local);
                var skinning = new Matrix4x4[bones.Length];
                var affected = new bool[bones.Length];
                for (var i = 0; i < bones.Length; i++)
                {
                    skinning[i] = inverseReference[i] * posedGlobal[i];
                    affected[i] = moved[i] || bones[i].Parent >= 0 && affected[bones[i].Parent];
                }

                var offsets = new Vector3[vertices.Length];
                for (var v = 0; v < vertices.Length; v++)
                {
                    var skinned = Vector3.Zero;
                    var total = 0f;
                    var touched = false;
                    foreach (var (bone, weight) in influences[v])
                    {
                        touched |= affected[bone];
                        skinned += Vector3.Transform(vertices[v], skinning[bone]) * weight;
                        total += weight;
                    }

                    if (touched && total > 0f) offsets[v] = skinned / total - vertices[v];
                }

                AddMorph(pose.PoseName, offsets);
            }

            foreach (var pose in poseAsset.Poses)
            {
                if (pose.CurveData is not { Length: > 0 } curves) continue;

                Vector3[] sum = null;
                for (var curve = 0; curve < curves.Length && curve < poseAsset.CurveNames.Count; curve++)
                {
                    // a pose asset gives every pose a curve of its own name, that isn't a combination
                    var curveName = poseAsset.CurveNames[curve];
                    if (Math.Abs(curves[curve]) < 0.001f || curveName.Equals(pose.PoseName, StringComparison.OrdinalIgnoreCase) ||
                        !byName.TryGetValue(curveName, out var source)) continue;
                    sum ??= new Vector3[vertices.Length];
                    for (var v = 0; v < sum.Length; v++) sum[v] += source[v] * curves[curve];
                }

                if (sum != null) AddMorph(byName.ContainsKey(pose.PoseName) ? $"curve_{pose.PoseName}" : pose.PoseName, sum);
            }
        }

        return morphs;

        void AddMorph(string name, Vector3[] offsets)
        {
            var deltas = new List<(int, Vector3)>();
            for (var v = 0; v < offsets.Length; v++)
                if (offsets[v].Length() > _MIN_OFFSET) deltas.Add((v, offsets[v]));

            if (deltas.Count == 0 || !byName.TryAdd(name, offsets)) return;
            morphs.Add((name, deltas));
        }
    }

    /// <summary>A pose key on top of the reference: translation added, additive rotation applied in the parent's space, scale grown.</summary>
    private static Matrix4x4 Posed(Matrix4x4 reference, CPoseKey key)
    {
        Matrix4x4.Decompose(reference, out var scale, out var rotation, out var translation);
        var additive = new Quaternion(key.Rotation.X, key.Rotation.Y, key.Rotation.Z, key.Rotation.W);
        var posedRotation = Quaternion.Normalize(Hamilton(additive, rotation));
        var posedScale = scale * (Vector3.One + new Vector3(key.Scale.X, key.Scale.Y, key.Scale.Z));
        var posedTranslation = translation + new Vector3(key.Location.X, key.Location.Y, key.Location.Z);
        return Local(posedTranslation, posedRotation, posedScale);
    }

    /// <summary>UE's FQuat a * b: b applied first.</summary>
    private static Quaternion Hamilton(Quaternion a, Quaternion b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    /// <summary>Row vector transform like FTransform: scale, then rotate, then translate.</summary>
    private static Matrix4x4 Local(Vector3 translation, Quaternion rotation, Vector3 scale) =>
        Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);

    private static Matrix4x4[] Globals(Bone[] bones, Matrix4x4[] local)
    {
        var global = new Matrix4x4[bones.Length];
        for (var i = 0; i < bones.Length; i++) // parents come before their children
            global[i] = bones[i].Parent >= 0 && bones[i].Parent < i ? local[i] * global[bones[i].Parent] : local[i];
        return global;
    }

    /// <summary>
    /// Local reference transforms. The file folds parent scales into translations for Blender, the mesh's own reference
    /// pose is taken instead when the bone names line up.
    /// </summary>
    private static Matrix4x4[] ReferencePose(Bone[] bones, USkinnedAsset mesh)
    {
        var meshPose = new Dictionary<string, FTransform>(StringComparer.OrdinalIgnoreCase);
        if (mesh?.ReferenceSkeleton is { FinalRefBoneInfo: { } infos, FinalRefBonePose: { } pose })
            for (var i = 0; i < infos.Length && i < pose.Length; i++) meshPose.TryAdd(infos[i].Name.Text, pose[i]);

        return bones.Select(bone => meshPose.TryGetValue(bone.Name, out var t)
            ? Local(new Vector3(t.Translation.X, t.Translation.Y, t.Translation.Z), Quaternion.Normalize(new Quaternion(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W)),
                new Vector3(t.Scale3D.X, t.Scale3D.Y, t.Scale3D.Z))
            : Local(bone.Position, Quaternion.Normalize(bone.Rotation), bone.Scale)).ToArray();
    }

    private static void AppendMorphTargets(List<Attribute> attributes, List<(string Name, List<(int Vertex, Vector3 Offset)> Deltas)> morphs)
    {
        if (morphs.Count == 0) return;

        var existing = attributes.FirstOrDefault(a => a.Name == "MORPHTARGETS");
        var writer = new BinaryWriter(new MemoryStream());
        var count = existing == null ? 0 : BitConverter.ToInt32(existing.Data, 0);
        writer.Write(count + morphs.Count);
        if (existing != null) writer.Write(existing.Data, 4, existing.Data.Length - 4);

        foreach (var (name, deltas) in morphs)
        {
            WriteFString(writer, name);
            writer.Write(deltas.Count);
            foreach (var (vertex, offset) in deltas)
            {
                writer.Write(offset.X);
                writer.Write(offset.Y);
                writer.Write(offset.Z);
                writer.Write(0f); // tangent Z delta, the importers only move the positions
                writer.Write(0f);
                writer.Write(0f);
                writer.Write(vertex);
            }
        }

        var data = ((MemoryStream) writer.BaseStream).ToArray();
        if (existing != null) existing.Data = data;
        else attributes.Add(new Attribute("MORPHTARGETS", data));
    }

    private static Vector3[] ReadVertices(byte[] data)
    {
        if (data == null) throw new InvalidDataException("a LOD has no vertices");
        var reader = new Reader(data);
        var vertices = new Vector3[reader.Int()];
        for (var i = 0; i < vertices.Length; i++) vertices[i] = reader.Vector();
        return vertices;
    }

    private static List<(int Bone, float Weight)>[] ReadWeights(byte[] data, int vertexCount)
    {
        var influences = new List<(int, float)>[vertexCount];
        for (var i = 0; i < vertexCount; i++) influences[i] = [];
        if (data == null) return influences;

        var reader = new Reader(data);
        var count = reader.Int();
        for (var i = 0; i < count; i++)
        {
            var bone = reader.UShort();
            var vertex = reader.Int();
            var weight = reader.Float();
            if (vertex >= 0 && vertex < vertexCount && weight > 0f) influences[vertex].Add((bone, weight));
        }

        return influences;
    }

    private static Bone[] ReadBones(byte[] data)
    {
        var reader = new Reader(data);
        var bones = new Bone[reader.Int()];
        for (var i = 0; i < bones.Length; i++)
        {
            var name = reader.FString();
            var parent = reader.Int();
            var position = reader.Vector();
            var rotation = new Quaternion(reader.Float(), reader.Float(), reader.Float(), reader.Float());
            bones[i] = new Bone(name, parent, position, rotation, reader.Vector());
        }

        return bones;
    }

    private static List<Attribute> ReadAttributes(byte[] data, int position, out int end)
    {
        var reader = new Reader(data) { Position = position };
        var attributes = new List<Attribute>();
        var count = reader.Int();
        for (var i = 0; i < count; i++)
        {
            var name = reader.FString();
            var size = reader.Int();
            attributes.Add(new Attribute(name, reader.Bytes(size)));
        }

        end = reader.Position;
        return attributes;
    }

    private static void WriteAttributes(BinaryWriter writer, List<Attribute> attributes)
    {
        writer.Write(attributes.Count);
        foreach (var attribute in attributes)
        {
            WriteFString(writer, attribute.Name);
            writer.Write(attribute.Data.Length);
            writer.Write(attribute.Data);
        }
    }

    /// <summary>The header up to the compression flag, the uncompressed body and the compression it had.</summary>
    private static (byte[] Header, byte[] Body, string Compression) ReadHeader(byte[] file)
    {
        var reader = new Reader(file);
        if (Encoding.ASCII.GetString(reader.Bytes(_MAGIC.Length)) != _MAGIC) throw new InvalidDataException("not a UEFormat file");
        reader.FString(); // identifier
        var version = reader.Bytes(1)[0];
        reader.FString(); // object name
        if (version >= 10) reader.FString(); // object path
        var header = file[..reader.Position];

        var isCompressed = reader.Bytes(1)[0] != 0;
        if (!isCompressed) return (header, file[reader.Position..], null);

        var compression = reader.FString();
        var uncompressedSize = reader.Int();
        reader.Int(); // compressed size
        var compressed = file[reader.Position..];
        var body = compression switch
        {
            "GZIP" => Gunzip(compressed),
            "ZSTD" => new Decompressor().Unwrap(compressed, uncompressedSize).ToArray(),
            _ => throw new InvalidDataException($"unknown compression {compression}")
        };
        return (header, body, compression);
    }

    private static byte[] WriteFile(byte[] header, byte[] body, string compression)
    {
        var writer = new BinaryWriter(new MemoryStream());
        writer.Write(header);
        if (compression == null)
        {
            writer.Write((byte) 0);
            writer.Write(body);
        }
        else
        {
            var compressed = compression == "GZIP" ? Gzip(body) : new Compressor(6).Wrap(body).ToArray();
            writer.Write((byte) 1);
            WriteFString(writer, compression);
            writer.Write(body.Length);
            writer.Write(compressed.Length);
            writer.Write(compressed);
        }

        return ((MemoryStream) writer.BaseStream).ToArray();
    }

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) gzip.Write(data);
        return output.ToArray();
    }

    private static void WriteFString(BinaryWriter writer, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private sealed class Reader(byte[] data)
    {
        public int Position { get; set; }

        public byte[] Bytes(int count)
        {
            if (count < 0 || Position + count > data.Length) throw new InvalidDataException("the UEFormat file ends early");
            var bytes = data.AsSpan(Position, count).ToArray();
            Position += count;
            return bytes;
        }

        public int Int() => BitConverter.ToInt32(Bytes(4));
        public ushort UShort() => BitConverter.ToUInt16(Bytes(2));
        public float Float() => BitConverter.ToSingle(Bytes(4));
        public Vector3 Vector() => new(Float(), Float(), Float());
        public string FString() => Encoding.UTF8.GetString(Bytes(Int()));
    }
}
