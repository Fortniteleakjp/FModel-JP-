using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;

namespace CUE4Parse.UE4.Objects.Engine.Animation;

public sealed class FAnimNodePropertyData
{
    public string Name { get; init; } = string.Empty;
    public string StructName { get; init; } = string.Empty;
    public int ChildPropertyIndex { get; init; } = -1;
    internal bool IsAnimNode { get; init; }
}

public sealed class FAnimNodePropertyCollection
{
    public string Name { get; init; } = string.Empty;
    public string StructName { get; init; } = string.Empty;
    public int ChildPropertyIndex { get; init; } = -1;
    public FStructFallback? Value { get; init; }
}

public sealed class FAnimBlueprintFunction
{
    public string Name { get; init; } = string.Empty;
    public bool HasOutputPose { get; init; }
}

public class UAnimBlueprintGeneratedClass : UBlueprintGeneratedClass
{
    private FAnimNodePropertyData[]? _animNodePropertyData;
    private FAnimBlueprintFunction[]? _animBlueprintFunctions;

    public FAnimNodePropertyData[] AnimNodePropertyData => _animNodePropertyData ??= BuildAnimNodePropertyData();

    public FAnimBlueprintFunction[] AnimBlueprintFunctions => _animBlueprintFunctions ??= BuildAnimBlueprintFunctions();

    public bool TryGetAnimNodeProperties(int animNodePropertyIndex, out FAnimNodePropertyCollection properties)
    {
        if (animNodePropertyIndex < 0 || animNodePropertyIndex >= AnimNodePropertyData.Length)
        {
            properties = null!;
            return false;
        }

        var data = AnimNodePropertyData[animNodePropertyIndex];
        FStructFallback? value = null;
        var cdo = ClassDefaultObject.Load();
        if (cdo.TryGetValue(out FStructFallback fallback, data.Name))
            value = fallback;

        properties = new FAnimNodePropertyCollection
        {
            Name = data.Name,
            StructName = data.StructName,
            ChildPropertyIndex = data.ChildPropertyIndex,
            Value = value
        };
        return true;
    }

    public bool TryGetRootNodePropertyForFunction(string functionName, out FAnimNodePropertyData? rootProperty)
    {
        rootProperty = null;
        if (!FuncMap.TryGetValue(functionName, out var functionIndex) ||
            !functionIndex.TryLoad(out var export) ||
            export is not UFunction function ||
            !TryGetPoseOutputName(function, out var outputName))
        {
            return false;
        }

        var cdo = ClassDefaultObject.Load();
        if (!cdo.TryGetValue(out FStructFallback functionValue, functionName) ||
            !functionValue.TryGetValue(out FStructFallback outputValue, outputName) ||
            !outputValue.TryGetValue(out int linkId, "LinkID"))
        {
            return false;
        }

        var data = AnimNodePropertyData;
        var candidates = new List<FAnimNodePropertyData?>();
        if (linkId >= 0 && linkId < data.Length)
            candidates.Add(data[linkId]);

        candidates.Add(data.FirstOrDefault(item => item.ChildPropertyIndex == linkId));
        if (linkId >= 0 && linkId < data.Length)
            candidates.Add(data[data.Length - 1 - linkId]);

        var reversedChildPropertyIndex = (ChildProperties?.Length ?? 0) - 1 - linkId;
        if (reversedChildPropertyIndex >= 0)
            candidates.Add(data.FirstOrDefault(item => item.ChildPropertyIndex == reversedChildPropertyIndex));

        foreach (var candidate in candidates.Distinct())
        {
            if (candidate?.StructName.EndsWith("_Root", StringComparison.OrdinalIgnoreCase) != true)
                continue;

            rootProperty = candidate;
            return true;
        }

        return false;
    }

    private FAnimNodePropertyData[] BuildAnimNodePropertyData()
    {
        return (ChildProperties ?? [])
            .Select((field, index) => (field, index))
            .Where(static item => item.field is FStructProperty)
            .Select(static item =>
            {
                var property = (FStructProperty)item.field;
                var structName = property.Struct.ResolvedObject?.Name.Text ?? string.Empty;
                return new FAnimNodePropertyData
                {
                    Name = item.field.Name.Text,
                    StructName = structName,
                    ChildPropertyIndex = item.index,
                    IsAnimNode = IsAnimNodeStruct(structName, item.field.Name.Text)
                };
            })
            .Where(static data => data.IsAnimNode)
            .ToArray();
    }

    private FAnimBlueprintFunction[] BuildAnimBlueprintFunctions()
    {
        return (FuncMap ?? [])
            .Select(pair => (pair.Key.Text, pair.Value))
            .Where(pair => pair.Value.TryLoad(out var export) && export is UFunction)
            .Select(pair =>
            {
                pair.Value.TryLoad(out var export);
                return new FAnimBlueprintFunction
                {
                    Name = pair.Text,
                    HasOutputPose = export is UFunction function && TryGetPoseOutputName(function, out _)
                };
            })
            .ToArray();
    }

    private static bool IsAnimNodeStruct(string structName, string fieldName)
        => structName.StartsWith("FAnimNode_", StringComparison.OrdinalIgnoreCase) ||
           structName.StartsWith("AnimNode_", StringComparison.OrdinalIgnoreCase) ||
           fieldName.StartsWith("AnimNode_", StringComparison.OrdinalIgnoreCase) ||
           fieldName.StartsWith("AnimGraphNode_", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetPoseOutputName(UFunction function, out string outputName)
    {
        foreach (var childProperty in function.ChildProperties ?? [])
        {
            if (childProperty is not FStructProperty structProperty ||
                !structProperty.PropertyFlags.HasFlag(EPropertyFlags.OutParm) ||
                structProperty.PropertyFlags.HasFlag(EPropertyFlags.ReturnParm))
            {
                continue;
            }

            var structName = structProperty.Struct.ResolvedObject?.Name.Text ?? string.Empty;
            if (structName.Contains("PoseLink", StringComparison.OrdinalIgnoreCase))
            {
                outputName = childProperty.Name.Text;
                return true;
            }
        }

        outputName = string.Empty;
        return false;
    }
}
