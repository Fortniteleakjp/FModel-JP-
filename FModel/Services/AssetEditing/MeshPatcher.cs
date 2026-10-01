using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;

namespace FModel.Services.AssetEditing;

/// <summary>
/// reshapes a cooked static/skeletal mesh without changing its topology
/// LOD0 goes out as an OBJ whose vertices are the mesh's distinct positions (render vertices split at UV/normal seams are welded,
/// so the mesh stays closed in Blender), the edited OBJ comes back with the same vertex count in the same order.
/// every LOD's position buffer is then overwritten where it sits, same size, and the tangent basis is turned with the surface:
/// no header, offset or table changes, exactly like <see cref="TexturePatcher"/>
/// </summary>
public static class MeshPatcher
{
    public sealed class Lod
    {
        public int Index;
        public Vector3[] Positions;
        public uint[] Indices;
        /// <summary>what CUE4Parse decoded for each vertex's TangentZ, tells which packed normal format is on disk</summary>
        public uint FirstNormalData;
    }

    public sealed class Result
    {
        public string MeshName;
        /// <summary>LOD index -> the positions written, compared against what the written package reads back</summary>
        public Dictionary<int, Vector3[]> Positions = [];
    }

    #region mesh access

    public static UObject FindMesh(IPackage source, string preferredName, out List<Lod> lods, out string error)
    {
        lods = null;
        error = null;
        var meshes = source.GetExports().Where(e => e is UStaticMesh or USkeletalMesh).ToList();
        var mesh = meshes.FirstOrDefault(m => m.Name.Equals(preferredName, StringComparison.OrdinalIgnoreCase)) ?? meshes.FirstOrDefault();
        switch (mesh)
        {
            case null:
                error = "このパッケージには StaticMesh / SkeletalMesh がありません";
                return null;
            case UStaticMesh { RenderData.NaniteResources.PageStreamingStates.Length: > 0 }:
            case USkeletalMesh { NaniteResources.PageStreamingStates.Length: > 0 }:
                error = "Nanite メッシュには対応していません（ゲームで表示される形状は Nanite データ側にあるため）";
                return null;
        }

        lods = [];
        if (mesh is UStaticMesh staticMesh)
        {
            var resources = staticMesh.RenderData?.LODs ?? [];
            for (var i = 0; i < resources.Length; i++)
            {
                var lod = resources[i];
                if (lod.SkipLod || lod.PositionVertexBuffer!.Verts.Length == 0) continue;
                lods.Add(new Lod
                {
                    Index = i,
                    Positions = lod.PositionVertexBuffer.Verts.Select(ToVector).ToArray(),
                    Indices = lod.IndexBuffer!.Buffer!,
                    FirstNormalData = lod.VertexBuffer!.UV.Length > 0 ? lod.VertexBuffer.UV[0].Normal[2].Data : 0
                });
            }
        }
        else if (mesh is USkeletalMesh skeletalMesh)
        {
            var models = skeletalMesh.LODModels ?? [];
            for (var i = 0; i < models.Length; i++)
            {
                var model = models[i];
                if (model.SkipLod) continue;
                var buffer = model.VertexBufferGPUSkin;
                IReadOnlyList<FSkelMeshVertexBase> vertices = buffer?.VertsFloat is { Length: > 0 } ? buffer.VertsFloat : buffer?.VertsHalf;
                if (vertices == null || vertices.Count == 0) continue;
                lods.Add(new Lod
                {
                    Index = i,
                    Positions = vertices.Select(v => ToVector(v.Pos)).ToArray(),
                    Indices = model.Indices!.Buffer!,
                    FirstNormalData = vertices[0].Normal[2].Data
                });
            }
        }

        if (lods.Count == 0 || lods[0].Index != 0)
        {
            error = "LOD0 の頂点データがありません（ストリーミングで読み込まれていない可能性があります）";
            return null;
        }
        return mesh;
    }

    private static Vector3 ToVector(FVector v) => new(v.X, v.Y, v.Z);

    /// <summary>
    /// render vertices sharing a position become one OBJ vertex, in order of first appearance
    /// </summary>
    private static int[] Weld(Vector3[] positions, out List<Vector3> welded)
    {
        var map = new int[positions.Length];
        var seen = new Dictionary<(float, float, float), int>();
        welded = [];
        for (var i = 0; i < positions.Length; i++)
        {
            var key = (positions[i].X, positions[i].Y, positions[i].Z);
            if (!seen.TryGetValue(key, out var index))
            {
                index = welded.Count;
                welded.Add(positions[i]);
                seen[key] = index;
            }
            map[i] = index;
        }
        return map;
    }

    #endregion

    #region obj

    // OBJ is Y-up: UE (x, y, z) is written as (x, z, y) so Blender's default import shows the mesh standing,
    // swapping two axes mirrors it, so triangles are written with the opposite winding
    private static string Format(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static void ExportObj(UObject mesh, List<Lod> lods, string path)
    {
        var lod = lods[0];
        var map = Weld(lod.Positions, out var welded);

        var sb = new StringBuilder();
        sb.AppendLine($"# FModel mesh edit: {mesh.GetPathName()} LOD0");
        sb.AppendLine($"# {welded.Count} vertices ({lod.Positions.Length} render vertices welded), {lod.Indices.Length / 3} triangles, units: cm");
        sb.AppendLine("# keep the vertex count and order: move, scale or sculpt, but don't add, delete, merge or reorder vertices");
        sb.AppendLine($"o {mesh.Name}");
        foreach (var v in welded) sb.Append("v ").Append(Format(v.X)).Append(' ').Append(Format(v.Z)).Append(' ').Append(Format(v.Y)).AppendLine();
        for (var i = 0; i + 2 < lod.Indices.Length; i += 3)
        {
            var a = map[lod.Indices[i]] + 1;
            var b = map[lod.Indices[i + 1]] + 1;
            var c = map[lod.Indices[i + 2]] + 1;
            if (a == b || b == c || a == c) continue; // degenerate after welding
            sb.Append("f ").Append(a).Append(' ').Append(c).Append(' ').Append(b).AppendLine();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString());
    }

    private static List<Vector3> ReadObj(string path)
    {
        var result = new List<Vector3>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.TrimStart();
            if (!line.StartsWith("v ") && !line.StartsWith("v\t")) continue;
            var parts = line.Split((char[]) [' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) throw new FormatException($"頂点の行を読めません: {raw}");
            float Parse(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
            result.Add(new Vector3(Parse(parts[1]), Parse(parts[3]), Parse(parts[2]))); // back from (x, z, y)
        }
        return result;
    }

    #endregion

    #region apply

    public static Result Apply(EditedAssetWriter.SourcePackage package, IFileProvider provider, IPackage source, GameFile entry, string objPath, AssetEditReport report)
    {
        var mesh = FindMesh(source, entry.NameWithoutExtension, out var lods, out var error);
        if (mesh == null)
        {
            report.Error(null, error);
            return null;
        }

        List<Vector3> edited;
        try
        {
            edited = ReadObj(objPath);
        }
        catch (Exception e)
        {
            report.Error(null, $"OBJ を読み込めませんでした: {e.Message}");
            return null;
        }

        var lod0 = lods[0];
        var map = Weld(lod0.Positions, out var welded);
        if (edited.Count != welded.Count)
        {
            report.Error(mesh.Name, $"OBJ の頂点数（{edited.Count}）が元のメッシュ（{welded.Count}）と一致しません。頂点の追加・削除・結合はできません（FModel で書き出した OBJ を変形して使ってください）");
            return null;
        }

        // LOD0 from the OBJ, other LODs follow the closest LOD0 vertex
        var displacement = new Vector3[welded.Count];
        for (var i = 0; i < welded.Count; i++) displacement[i] = edited[i] - welded[i];
        var moved = displacement.Count(d => d != Vector3.Zero);
        if (moved == 0)
        {
            report.Error(mesh.Name, "OBJ の頂点位置が元のメッシュと同じです（変更がありません）");
            return null;
        }

        var grid = new SpatialGrid(welded);
        var result = new Result { MeshName = mesh.Name };
        foreach (var lod in lods)
        {
            var positions = new Vector3[lod.Positions.Length];
            for (var i = 0; i < positions.Length; i++)
            {
                var source0 = lod.Index == 0 ? map[i] : grid.Nearest(lod.Positions[i]);
                positions[i] = lod.Positions[i] + displacement[source0];
            }

            var path = $"{mesh.Name}.LOD{lod.Index}";
            if (!TryPatchLod(package, lod, positions, path, report))
            {
                if (lod.Index == 0) return null;
                report.Warn(path, "この LOD の頂点データの位置を特定できなかったため、変更していません");
                continue;
            }
            result.Positions[lod.Index] = positions;
        }

        var before = Bounds(lod0.Positions);
        var after = Bounds(result.Positions[0]);
        if (after.min.X < before.min.X - 0.01f || after.min.Y < before.min.Y - 0.01f || after.min.Z < before.min.Z - 0.01f ||
            after.max.X > before.max.X + 0.01f || after.max.Y > before.max.Y + 0.01f || after.max.Z > before.max.Z + 0.01f)
            report.Warn(mesh.Name, "形状が元の範囲（バウンディングボックス）からはみ出しています。カリングで欠けて見える場合は Properties の境界（ExtendedBounds / PositiveBoundsExtension 等）も広げてください");

        if (package.Model != null)
        {
            package.Header = package.Model.Write(UAssetApiSupport.ToEngineVersion(provider.Versions.Game), false, out var exports);
            package.Exports = exports;
        }

        report.Change(mesh.Name, $"形状を {Path.GetFileName(objPath)} に差し替え（{moved}/{welded.Count} 頂点を移動、LOD {string.Join(", ", result.Positions.Keys)}）");
        return result;
    }

    private static (Vector3 min, Vector3 max) Bounds(Vector3[] positions)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }

    /// <summary>
    /// finds "int32 12, int32 count, float3[count]" (a bulk serialized FPositionVertexBuffer) in the package files and overwrites it,
    /// then the FStaticMeshVertexBuffer that's serialized right after it to rotate the tangents
    /// </summary>
    private static bool TryPatchLod(EditedAssetWriter.SourcePackage package, Lod lod, Vector3[] positions, string path, AssetEditReport report)
    {
        var count = lod.Positions.Length;
        var pattern = new byte[8 + count * 12];
        BitConverter.TryWriteBytes(pattern.AsSpan(0), 12);
        BitConverter.TryWriteBytes(pattern.AsSpan(4), count);
        WritePositions(pattern.AsSpan(8), lod.Positions);

        var found = 0;
        foreach (var file in Files(package))
        {
            var start = 0;
            while (start < file.Length)
            {
                var at = file.AsSpan(start).IndexOf(pattern);
                if (at < 0) break;
                at += start;

                WritePositions(file.AsSpan(at + 8), positions);
                PatchTangents(file, at + pattern.Length, lod, positions, path, report);
                found++;
                start = at + pattern.Length;
            }
        }

        if (found == 0)
        {
            if (lod.Index == 0) report.Error(path, "頂点データがパッケージ内のどこにあるか特定できませんでした（データが配信されていないか、非対応の形式です）");
            return false;
        }
        if (found > 1) report.Warn(path, $"同じ頂点データが {found} 箇所にあったため、すべて書き換えました");
        return true;
    }

    private static IEnumerable<byte[]> Files(EditedAssetWriter.SourcePackage package)
    {
        if (package.Model != null)
        {
            foreach (var export in package.Model.Exports) yield return export.Data;
        }
        else
        {
            yield return package.Exports;
        }
        foreach (var payload in package.Payloads.Values) yield return payload;
    }

    private static void WritePositions(Span<byte> target, Vector3[] positions)
    {
        for (var i = 0; i < positions.Length; i++)
        {
            BitConverter.TryWriteBytes(target[(i * 12)..], positions[i].X);
            BitConverter.TryWriteBytes(target[(i * 12 + 4)..], positions[i].Y);
            BitConverter.TryWriteBytes(target[(i * 12 + 8)..], positions[i].Z);
        }
    }

    /// <summary>
    /// FStaticMeshVertexBuffer: strip flags (2 bytes), NumTexCoords, NumVertices, bUseFullPrecisionUVs, bUseHighPrecisionTangentBasis,
    /// then the tangents as a bulk array of (TangentX, TangentZ) packed as 4 x int8/uint8 or 4 x int16/uint16
    /// </summary>
    private static void PatchTangents(byte[] file, int at, Lod lod, Vector3[] positions, string path, AssetEditReport report)
    {
        var count = lod.Positions.Length;
        if (at + 26 > file.Length)
        {
            report.Warn(path, "法線データを特定できなかったため、法線は元のままです");
            return;
        }

        var numVertices = BitConverter.ToInt32(file, at + 6);
        var highPrecision = BitConverter.ToInt32(file, at + 14);
        var itemSize = BitConverter.ToInt32(file, at + 18);
        var itemCount = BitConverter.ToInt32(file, at + 22);
        var expectedSize = highPrecision == 1 ? 16 : 8;
        var data = at + 26;
        if (numVertices != count || highPrecision is not (0 or 1) || itemSize != expectedSize || itemCount != count || data + count * itemSize > file.Length)
        {
            report.Warn(path, "法線データを特定できなかったため、法線は元のままです（形状によっては陰影が不自然になります）");
            return;
        }

        var signed = highPrecision == 1 || (BitConverter.ToUInt32(file, data + 4) ^ 0x80808080) == lod.FirstNormalData;
        var normalsOld = FaceNormals(lod.Positions, lod.Indices);
        var normalsNew = FaceNormals(positions, lod.Indices);
        var component = highPrecision == 1 ? 2 : 1;

        for (var i = 0; i < count; i++)
        {
            if (positions[i] == lod.Positions[i] && normalsOld[i] == normalsNew[i]) continue;
            var o = normalsOld[i];
            var n = normalsNew[i];
            if (o.LengthSquared() < 1e-12f || n.LengthSquared() < 1e-12f) continue;

            var rotation = RotationBetween(Vector3.Normalize(o), Vector3.Normalize(n));
            if (rotation == Quaternion.Identity) continue;

            var item = data + i * itemSize;
            for (var t = 0; t < 2; t++) // TangentX, TangentZ, the 4th component (binormal sign) is kept
            {
                var offset = item + t * 4 * component;
                var vector = new Vector3(Read(file, offset, component, signed), Read(file, offset + component, component, signed), Read(file, offset + 2 * component, component, signed));
                vector = Vector3.Normalize(Vector3.Transform(vector, rotation));
                Write(file, offset, component, signed, vector.X);
                Write(file, offset + component, component, signed, vector.Y);
                Write(file, offset + 2 * component, component, signed, vector.Z);
            }
        }
    }

    private static Vector3[] FaceNormals(Vector3[] positions, uint[] indices)
    {
        var normals = new Vector3[positions.Length];
        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            uint a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if (a >= positions.Length || b >= positions.Length || c >= positions.Length) continue;
            var normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]); // area weighted
            normals[a] += normal;
            normals[b] += normal;
            normals[c] += normal;
        }
        return normals;
    }

    private static Quaternion RotationBetween(Vector3 from, Vector3 to)
    {
        var dot = Vector3.Dot(from, to);
        if (dot > 0.999999f) return Quaternion.Identity;
        if (dot < -0.999999f)
        {
            var axis = Vector3.Cross(Vector3.UnitX, from);
            if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(Vector3.UnitY, from);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var q = new Quaternion(Vector3.Cross(from, to), 1 + dot);
        return Quaternion.Normalize(q);
    }

    // packed normal components: int8 (x/127) / uint8 (x/127.5-1), high precision int16 (x/32767) / uint16
    private static float Read(byte[] file, int offset, int size, bool signed)
    {
        if (size == 1) return signed ? (sbyte) file[offset] / 127f : file[offset] / 127.5f - 1f;
        return signed ? BitConverter.ToInt16(file, offset) / 32767f : BitConverter.ToUInt16(file, offset) / 32767.5f - 1f;
    }

    private static void Write(byte[] file, int offset, int size, bool signed, float value)
    {
        value = Math.Clamp(value, -1f, 1f);
        if (size == 1)
        {
            file[offset] = signed ? (byte) (sbyte) MathF.Round(value * 127f) : (byte) Math.Clamp(MathF.Round((value + 1f) * 127.5f), 0, 255);
            return;
        }
        var raw = signed ? (ushort) (short) MathF.Round(value * 32767f) : (ushort) Math.Clamp(MathF.Round((value + 1f) * 32767.5f), 0, 65535);
        BitConverter.TryWriteBytes(file.AsSpan(offset), raw);
    }

    /// <summary>
    /// nearest LOD0 vertex for the lower LODs
    /// </summary>
    private sealed class SpatialGrid
    {
        private readonly List<Vector3> _points;
        private readonly Dictionary<(int, int, int), List<int>> _cells = [];
        private readonly float _size;

        public SpatialGrid(List<Vector3> points)
        {
            _points = points;
            var (min, max) = Bounds([.. points]);
            var extent = max - min;
            var volume = Math.Max(extent.X, 1e-3f) * Math.Max(extent.Y, 1e-3f) * Math.Max(extent.Z, 1e-3f);
            _size = MathF.Max(MathF.Cbrt(volume / Math.Max(points.Count, 1)) * 2f, 1e-3f);
            for (var i = 0; i < points.Count; i++)
            {
                var key = Cell(points[i]);
                if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = [];
                list.Add(i);
            }
        }

        private (int, int, int) Cell(Vector3 p) => ((int) MathF.Floor(p.X / _size), (int) MathF.Floor(p.Y / _size), (int) MathF.Floor(p.Z / _size));

        public int Nearest(Vector3 p)
        {
            var (cx, cy, cz) = Cell(p);
            var best = -1;
            var bestDistance = float.MaxValue;
            for (var radius = 0; radius < 64; radius++)
            {
                for (var x = cx - radius; x <= cx + radius; x++)
                for (var y = cy - radius; y <= cy + radius; y++)
                for (var z = cz - radius; z <= cz + radius; z++)
                {
                    // only the shell of this radius, the inside was searched already
                    if (Math.Max(Math.Max(Math.Abs(x - cx), Math.Abs(y - cy)), Math.Abs(z - cz)) != radius) continue;
                    if (!_cells.TryGetValue((x, y, z), out var list)) continue;
                    foreach (var i in list)
                    {
                        var distance = Vector3.DistanceSquared(_points[i], p);
                        if (distance >= bestDistance) continue;
                        bestDistance = distance;
                        best = i;
                    }
                }
                // anything outside this shell is at least radius cells away
                if (best >= 0 && bestDistance <= radius * _size * (radius * _size)) break;
            }

            if (best >= 0) return best;
            for (var i = 0; i < _points.Count; i++)
            {
                var distance = Vector3.DistanceSquared(_points[i], p);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = i;
            }
            return best;
        }
    }

    #endregion
}
