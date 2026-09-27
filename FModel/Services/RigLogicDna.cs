using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Rig;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Readers;
using CUE4Parse_Conversion.Writers.ActorX.Structs.Animations;
using Newtonsoft.Json;

namespace FModel.Services;

/// <summary>
/// The "DNA" asset of current Fortnite heads (UE6's UDNA, the successor of UDNAAsset), which CUE4Parse doesn't know.
/// A cooked one holds a bool, a minimum runtime DNA (definition layer only: joint and control names) and the snapshot
/// of the RigLogic instance built from the full DNA, where the facial behavior lives. Kept as it is, read by
/// <see cref="RigLogicDna"/>.
/// </summary>
public class FortniteDna : UObject
{
    public byte[] Payload { get; private set; } = [];

    /// <summary>Must run before any package is loaded.</summary>
    public static void Register() => ObjectTypeRegistry.RegisterClass("DNA", typeof(FortniteDna));

    public override void Deserialize(FAssetArchive Ar, long validPos)
    {
        base.Deserialize(Ar, validPos);
        if (Flags.HasFlag(EObjectFlags.RF_ClassDefaultObject) || Ar.Position >= validPos) return;

        Payload = Ar.ReadBytes((int) (validPos - Ar.Position));
        Ar.Position = validPos;
    }

    protected override void WriteJson(JsonWriter writer, JsonSerializer serializer)
    {
        base.WriteJson(writer, serializer);
        writer.WritePropertyName("PayloadSize");
        writer.WriteValue(Payload.Length);
    }
}

/// <summary>
/// Turns the RigLogic snapshot of a <see cref="FortniteDna"/> into poses, one per raw control (CTRL_expressions_*):
/// the joint deltas RigLogic outputs with that control at 1 and the others at 0, the corrective PSDs included. They're
/// written the way a pose asset holds them (local space additive transforms), so UEFormat's .uepose importer turns each
/// into a shape key of the head mesh.
/// The snapshot is rl4's terse dump (big endian): configuration, metadata, controls, machine learned and RBF behavior,
/// PSD net, then the joints, whose linear evaluator keeps each joint group as a block partitioned matrix of Euler
/// angle deltas (degrees) converted to quaternions on output. Rigs using the other evaluators (ML, RBF, twist/swing,
/// quaternion joint groups) aren't read.
/// </summary>
public static class RigLogicDna
{
    private const int _ATTRIBUTES_PER_JOINT = 10; // tx ty tz qx qy qz qw sx sy sz
    private const float _MIN_DELTA = 1e-5f;

    private enum EEvaluator : ushort
    {
        Auto,
        Null,
        Concrete
    }

    private sealed class JointGroup
    {
        public uint ValuesOffset, InputIndicesOffset, OutputIndicesOffset, LodsOffset, RotationIndicesOffset, RotationLodsOffset;
        public uint ValuesSize, ColCount, RowCount;
    }

    private sealed class LodRegion
    {
        public uint InputSize;
        public uint OutputSize, OutputSizePaddedToLastFullBlock;
    }

    /// <summary>Poses of every raw control that moves a joint, named after it. Throws when the snapshot can't be read.</summary>
    /// <param name="neutral">the neutral local transform RigLogic puts each joint in, the head mesh's reference pose</param>
    public static CPoseAsset BuildPoses(FortniteDna dna, out Dictionary<string, FTransform> neutral)
    {
        var payload = dna.Payload;
        if (payload.Length < 8 || BinaryPrimitives.ReadInt32LittleEndian(payload) == 0)
            throw new InvalidOperationException("the DNA asset holds no RigLogic data");

        const int dnaStart = 4; // after the IsValid bool
        var (definition, dnaEnd) = ReadDefinition(payload, dnaStart);
        var reader = new BigEndianReader(payload, dnaEnd);

        // Configuration
        reader.Skip(1); // calculation type
        var loadJoints = reader.U8() != 0;
        reader.Skip(5); // blend shapes, animated maps, ML, RBF, twist swing
        reader.Skip(3); // translation, rotation, scale types
        reader.Skip(12); // pruning thresholds

        // RigMetadata
        reader.Skip(4 * 3 + 4 + 4 * 3); // coordinate system, rotation sequence and signs, already applied by UE when cooking
        var lodCount = reader.U16();
        reader.U16(); // GUI controls
        var rawControlCount = reader.U16();
        var psdControlCount = reader.U16();
        var mlControlCount = reader.U16();
        var rbfControlCount = reader.U16();
        reader.U16(); // joint groups
        var jointAttributeCount = reader.U16();
        reader.Skip(2 * 6); // blend shapes, animated maps, ML types, RBF solvers, twists, swings
        var evaluators = new Queue<EEvaluator>(reader.U16Array().Select(e => (EEvaluator) e));

        // Controls: registered controls, GUI to raw conditional table, initial values
        reader.SkipU16Matrix();
        SkipConditionalTable(reader);
        reader.SkipArray(8);

        // creation order of the restore path: ML behavior, RBF behavior, joints (linear, quaternion, twist swing, ML),
        // blend shapes, animated maps, PSDs
        var mlBehavior = Next(evaluators);
        var rbfBehavior = Next(evaluators);
        var jointEvaluators = loadJoints && lodCount > 0 && jointAttributeCount > 0
            ? new[] { Next(evaluators), Next(evaluators), Next(evaluators), Next(evaluators) }
            : null;
        Next(evaluators); // blend shapes
        Next(evaluators); // animated maps
        var psdNet = Next(evaluators);

        if (mlBehavior == EEvaluator.Concrete) throw new NotSupportedException("machine learned behavior");
        reader.SkipArray(2); // mesh region counts
        if (rbfBehavior == EEvaluator.Concrete) throw new NotSupportedException("RBF behavior");

        // PSD net
        ushort[][] psdInputLods = [], psdOutputLods = [];
        ushort[] psdInputIndices = [];
        (ulong Offset, ulong Size, float Weight)[] psds = [];
        ushort psdMinIndex = 0;
        if (psdNet == EEvaluator.Concrete)
        {
            psdInputLods = reader.U16Matrix();
            psdOutputLods = reader.U16Matrix();
            psdInputIndices = reader.U16Array();
            psds = new (ulong, ulong, float)[reader.U32()];
            for (var i = 0; i < psds.Length; i++) psds[i] = (reader.U64(), reader.U64(), reader.F32());
            psdMinIndex = reader.U16();
            reader.U16(); // max index
        }

        if (jointEvaluators == null || jointEvaluators[0] != EEvaluator.Concrete)
            throw new InvalidOperationException("the rig has no joint behavior");
        if (jointEvaluators.Skip(1).Any(e => e == EEvaluator.Concrete))
            throw new NotSupportedException("quaternion, twist/swing or machine learned joints");

        // linear joint storage
        var values = reader.F32Array();
        var inputIndices = reader.U16Array();
        var outputIndices = reader.U16Array();
        var lodRegions = new LodRegion[reader.U32()];
        for (var i = 0; i < lodRegions.Length; i++)
        {
            var inputSize = reader.U32();
            reader.Skip(8); // aligned to 4 and 8
            var outputSize = reader.U32();
            var paddedToLastFullBlock = reader.U32();
            reader.U32(); // padded to second last full block
            lodRegions[i] = new LodRegion { InputSize = inputSize, OutputSize = outputSize, OutputSizePaddedToLastFullBlock = paddedToLastFullBlock };
        }

        var rotationIndices = reader.U16Array();
        var rotationLods = reader.U16Array();
        var jointGroups = new JointGroup[reader.U32()];
        for (var i = 0; i < jointGroups.Length; i++)
        {
            jointGroups[i] = new JointGroup
            {
                ValuesOffset = reader.U32(), InputIndicesOffset = reader.U32(), OutputIndicesOffset = reader.U32(),
                LodsOffset = reader.U32(), RotationIndicesOffset = reader.U32(), RotationLodsOffset = reader.U32(),
                ValuesSize = reader.U32(), ColCount = reader.U32(), RowCount = reader.U32()
            };
        }

        var neutralValues = reader.F32Array();
        if (neutralValues.Length < jointAttributeCount) throw new InvalidOperationException("the neutral joint values are missing");

        var (blockHeight, padTo) = DetectBlockLayout(jointGroups, lodRegions);
        var inputCount = rawControlCount + psdControlCount + mlControlCount + rbfControlCount;
        var jointCount = jointAttributeCount / _ATTRIBUTES_PER_JOINT;
        var jointNames = definition.JointNames ?? [];
        var controlNames = definition.RawControlNames ?? [];

        neutral = new Dictionary<string, FTransform>(StringComparer.OrdinalIgnoreCase);
        for (var joint = 0; joint < jointCount && joint < jointNames.Length; joint++)
        {
            var o = joint * _ATTRIBUTES_PER_JOINT;
            neutral[jointNames[joint]] = new FTransform(
                new FQuat(neutralValues[o + 3], neutralValues[o + 4], neutralValues[o + 5], neutralValues[o + 6]),
                new FVector(neutralValues[o], neutralValues[o + 1], neutralValues[o + 2]),
                new FVector(neutralValues[o + 7], neutralValues[o + 8], neutralValues[o + 9]));
        }

        var poseAsset = new CPoseAsset();
        var inputs = new float[inputCount];
        var outputs = new float[jointAttributeCount];
        for (var control = 0; control < rawControlCount; control++)
        {
            Array.Clear(inputs);
            inputs[control] = 1f;
            CalculatePsds(inputs, psdInputLods, psdOutputLods, psdInputIndices, psds, psdMinIndex);

            Array.Clear(outputs);
            for (var joint = 0; joint < jointCount; joint++) outputs[joint * _ATTRIBUTES_PER_JOINT + 6] = 1f; // identity rotations

            foreach (var group in jointGroups)
            {
                if (group.RowCount == 0) continue;
                var lod = lodRegions[group.LodsOffset]; // LOD 0 holds every row and column
                var rows = lod.OutputSize;
                var remainder = rows % blockHeight;
                var target = rows - remainder;
                var paddedRemainder = RoundUp(remainder, padTo);
                for (uint row = 0; row < rows; row++)
                {
                    var sum = 0f;
                    for (uint col = 0; col < lod.InputSize; col++)
                    {
                        var input = inputs[inputIndices[group.InputIndicesOffset + col]];
                        if (input == 0f) continue;

                        var index = row < target
                            ? row / blockHeight * blockHeight * group.ColCount + col * blockHeight + row % blockHeight
                            : target * group.ColCount + col * paddedRemainder + (row - target);
                        sum += values[group.ValuesOffset + index] * input;
                    }

                    if (sum != 0f) outputs[outputIndices[group.OutputIndicesOffset + row]] = sum;
                }

                // Euler angle deltas (degrees, XYZ, the signs were folded in by UE's coordinate conversion) to quaternions
                var rotationRows = rotationLods[group.RotationLodsOffset];
                for (var row = 0; row < rotationRows; row++)
                {
                    var start = rotationIndices[group.RotationIndicesOffset + row];
                    var (x, y, z, w) = EulerToQuaternion(outputs[start], outputs[start + 1], outputs[start + 2]);
                    outputs[start] = x;
                    outputs[start + 1] = y;
                    outputs[start + 2] = z;
                    outputs[start + 3] = w;
                }
            }

            var pose = new CPoseData { PoseName = control < controlNames.Length ? controlNames[control] : $"RawControl_{control}", CurveData = [] };
            for (var joint = 0; joint < jointCount && joint < jointNames.Length; joint++)
            {
                if (ToAdditiveKey(jointNames[joint], outputs, neutralValues, joint * _ATTRIBUTES_PER_JOINT) is { } key)
                    pose.Keys.Add(key);
            }

            if (pose.Keys.Count > 0) poseAsset.Poses.Add(pose);
        }

        return poseAsset;
    }

    /// <summary>
    /// RigLogic sets a joint to neutral + delta (translation and scale added, rotation Neutral * Delta), a pose asset
    /// adds its key on top of the reference pose (Additive * Neutral for the rotation), so the rotation is moved over.
    /// </summary>
    private static CPoseKey ToAdditiveKey(string bone, float[] outputs, float[] neutral, int offset)
    {
        var translation = new FVector(outputs[offset], outputs[offset + 1], outputs[offset + 2]);
        var delta = new FQuat(outputs[offset + 3], outputs[offset + 4], outputs[offset + 5], outputs[offset + 6]);
        var scale = new FVector(outputs[offset + 7], outputs[offset + 8], outputs[offset + 9]);
        var isRotated = Math.Abs(delta.X) > _MIN_DELTA || Math.Abs(delta.Y) > _MIN_DELTA || Math.Abs(delta.Z) > _MIN_DELTA;
        if (translation.Size() <= _MIN_DELTA && !isRotated && scale.Size() <= _MIN_DELTA) return null;

        var neutralRotation = new FQuat(neutral[offset + 3], neutral[offset + 4], neutral[offset + 5], neutral[offset + 6]);
        var additive = Normalized(Multiply(Multiply(neutralRotation, delta), Conjugate(neutralRotation)));

        var neutralScale = new FVector(neutral[offset + 7], neutral[offset + 8], neutral[offset + 9]);
        var additiveScale = new FVector(Divide(scale.X, neutralScale.X), Divide(scale.Y, neutralScale.Y), Divide(scale.Z, neutralScale.Z));
        return new CPoseKey(bone, translation, additive, additiveScale);
    }

    private static float Divide(float value, float by) => Math.Abs(by) > 1e-6f ? value / by : value;

    /// <summary>Hamilton product, UE's FQuat operator*: the right one is applied first.</summary>
    private static FQuat Multiply(FQuat a, FQuat b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    private static FQuat Conjugate(FQuat q) => new(-q.X, -q.Y, -q.Z, q.W);

    /// <summary>Exact normalization, FQuat.Normalize's fast inverse square root is off by a few thousandths.</summary>
    private static FQuat Normalized(FQuat q)
    {
        var length = Math.Sqrt((double) q.X * q.X + (double) q.Y * q.Y + (double) q.Z * q.Z + (double) q.W * q.W);
        return length < 1e-8 ? new FQuat(0, 0, 0, 1) : new FQuat((float) (q.X / length), (float) (q.Y / length), (float) (q.Z / length), (float) (q.W / length));
    }

    /// <summary>rl4's intrinsic XYZ conversion (tdm euler_to_quat), angles in degrees.</summary>
    private static (float X, float Y, float Z, float W) EulerToQuaternion(float xDegrees, float yDegrees, float zDegrees)
    {
        const double halfRadians = Math.PI / 360.0;
        double sx = Math.Sin(xDegrees * halfRadians), cx = Math.Cos(xDegrees * halfRadians);
        double sy = Math.Sin(yDegrees * halfRadians), cy = Math.Cos(yDegrees * halfRadians);
        double sz = Math.Sin(zDegrees * halfRadians), cz = Math.Cos(zDegrees * halfRadians);
        return ((float) (sx * cy * cz - cx * sy * sz),
                (float) (cx * sy * cz + sx * cy * sz),
                (float) (cx * cy * sz - sx * sy * cz),
                (float) (cx * cy * cz + sx * sy * sz));
    }

    /// <summary>
    /// PSDs (corrective poses) are products of the controls they combine, clamped to [0, 1]: a single raw control only
    /// fires the ones made of it alone.
    /// </summary>
    private static void CalculatePsds(float[] inputs, ushort[][] inputLods, ushort[][] outputLods, ushort[] inputIndices,
        (ulong Offset, ulong Size, float Weight)[] psds, ushort minIndex)
    {
        if (psds.Length == 0 || outputLods.Length == 0) return;

        var clamped = (float[]) inputs.Clone();
        foreach (var index in inputLods[0]) clamped[index] = Math.Clamp(inputs[index], 0f, 1f);
        foreach (var output in outputLods[0])
        {
            var psd = psds[output - minIndex];
            var value = psd.Weight;
            for (var i = psd.Offset; i < psd.Offset + psd.Size; i++) value *= clamped[inputIndices[i]];
            inputs[output] = Math.Min(1f, value);
        }
    }

    /// <summary>
    /// Height of the row blocks the matrices were partitioned into, twice the SIMD width RigLogic was cooked with
    /// (SSE: 4 floats, AVX: 8), found back from the LOD boundaries it stored.
    /// </summary>
    private static (uint BlockHeight, uint PadTo) DetectBlockLayout(JointGroup[] groups, LodRegion[] lods)
    {
        foreach (var padTo in new uint[] { 4, 8 })
        {
            var blockHeight = padTo * 2;
            var consistent = groups.Where(g => g.RowCount > 0).All(g =>
            {
                var lod = lods[g.LodsOffset];
                if (g.RowCount != RoundUp(lod.OutputSize, padTo)) return false;
                var endsWithPadToBlock = g.RowCount % blockHeight == padTo;
                var padded = RoundUp(lod.OutputSize, endsWithPadToBlock && g.RowCount - lod.OutputSize < padTo ? padTo : blockHeight);
                return lod.OutputSizePaddedToLastFullBlock == padded - padded % blockHeight;
            });
            if (consistent) return (blockHeight, padTo);
        }

        throw new NotSupportedException("unknown joint matrix layout");
    }

    private static uint RoundUp(uint value, uint to) => (value + to - 1) / to * to;

    private static EEvaluator Next(Queue<EEvaluator> evaluators) => evaluators.Count > 0 ? evaluators.Dequeue() : EEvaluator.Null;

    private static void SkipConditionalTable(BigEndianReader reader)
    {
        var rangeMaps = reader.U32();
        for (var i = 0; i < rangeMaps; i++)
        {
            var ranges = reader.U32();
            for (var j = 0; j < ranges; j++)
            {
                reader.Skip(8); // from, to
                reader.SkipArray(2);
            }
        }

        reader.SkipArray(2); // intervals remaining
        reader.SkipArray(2); // input indices
        reader.SkipArray(2); // output indices
        for (var i = 0; i < 4; i++) reader.SkipArray(4); // from, to, slope, cut
        reader.Skip(4); // input and output counts
    }

    /// <summary>The definition layer of the minimum runtime DNA (names of the joints and controls) and where the DNA ends.</summary>
    private static (RawDefinition Definition, int End) ReadDefinition(byte[] payload, int start)
    {
        if (Encoding.ASCII.GetString(payload, start, 3) != "DNA") throw new InvalidOperationException("no DNA stream");

        var entryCount = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(start + 7));
        var end = 0L;
        long definitionOffset = -1;
        for (var i = 0; i < entryCount; i++)
        {
            var entry = start + 11 + i * 16;
            var id = Encoding.ASCII.GetString(payload, entry, 4);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(entry + 8));
            var size = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(entry + 12));
            if (id == "defn") definitionOffset = offset;
            end = Math.Max(end, offset + size);
        }

        if (definitionOffset < 0) throw new InvalidOperationException("the DNA has no definition layer");

        var archive = new FByteArchive("DNA", payload) { Position = start + definitionOffset };
        return (new RawDefinition(new FArchiveBigEndian(archive)), (int) (start + end));
    }

    /// <summary>terse's binary archive: network byte order, sizes as uint32.</summary>
    private sealed class BigEndianReader(byte[] data, int position)
    {
        private int _position = position;

        private ReadOnlySpan<byte> Take(int count)
        {
            if (_position + count > data.Length) throw new InvalidOperationException("the RigLogic snapshot ends early");
            var span = data.AsSpan(_position, count);
            _position += count;
            return span;
        }

        public void Skip(int count) => Take(count);
        public byte U8() => Take(1)[0];
        public ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
        public ulong U64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));
        public float F32() => BinaryPrimitives.ReadSingleBigEndian(Take(4));

        public ushort[] U16Array()
        {
            var result = new ushort[U32()];
            for (var i = 0; i < result.Length; i++) result[i] = U16();
            return result;
        }

        public float[] F32Array()
        {
            var result = new float[U32()];
            for (var i = 0; i < result.Length; i++) result[i] = F32();
            return result;
        }

        public ushort[][] U16Matrix()
        {
            var result = new ushort[U32()][];
            for (var i = 0; i < result.Length; i++) result[i] = U16Array();
            return result;
        }

        public void SkipArray(int elementSize) => Skip(checked((int) U32() * elementSize));

        public void SkipU16Matrix()
        {
            var rows = U32();
            for (var i = 0; i < rows; i++) SkipArray(2);
        }
    }
}
