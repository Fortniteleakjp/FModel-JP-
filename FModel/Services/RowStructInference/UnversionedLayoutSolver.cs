#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace FModel.Services.RowStructInference;

/// <summary>
/// What the solver can tell apart from the bytes alone. The fixed sizes are resolved to a property type
/// (bool/byte, int/float, int64/double, vector...) afterwards by looking at every value that was read.
/// The order is the order guesses are tried in.
/// </summary>
internal enum ValueShape : sbyte
{
    Text,
    SoftObjectPath,
    InstancedStruct,
    Name,
    Struct,
    String,
    Array,
    Fixed4,
    Fixed1,
    Fixed8,
    Fixed12,
    Fixed16,
    Fixed24,
}

internal sealed class LayoutSolverOptions
{
    public required string[] Names { get; init; }
    /// <summary>UE5.1+ writes FTopLevelAssetPath (package + asset FName) instead of a single FName</summary>
    public bool SoftObjectPathHasTwoNames { get; init; } = true;
    /// <summary>Uncooked packages with a soft object path list store an index instead of the path</summary>
    public bool SoftObjectPathIsIndex { get; init; }
    /// <summary>FVector and friends are doubles</summary>
    public bool LargeWorldCoordinates { get; init; } = true;
    public int ImportCount { get; init; }
    public int ExportCount { get; init; }
}

internal class InferenceAbortedException(string message) : Exception(message);

internal sealed class AmbiguousLayoutException() : InferenceAbortedException("More than one layout fits the rows");

/// <summary>
/// What a try of the solver accepts. Random bytes easily read as a struct header, an array count or a row start, and
/// every such reading loosens the relaxation the search is pruned with, so the tries go from strict to loose.
/// </summary>
internal sealed record SolverProfile(
    string Name,
    int MaxArrayCount,
    bool ArrayStructs, // arrays of structs, not only of leaves
    bool LargeFixed, // 12/16/24 bytes: vectors, colors, guids
    int MaxDepth, // struct/array levels inside a row
    int MaxNestedSchema, // structs nested in a row are small, while a random header claims up to 127 values per fragment
    int MaxNestedValues,
    bool SimilarRowSpans) // row starts only where the header spans half to twice the first row's
{
    public static readonly SolverProfile[] Tries =
    [
        new("simple", 256, false, false, 2, 16, 8, true),
        new("nested", 4096, true, true, 3, 64, 32, true),
        new("any row", 4096, true, true, 3, 64, 32, false)
    ];
}

/// <summary>
/// Finds the layout of a DataTable whose row struct is missing from the mappings, from the unversioned rows alone.
/// Every row is an FName and an unversioned property header (which tells the schema index of each serialized value),
/// then the values. A value doesn't carry its size, so the shape of each schema index is searched for: the same index
/// must have the same shape in every row and the rows must chain until the end of the export.
/// <para>
/// A relaxation where every value may have any shape is computed first for the first rows, as sets of positions
/// (bitsets): where each value of a row can end, then backwards, the positions that can still reach the end of the data
/// (or a row start that can). The search drops a guess as soon as its end isn't one of those positions.
/// </para>
/// The search recurses at every guess, so run it on a thread with a large stack.
/// </summary>
internal sealed class UnversionedLayoutSolver
{
    public const int RecommendedStackSize = 64 * 1024 * 1024;

    private const int WholeTableBytes = 256 * 1024; // smaller tables get the relaxation for every row
    private const int WindowRows = 64;
    private const int MaxRowStarts = 4096;
    private const int MaxPaths = 8192;
    private const long MaxWork = 30_000_000; // per try
    private const int TryMilliseconds = 1500;
    private const int TotalMilliseconds = 3000;
    private const int AlternativeMilliseconds = 1000;
    private const int MaxSamples = 4096;

    private readonly byte[] _d;
    private readonly int _end;
    private readonly int _numRows;
    private readonly LayoutSolverOptions _options;
    private readonly ValueShape[] _shapes;
    private readonly int[] _fixedSizes;
    private readonly ValueShape[] _variableLeaves;
    private long _work;
    private long _workLimit = MaxWork;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _deadline;
    internal static Action<string>? Trace { get; set; } // diagnostics

    // schema paths: 0 is the row struct, a child is (parent, schema index) or (parent, -1) for the elements of an array
    private readonly List<int> _pathIndex = [0];
    private readonly List<int> _pathDepth = [0];
    private readonly List<sbyte> _assigned = [-1];
    private readonly Dictionary<long, int> _children = [];

    private readonly Dictionary<int, int[]>[] _structEnds;
    private readonly Dictionary<int, int[]>[] _arrayEnds;
    private readonly int[][] _leafEnds; // per variable leaf shape, per position: end + 2, 1 = invalid, 0 = unknown

    private int _window;
    private Dictionary<int, PositionSet[]>[] _good = [];

    private bool _collect;
    private bool _seekAlternative;
    private readonly HashSet<(int Row, int Pos)> _valueStarts = []; // of the first layout, every value (nested ones too)
    private readonly List<int> _rowStarts = [];
    private readonly Dictionary<int, List<(int Pos, int Row)>> _samples = [];
    private readonly Dictionary<int, SortedSet<int>> _observedIndices = [];

    private readonly SolverProfile _profile;
    private readonly int _maxDepth;
    private readonly int _minRowSpan;
    private readonly int _maxRowSpan;

    private UnversionedLayoutSolver(byte[] rows, int numRows, LayoutSolverOptions options, SolverProfile profile, int firstRowSpan, long milliseconds)
    {
        _deadline = milliseconds;
        _profile = profile;
        _maxDepth = profile.MaxDepth;
        _minRowSpan = profile.SimilarRowSpans ? Math.Max(1, firstRowSpan / 2) : 0;
        _maxRowSpan = profile.SimilarRowSpans ? firstRowSpan * 2 + 8 : int.MaxValue;
        _d = rows;
        _end = rows.Length;
        _numRows = numRows;
        _options = options;

        var shapes = new List<ValueShape>((ValueShape[]) Enum.GetValues(typeof(ValueShape)));
        if (options.SoftObjectPathIsIndex) shapes.Remove(ValueShape.SoftObjectPath);
        shapes.Remove(options.LargeWorldCoordinates ? ValueShape.Fixed12 : ValueShape.Fixed24);
        if (!profile.LargeFixed) shapes.RemoveAll(s => FixedSize(s) > 8);
        _shapes = shapes.ToArray();
        _fixedSizes = shapes.ConvertAll(FixedSize).FindAll(size => size > 0).ToArray();
        _variableLeaves = shapes.FindAll(s => s is ValueShape.Text or ValueShape.String or ValueShape.SoftObjectPath or ValueShape.InstancedStruct).ToArray();

        _structEnds = new Dictionary<int, int[]>[_maxDepth + 1];
        _arrayEnds = new Dictionary<int, int[]>[_maxDepth + 1];
        for (var i = 0; i <= _maxDepth; i++)
        {
            _structEnds[i] = [];
            _arrayEnds[i] = [];
        }

        _leafEnds = new int[_variableLeaves.Length][];
        for (var i = 0; i < _leafEnds.Length; i++)
            _leafEnds[i] = new int[_end + 1];
    }

    /// <returns>null when no layout fits every row</returns>
    private SolvedLayout? Solve()
    {
        if (_numRows <= 0) return null;
        var built = BuildRelaxation();
        Trace?.Invoke($"{_profile.Name}: relaxation={built} work={_work} window={_window} {_clock.ElapsedMilliseconds}ms");
        if (!built) return null;
        var found = Search(0, new RowWork(0), false, 0);
        Trace?.Invoke($"{_profile.Name}: search found={found} work={_work} paths={_pathIndex.Count} {_clock.ElapsedMilliseconds}ms");
        if (!found) return null;

        _collect = true;
        if (!Search(0, new RowWork(0), false, 0))
            throw new InferenceAbortedException("The solved layout didn't parse again");
        _collect = false;

        if (HasAlternative())
            throw new AmbiguousLayoutException();
        return new SolvedLayout(this);
    }

    /// <summary>
    /// Whether another layout splits the rows into values differently (nested values included). Then the bytes don't
    /// tell which is right, and a wrong guess shows garbage, so the table is left unread. A layout that only reads the
    /// same bytes another way (8 bytes as a name or as an int64) doesn't count: the first one found wins.
    /// </summary>
    private bool HasAlternative()
    {
        var solution = _assigned.ToArray();
        for (var i = 0; i < _assigned.Count; i++) _assigned[i] = -1;
        _seekAlternative = true;
        _workLimit = _work + MaxWork / 3;
        _deadline = _clock.ElapsedMilliseconds + AlternativeMilliseconds;
        bool found;
        try
        {
            found = Search(0, new RowWork(0), false, 0);
        }
        catch (InferenceAbortedException)
        {
            found = false; // none found in time
            _unproven = true;
        }

        Trace?.Invoke($"{_profile.Name}: alternative={found} work={_work} {_clock.ElapsedMilliseconds}ms");
        _seekAlternative = false;
        for (var i = 0; i < _assigned.Count; i++) _assigned[i] = i < solution.Length ? solution[i] : (sbyte) -1;
        return found;
    }

    #region Layout result

    /// <summary>Set when the search for another layout ran out of time</summary>
    private bool _unproven;

    internal sealed class SolvedLayout(UnversionedLayoutSolver solver)
    {
        /// <summary>No other layout was found, but the search for one didn't finish</summary>
        public bool Unproven => solver._unproven;

        public byte[] Data => solver._d;
        public bool LargeWorldCoordinates => solver._options.LargeWorldCoordinates;
        public int RowCount => solver._rowStarts.Count;

        public string RowName(int row) => solver.NameText(solver._rowStarts[row]);

        public IReadOnlyCollection<int> ObservedIndices(int path) =>
            solver._observedIndices.TryGetValue(path, out var set) ? set : Array.Empty<int>();

        public int? Child(int path, int index) =>
            solver._children.TryGetValue(Key(path, index), out var id) ? id : null;

        /// <summary>null when the path never had a serialized value (always zero)</summary>
        public ValueShape? Shape(int path) =>
            path < solver._assigned.Count && solver._assigned[path] >= 0 && solver._samples.ContainsKey(path)
                ? (ValueShape) solver._assigned[path]
                : null;

        public IReadOnlyList<(int Pos, int Row)> Samples(int path) =>
            solver._samples.TryGetValue(path, out var list) ? list : Array.Empty<(int, int)>();

        public uint U32(int pos) => solver.U32(pos);
        public ulong U64(int pos) => BinaryPrimitives.ReadUInt64LittleEndian(solver._d.AsSpan(pos));
        public string NameText(int pos) => solver.NameText(pos);

        /// <summary>Key of an FText with a Base history, null otherwise</summary>
        public string? TextKey(int pos)
        {
            if (unchecked((sbyte) solver._d[pos + 4]) != 0) return null;
            var q = solver.StringEnd(pos + 5, false);
            return q < 0 ? null : solver.ReadString(q);
        }
    }

    private string NameText(int pos)
    {
        var name = _options.Names[U32(pos)];
        var number = U32(pos + 4);
        return number == 0 ? name : $"{name}_{number - 1}";
    }

    private string ReadString(int pos)
    {
        var len = I32(pos);
        if (len == 0) return string.Empty;
        if (len > 0) return System.Text.Encoding.Latin1.GetString(_d, pos + 4, len - 1);
        return System.Text.Encoding.Unicode.GetString(_d, pos + 4, (-len - 1) * 2);
    }

    #endregion

    #region Primitive readers

    private static long Key(int parent, int index) => ((long) parent << 32) | (uint) index;

    private uint U32(int pos) => BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(pos));
    private int I32(int pos) => BinaryPrimitives.ReadInt32LittleEndian(_d.AsSpan(pos));
    private ushort U16(int pos) => BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(pos));
    private bool Bit(int maskStart, int bit) => (_d[maskStart + (bit >> 3)] & (1 << (bit & 7))) != 0;

    private void Spend(long amount = 1)
    {
        var before = _work;
        _work += amount;
        if (_work > _workLimit || ((_work ^ before) >> 12 != 0 && _clock.ElapsedMilliseconds > _deadline))
            throw new InferenceAbortedException("The search took too long");
    }

    private int NameEnd(int p)
    {
        if (p < 0 || p > _end - 8) return -1;
        var index = U32(p);
        var number = U32(p + 4);
        return index < (uint) _options.Names.Length && number <= 0xFFFFF ? p + 8 : -1;
    }

    private int StringEnd(int p, bool requireNonEmpty)
    {
        if (p < 0 || p > _end - 4) return -1;
        var len = I32(p);
        var q = p + 4;
        if (len == 0) return requireNonEmpty ? -1 : q;

        if (len > 0)
        {
            if (len > 0x10000 || len > _end - q || _d[q + len - 1] != 0) return -1;
            if (requireNonEmpty && len == 1) return -1;
            for (var i = q; i < q + len - 1; i++)
            {
                var b = _d[i];
                if (b == 0 || (b < 0x20 && b is not (9 or 10 or 13))) return -1;
            }
            return q + len;
        }

        if (len == int.MinValue) return -1;
        var count = -len;
        if (count > 0x8000 || count * 2L > _end - q) return -1;
        if (requireNonEmpty && count == 1) return -1;
        var last = q + (count - 1) * 2;
        if (_d[last] != 0 || _d[last + 1] != 0) return -1;
        for (var i = q; i < last; i += 2)
        {
            var c = (ushort) (_d[i] | (_d[i + 1] << 8));
            if (c == 0 || (c < 0x20 && c is not (9 or 10 or 13))) return -1;
        }
        return q + count * 2;
    }

    private int TextEnd(int p)
    {
        if (p < 0 || p > _end - 5) return -1;
        if (U32(p) > 0x1F) return -1; // ETextFlag
        var q = p + 5;
        switch (unchecked((sbyte) _d[p + 4]))
        {
            case -1: // None: culture invariant string
            {
                if (q > _end - 4) return -1;
                var hasString = I32(q);
                q += 4;
                return hasString switch
                {
                    0 => q,
                    1 => StringEnd(q, false),
                    _ => -1
                };
            }
            case 0: // Base: namespace, key, source string
                q = StringEnd(q, false);
                q = StringEnd(q, true);
                return StringEnd(q, false);
            case 11: // StringTableEntry
                q = NameEnd(q);
                return StringEnd(q, true);
            default:
                return -1;
        }
    }

    private int SoftObjectPathEnd(int p)
    {
        var q = NameEnd(p);
        if (q < 0) return -1;
        var package = _options.Names[U32(p)];
        if (!package.StartsWith('/') && package != "None") return -1;
        if (_options.SoftObjectPathHasTwoNames)
        {
            // /Game/Path/Asset.Asset, or Asset_C for a class: the asset name starts with the package's last segment
            var assetEnd = NameEnd(q);
            if (assetEnd < 0) return -1;
            if (package != "None")
            {
                var segment = NameText(p);
                segment = segment[(segment.LastIndexOf('/') + 1)..];
                if (!NameText(q).StartsWith(segment, StringComparison.OrdinalIgnoreCase)) return -1;
            }
            q = assetEnd;
        }
        return StringEnd(q, false);
    }

    /// <summary>FInstancedStruct: the struct (an import or export), the size of what follows, then the struct itself</summary>
    private int InstancedStructEnd(int p)
    {
        if (p < 0 || p > _end - 8) return -1;
        var index = I32(p);
        var size = I32(p + 4);
        if (size < 0 || size > _end - p - 8) return -1;
        if (index == 0) return size == 0 ? p + 8 : -1;
        if (index < 0 ? -index > _options.ImportCount : index > _options.ExportCount) return -1;
        if (size < 2 || !TryHeader(p + 8, null, out var headerEnd, out _, false) || headerEnd > p + 8 + size) return -1;
        return p + 8 + size;
    }

    private static int FixedSize(ValueShape shape) => shape switch
    {
        ValueShape.Fixed1 => 1,
        ValueShape.Fixed4 => 4,
        ValueShape.Fixed8 => 8,
        ValueShape.Fixed12 => 12,
        ValueShape.Fixed16 => 16,
        ValueShape.Fixed24 => 24,
        _ => 0
    };

    private static bool IsComposite(ValueShape shape) => shape is ValueShape.Struct or ValueShape.Array;

    private int LeafEnd(ValueShape shape, int p)
    {
        switch (shape)
        {
            case ValueShape.Name: return NameEnd(p);
            case ValueShape.String: return StringEnd(p, false);
            case ValueShape.Text: return TextEnd(p);
            case ValueShape.SoftObjectPath: return SoftObjectPathEnd(p);
            case ValueShape.InstancedStruct: return InstancedStructEnd(p);
            default:
            {
                var size = FixedSize(shape);
                return size > 0 && p >= 0 && p <= _end - size ? p + size : -1;
            }
        }
    }

    /// <summary>
    /// A member of a row or of a struct is in the header's zero mask when its memory is all zeros, so a serialized one
    /// never reads as zeros, an empty string or the None name (an empty soft object path still can, it isn't zeros
    /// in memory). The elements of an array have no such mask.
    /// </summary>
    private int MemberLeafEnd(ValueShape shape, int p)
    {
        var e = LeafEnd(shape, p);
        if (e < 0) return -1;
        var size = FixedSize(shape);
        if (size > 0) return IsZero(p, size) ? -1 : e;
        return shape switch
        {
            ValueShape.Name => MemberNameOk(p) ? e : -1,
            _ => MemberLeafOk(shape, p) ? e : -1
        };
    }

    private bool IsZero(int p, int size)
    {
        foreach (var b in _d.AsSpan(p, size))
            if (b != 0)
                return false;
        return true;
    }

    private bool MemberNameOk(int p) => _options.Names[U32(p)] != "None";

    private bool MemberLeafOk(ValueShape shape, int p) => shape switch
    {
        ValueShape.String => I32(p) != 0,
        ValueShape.InstancedStruct => I32(p) != 0,
        _ => true
    };

    private int CachedVariableLeafEnd(int leaf, int p)
    {
        if (p < 0 || p > _end) return -1;
        var cached = _leafEnds[leaf][p];
        if (cached == 0)
        {
            var e = LeafEnd(_variableLeaves[leaf], p);
            _leafEnds[leaf][p] = cached = e < 0 ? 1 : e + 2;
        }
        return cached - 2;
    }

    private int ArrayCount(int p)
    {
        if (p < 0 || p > _end - 4) return -1;
        var count = I32(p);
        return count >= 1 && count <= _profile.MaxArrayCount && count <= _end - p - 4 ? count : -1;
    }

    /// <summary>FUnversionedHeader, validated the way UE writes it</summary>
    private bool TryHeader(int p, List<(int Index, bool NonZero)>? values, out int end, out int nonZeroCount, bool nested) =>
        TryHeader(p, values, out end, out nonZeroCount, out _, nested);

    /// <param name="span">last schema index + 1</param>
    private bool TryHeader(int p, List<(int Index, bool NonZero)>? values, out int end, out int nonZeroCount, out int span, bool nested)
    {
        end = -1;
        nonZeroCount = 0;
        span = 0;
        values?.Clear();

        Span<ushort> fragments = stackalloc ushort[64];
        var fragmentCount = 0;
        var zeroMaskNum = 0;
        var schema = 0;
        var maxSchema = nested ? _profile.MaxNestedSchema : 4096;
        var q = p;
        while (true)
        {
            if (q < 0 || q > _end - 2) return false;
            var packed = U16(q);
            q += 2;
            var skip = packed & 0x7F;
            var hasZeroes = (packed & 0x80) != 0;
            var isLast = (packed & 0x100) != 0;
            var valueNum = packed >> 9;

            if (valueNum == 0)
            {
                if (isLast)
                {
                    if (fragmentCount != 0 || skip != 0 || hasZeroes) return false; // only an empty header ends without values
                }
                else if (skip != 127 || hasZeroes) return false; // a value-less fragment only carries a long skip
            }

            if (fragmentCount == fragments.Length) return false;
            fragments[fragmentCount++] = packed;
            if (hasZeroes) zeroMaskNum += valueNum;
            schema += skip + valueNum;
            if (schema > maxSchema) return false;
            if (isLast) break;
        }

        var maskStart = q;
        if (zeroMaskNum > 0)
        {
            var maskBytes = zeroMaskNum <= 8 ? 1 : zeroMaskNum <= 16 ? 2 : (zeroMaskNum + 31) / 32 * 4;
            if (q > _end - maskBytes) return false;
            for (var bit = zeroMaskNum; bit < maskBytes * 8; bit++)
                if (Bit(maskStart, bit)) return false;
            q += maskBytes;
        }

        var schemaIt = 0;
        var maskIndex = 0;
        for (var f = 0; f < fragmentCount; f++)
        {
            var packed = fragments[f];
            var hasZeroes = (packed & 0x80) != 0;
            var valueNum = packed >> 9;
            schemaIt += packed & 0x7F;

            var anyZero = false;
            for (var j = 0; j < valueNum; j++)
            {
                var nonZero = true;
                if (hasZeroes)
                {
                    if (Bit(maskStart, maskIndex))
                    {
                        nonZero = false;
                        anyZero = true;
                    }
                    maskIndex++;
                }

                if (nonZero) nonZeroCount++;
                values?.Add((schemaIt, nonZero));
                schemaIt++;
            }

            if (hasZeroes && !anyZero) return false;
        }

        if (nested && (nonZeroCount == 0 || nonZeroCount > _profile.MaxNestedValues)) return false;
        end = q;
        span = schemaIt;
        return true;
    }

    private bool TryRowHead(int p, List<(int Index, bool NonZero)>? values, out int headerEnd, out int nonZeroCount)
    {
        headerEnd = -1;
        nonZeroCount = 0;
        var q = NameEnd(p);
        return q >= 0 && TryHeader(q, values, out headerEnd, out nonZeroCount, out var span, false) &&
               (p == 0 || (span >= _minRowSpan && span <= _maxRowSpan));
    }

    /// <summary>
    /// Random bytes often look like a row start, which leaves the relaxation too loose to prune anything. The rows of a
    /// table usually cover a similar part of the schema, so the first try only accepts row starts whose header spans
    /// half to twice the first row's, and only if that fails, any.
    /// </summary>
    /// <summary>Below this many rows, a layout whose uniqueness wasn't proven in time is turned down</summary>
    private const int MinRowsUnproven = 8;

    public static SolvedLayout? SolveWithFallback(byte[] rows, int numRows, LayoutSolverOptions options)
    {
        if (numRows <= 0) return null;
        // one row hardly constrains anything: on Fortnite's tables, a fifth of the single row layouts found were wrong
        if (numRows == 1) throw new InferenceAbortedException("A single row doesn't tell its layout reliably");
        var clock = Stopwatch.StartNew();
        var probe = new UnversionedLayoutSolver(rows, numRows, options, SolverProfile.Tries[^1], 0, TryMilliseconds);
        var q = probe.NameEnd(0);
        if (q < 0 || !probe.TryHeader(q, null, out _, out _, out var span, false)) return null;

        InferenceAbortedException? aborted = null;
        foreach (var profile in SolverProfile.Tries)
        {
            var remaining = TotalMilliseconds - clock.ElapsedMilliseconds;
            if (remaining <= 0) break;
            try
            {
                var layout = new UnversionedLayoutSolver(rows, numRows, options, profile, span, Math.Min(TryMilliseconds, remaining)).Solve();
                if (layout is { Unproven: true } && numRows < MinRowsUnproven)
                    throw new InferenceAbortedException("Couldn't make sure that no other layout fits the rows");
                if (layout is not null) return layout;
                Trace?.Invoke($"{profile.Name}: no layout");
            }
            catch (AmbiguousLayoutException)
            {
                throw; // a looser try only finds more layouts
            }
            catch (InferenceAbortedException e)
            {
                aborted = e;
                Trace?.Invoke($"{profile.Name}: {e.Message}");
            }
        }

        if (aborted is not null) throw aborted;
        return null;
    }

    #endregion

    #region Relaxation

    /// <summary>Growable bitset of absolute positions at or after <see cref="Base"/></summary>
    private sealed class PositionSet(int @base)
    {
        public readonly int Base = @base;
        private ulong[] _words = new ulong[4];
        private int _lo = int.MaxValue, _hi = -1;

        public bool IsEmpty => _hi < 0;

        private void Ensure(int word)
        {
            if (word < _words.Length) return;
            Array.Resize(ref _words, Math.Max(word + 1, _words.Length * 2));
        }

        public void Add(int pos)
        {
            var rel = pos - Base;
            if (rel < 0) return;
            var word = rel >> 6;
            Ensure(word);
            _words[word] |= 1UL << (rel & 63);
            if (word < _lo) _lo = word;
            if (word > _hi) _hi = word;
        }

        public bool Contains(int pos)
        {
            var rel = pos - Base;
            if (rel < 0) return false;
            var word = rel >> 6;
            return word >= _lo && word <= _hi && ((_words[word] >> (rel & 63)) & 1) != 0;
        }

        /// <summary>Adds every position of <paramref name="source"/> (same base) moved by <paramref name="shift"/>, up to <paramref name="max"/></summary>
        public void AddShifted(PositionSet source, int shift, int max)
        {
            if (source.IsEmpty) return;
            var wordShift = shift >> 6;
            var bitShift = shift & 63;
            var lastWord = (max - Base) >> 6;
            if (max < Base) return;
            for (var i = source._lo; i <= source._hi; i++)
            {
                var v = source._words[i];
                if (v == 0) continue;
                var target = i + wordShift;
                if (target > lastWord) break;
                Ensure(target + 1);
                _words[target] |= v << bitShift;
                if (target < _lo) _lo = target;
                if (target > _hi) _hi = target;
                if (bitShift != 0 && target + 1 <= lastWord && (v >> (64 - bitShift)) != 0)
                {
                    _words[target + 1] |= v >> (64 - bitShift);
                    if (target + 1 > _hi) _hi = target + 1;
                }
            }
            ClipAbove(max);
        }

        private void ClipAbove(int max)
        {
            if (_hi < 0) return;
            var rel = max - Base;
            var word = rel >> 6;
            if (word > _hi) return;
            var bit = rel & 63;
            _words[word] &= bit == 63 ? ulong.MaxValue : (1UL << (bit + 1)) - 1;
            for (var i = word + 1; i <= _hi; i++) _words[i] = 0;
            _hi = word;
            while (_hi >= _lo && _words[_hi] == 0) _hi--;
            if (_hi < _lo)
            {
                _lo = int.MaxValue;
                _hi = -1;
            }
        }

        public List<int> ToList()
        {
            var list = new List<int>();
            for (var i = _lo; i <= _hi; i++)
            {
                var v = _words[i];
                while (v != 0)
                {
                    list.Add(Base + (i << 6) + BitOperations.TrailingZeroCount(v));
                    v &= v - 1;
                }
            }
            return list;
        }
    }

    /// <summary>Where a member value (of a row or a struct) of any shape that starts at one of <paramref name="from"/> can end</summary>
    private PositionSet Step(PositionSet from, int depth)
    {
        var to = new PositionSet(from.Base);
        var positions = from.ToList();
        Spend(positions.Count);
        foreach (var q in positions)
        {
            foreach (var size in _fixedSizes)
                if (q <= _end - size && !IsZero(q, size))
                    to.Add(q + size);
            if (NameEnd(q) >= 0 && MemberNameOk(q)) to.Add(q + 8);
            for (var leaf = 0; leaf < _variableLeaves.Length; leaf++)
            {
                var e = CachedVariableLeafEnd(leaf, q);
                if (e >= 0 && MemberLeafOk(_variableLeaves[leaf], q)) to.Add(e);
            }

            if (depth >= _maxDepth) continue;
            foreach (var e in StructEnds(q, depth)) to.Add(e);
            foreach (var e in ArrayEnds(q, depth)) to.Add(e);
        }

        return to;
    }

    /// <summary>Whether a value of any shape starting at <paramref name="q"/> can end in <paramref name="targets"/></summary>
    private bool AnyEndIn(int q, int depth, PositionSet targets)
    {
        foreach (var size in _fixedSizes)
            if (q <= _end - size && targets.Contains(q + size) && !IsZero(q, size))
                return true;
        if (NameEnd(q) >= 0 && targets.Contains(q + 8) && MemberNameOk(q)) return true;
        for (var leaf = 0; leaf < _variableLeaves.Length; leaf++)
        {
            var e = CachedVariableLeafEnd(leaf, q);
            if (e >= 0 && targets.Contains(e) && MemberLeafOk(_variableLeaves[leaf], q)) return true;
        }

        if (depth >= _maxDepth) return false;
        foreach (var e in StructEnds(q, depth))
            if (targets.Contains(e))
                return true;
        foreach (var e in ArrayEnds(q, depth))
            if (targets.Contains(e))
                return true;
        return false;
    }

    /// <summary>Ends of a struct value at depth <paramref name="depth"/> (0: a value of the row)</summary>
    private int[] StructEnds(int q, int depth)
    {
        var memo = _structEnds[depth];
        if (memo.TryGetValue(q, out var cached)) return cached;

        int[] result = [];
        if (TryHeader(q, null, out var headerEnd, out var values, true))
        {
            var reach = new PositionSet(headerEnd);
            reach.Add(headerEnd);
            for (var i = 0; i < values && !reach.IsEmpty; i++)
                reach = Step(reach, depth + 1);
            result = reach.ToList().ToArray();
        }

        memo[q] = result;
        return result;
    }

    /// <summary>Ends of an array value at depth <paramref name="depth"/>: count, then elements of one shape (no arrays)</summary>
    private int[] ArrayEnds(int q, int depth)
    {
        var memo = _arrayEnds[depth];
        if (memo.TryGetValue(q, out var cached)) return cached;

        var count = ArrayCount(q);
        var ends = new List<int>();
        if (count > 0)
        {
            var first = q + 4;
            foreach (var size in _fixedSizes)
            {
                var e = first + (long) count * size;
                if (e <= _end) ends.Add((int) e);
            }

            var p = first;
            for (var i = 0; i < count && p >= 0; i++) p = NameEnd(p);
            if (p >= 0) ends.Add(p);

            for (var leaf = 0; leaf < _variableLeaves.Length; leaf++)
            {
                p = first;
                for (var i = 0; i < count && p >= 0; i++) p = CachedVariableLeafEnd(leaf, p);
                if (p >= 0) ends.Add(p);
            }
            Spend(count);

            if (_profile.ArrayStructs && depth + 1 < _maxDepth)
            {
                var reach = new PositionSet(first);
                reach.Add(first);
                for (var i = 0; i < count && !reach.IsEmpty; i++)
                {
                    var next = new PositionSet(first);
                    var positions = reach.ToList();
                    Spend(positions.Count);
                    foreach (var r in positions)
                    foreach (var e in StructEnds(r, depth + 1))
                        next.Add(e);
                    reach = next;
                }
                ends.AddRange(reach.ToList());
            }
        }

        var result = ends.ToArray();
        memo[q] = result;
        return result;
    }

    /// <summary>
    /// Forward: every start the first rows can have and where each of their values can end.
    /// Backward: keeps the positions that can still reach the end of the window (the end of the data when the window
    /// covers the table, otherwise a valid row start).
    /// </summary>
    private bool BuildRelaxation()
    {
        _window = _end <= WholeTableBytes ? _numRows : Math.Min(_numRows, WindowRows);
        // the reach of a row only depends on where it starts, and the rows of a window share most of their candidate starts
        var reachByStart = new Dictionary<int, PositionSet[]?>();
        var forward = new List<List<int>>();
        var starts = new HashSet<int> { 0 };
        for (var r = 0; r < _window; r++)
        {
            var states = new List<int>();
            var nextStarts = new HashSet<int>();
            foreach (var s in starts)
            {
                if (!reachByStart.TryGetValue(s, out var reach))
                    reachByStart[s] = reach = RowReach(s);
                if (reach is null) continue;

                states.Add(s);
                if (r + 1 < _numRows)
                {
                    foreach (var e in reach[^1].ToList())
                        if (TryRowHead(e, null, out _, out _))
                            nextStarts.Add(e);
                }
            }

            forward.Add(states);
            if (r + 1 < _numRows)
            {
                if (nextStarts.Count == 0) return false;
                if (nextStarts.Count > MaxRowStarts) throw new InferenceAbortedException("Too many possible row starts");
            }
            starts = nextStarts;
        }

        _good = new Dictionary<int, PositionSet[]>[_window];
        for (var r = _window - 1; r >= 0; r--)
        {
            _good[r] = [];
            foreach (var s in forward[r])
            {
                var reach = reachByStart[s]!;
                var n = reach.Length - 1;
                var headerEnd = reach[0].Base;
                var good = new PositionSet[n + 1];
                good[n] = new PositionSet(headerEnd);
                foreach (var e in reach[n].ToList())
                {
                    var reachesGoal = r + 1 == _numRows
                        ? e == _end
                        : r + 1 == _window
                            ? starts.Contains(e)
                            : _good[r + 1].ContainsKey(e);
                    if (reachesGoal) good[n].Add(e);
                }

                for (var k = n - 1; k >= 0; k--)
                {
                    good[k] = new PositionSet(headerEnd);
                    if (good[k + 1].IsEmpty) continue;
                    var positions = reach[k].ToList();
                    Spend(positions.Count);
                    foreach (var q in positions)
                        if (AnyEndIn(q, 0, good[k + 1]))
                            good[k].Add(q);
                }

                if (good[0].Contains(headerEnd))
                    _good[r][s] = good;
            }

            if (_good[r].Count == 0) return false;
        }

        return _good[0].ContainsKey(0);
    }

    /// <summary>Where each value of a row starting at <paramref name="start"/> can end, null if it can't be read</summary>
    private PositionSet[]? RowReach(int start)
    {
        if (!TryRowHead(start, null, out var headerEnd, out var n)) return null;
        var reach = new PositionSet[n + 1];
        reach[0] = new PositionSet(headerEnd);
        reach[0].Add(headerEnd);
        for (var k = 0; k < n; k++)
        {
            reach[k + 1] = Step(reach[k], 0);
            if (reach[k + 1].IsEmpty) return null;
        }
        return reach;
    }

    private bool StillGood(int row, int rowStart, int k, int pos) =>
        row >= _window || _good[row][rowStart][k].Contains(pos);

    #endregion

    #region Exact search

    private abstract class Work(Work? next)
    {
        public readonly Work? Next = next;
    }

    private sealed class RowWork(int row) : Work(null)
    {
        public readonly int Row = row;
    }

    private sealed class ValueWork(int path, int row, int rowStart, int k, Work? next) : Work(next)
    {
        public readonly int Path = path;
        public readonly int Row = row;
        public readonly int RowStart = rowStart;
        public readonly int K = k; // index among the serialized values of the row, -1 when nested
    }

    private sealed class CheckWork(int row, int rowStart, int k, Work? next) : Work(next)
    {
        public readonly int Row = row;
        public readonly int RowStart = rowStart;
        public readonly int K = k;
    }

    private int Child(int parent, int index)
    {
        var key = Key(parent, index);
        if (_children.TryGetValue(key, out var id)) return id;

        id = _pathIndex.Count;
        if (id >= MaxPaths) throw new InferenceAbortedException("Too many properties");
        _pathIndex.Add(index);
        _pathDepth.Add(_pathDepth[parent] + 1);
        _assigned.Add(-1);
        _children[key] = id;
        return id;
    }

    /// <summary>Same limits as the relaxation: a value of the row is at depth 1</summary>
    private bool ShapeAllowed(int path, ValueShape shape)
    {
        if (!IsComposite(shape)) return true;
        var depth = _pathDepth[path];
        if (_pathIndex[path] == -1) // element of an array: no array of arrays
            return shape == ValueShape.Struct && _profile.ArrayStructs && depth <= _maxDepth;
        return depth <= _maxDepth;
    }

    private int ValueEnd(int path, ValueShape shape, int pos) =>
        _pathIndex[path] == -1 ? LeafEnd(shape, pos) : MemberLeafEnd(shape, pos);

    private void Record(int path, int pos, int row)
    {
        if (!_samples.TryGetValue(path, out var list))
            _samples[path] = list = [];
        if (list.Count < MaxSamples) list.Add((pos, row));
    }

    private void RecordIndices(int path, List<(int Index, bool NonZero)> values)
    {
        if (!_observedIndices.TryGetValue(path, out var set))
            _observedIndices[path] = set = [];
        foreach (var (index, _) in values)
            set.Add(index);
    }

    /// <summary>
    /// Parses from <paramref name="pos"/> what <paramref name="work"/> describes, guessing the shape of a schema index the
    /// first time it is met. Only the guesses recurse; the rest is a loop, so a long table doesn't deepen the stack.
    /// </summary>
    /// <param name="divergent">a value started where none of the first layout did</param>
    /// <param name="valueCount">values read so far, to tell a layout with fewer boundaries than the first one</param>
    private bool Search(int pos, Work work, bool divergent, int valueCount)
    {
        var current = work;
        while (true)
        {
            Spend();
            switch (current)
            {
                case RowWork rowWork:
                {
                    var row = rowWork.Row;
                    if (row == _numRows) return pos == _end && (!_seekAlternative || divergent || valueCount != _valueStarts.Count);
                    if (row < _window && !_good[row].ContainsKey(pos)) return false;

                    var values = new List<(int Index, bool NonZero)>();
                    if (!TryRowHead(pos, values, out var headerEnd, out var nonZero)) return false;
                    if (_collect)
                    {
                        _rowStarts.Add(pos);
                        RecordIndices(0, values);
                    }

                    Work next = new RowWork(row + 1);
                    var k = nonZero;
                    for (var i = values.Count - 1; i >= 0; i--)
                    {
                        if (!values[i].NonZero) continue;
                        k--;
                        next = new ValueWork(Child(0, values[i].Index), row, pos, k, next);
                    }

                    pos = headerEnd;
                    current = next;
                    continue;
                }
                case CheckWork check:
                    if (!StillGood(check.Row, check.RowStart, check.K, pos)) return false;
                    current = check.Next;
                    continue;
                case ValueWork value:
                {
                    var assigned = _assigned[value.Path];
                    if (assigned >= 0)
                    {
                        if (_collect) _valueStarts.Add((value.Row, pos));
                        else if (_seekAlternative)
                        {
                            valueCount++;
                            if (!divergent && !_valueStarts.Contains((value.Row, pos))) divergent = true;
                        }
                    }

                    if (assigned < 0)
                    {
                        foreach (var candidate in _shapes)
                        {
                            if (!ShapeAllowed(value.Path, candidate)) continue;
                            if (!IsComposite(candidate))
                            {
                                var e = ValueEnd(value.Path, candidate, pos);
                                if (e < 0 || (value.K >= 0 && !StillGood(value.Row, value.RowStart, value.K + 1, e))) continue;
                            }

                            _assigned[value.Path] = (sbyte) candidate;
                            if (Search(pos, value, divergent, valueCount)) return true;
                            _assigned[value.Path] = -1;
                        }

                        return false;
                    }

                    var shape = (ValueShape) assigned;
                    if (_collect) Record(value.Path, pos, value.Row);

                    if (!IsComposite(shape))
                    {
                        var e = ValueEnd(value.Path, shape, pos);
                        if (e < 0 || (value.K >= 0 && !StillGood(value.Row, value.RowStart, value.K + 1, e))) return false;
                        pos = e;
                        current = value.Next;
                        continue;
                    }

                    var continuation = value.K >= 0 && value.Row < _window
                        ? new CheckWork(value.Row, value.RowStart, value.K + 1, value.Next)
                        : value.Next;

                    if (shape == ValueShape.Struct)
                    {
                        var values = new List<(int Index, bool NonZero)>();
                        if (!TryHeader(pos, values, out var headerEnd, out _, true)) return false;
                        if (_collect) RecordIndices(value.Path, values);

                        for (var i = values.Count - 1; i >= 0; i--)
                        {
                            if (!values[i].NonZero) continue;
                            continuation = new ValueWork(Child(value.Path, values[i].Index), value.Row, value.RowStart, -1, continuation);
                        }

                        pos = headerEnd;
                        current = continuation;
                        continue;
                    }

                    var count = ArrayCount(pos);
                    if (count < 0) return false;
                    var element = Child(value.Path, -1);
                    for (var i = 0; i < count; i++)
                        continuation = new ValueWork(element, value.Row, value.RowStart, -1, continuation);

                    pos += 4;
                    current = continuation;
                    continue;
                }
                default:
                    return pos == _end && (!_seekAlternative || divergent || valueCount != _valueStarts.Count);
            }
        }
    }

    #endregion
}
