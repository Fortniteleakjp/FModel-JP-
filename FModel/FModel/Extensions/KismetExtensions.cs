using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Engine.Ai;
using CUE4Parse.UE4.Objects.Engine.GameFramework;
using CUE4Parse.UE4.Objects.GameplayTags;
using CUE4Parse.UE4.Objects.UObject;

namespace FModel.Extensions;

public static class KismetExtensions
{
    public static string GetPrefix(string type, string extra = "") // todo: implement better handling
    {
        return type switch
        {
            "FNameProperty" or "FPackageIndex" or "FTextProperty" or "FStructProperty" => "F",
            "UBlueprintGeneratedClass" or "FActorProperty" => "A",
            "FObjectProperty" when extra.Contains("Actor") => "A",
            "ResolvedScriptObject" or "ResolvedLoadedObject" or "FSoftObjectProperty" or "FObjectProperty" => "U",
            _ => ""
        };
    }

    // GetUnknownFieldType and GetPropertyType from
    // https://github.com/CrystalFerrai/UeBlueprintDumper/blob/main/UeBlueprintDumper/BlueprintDumper.cs#L352
    // nothing else is from UeBlueprintDumper

    public static string GetUnknownFieldType(object field)
    {
        string typeName = field.GetType().Name;
        int suffixIndex = typeName.IndexOf("Property", StringComparison.Ordinal);
        return suffixIndex < 0 ? typeName : typeName.Substring(1, suffixIndex - 1);
    }

    public static string GetPropertyType(object? property)
    {
        if (property is null)
            return "None";

        return property switch
        {
            FIntProperty or int => "int",
            FInt8Property or byte => "int8",
            FInt16Property or short => "int16",
            FInt64Property or long => "int64",
            FUInt16Property or ushort => "uint16",
            FUInt32Property or uint => "uint32",
            FUInt64Property or ulong => "uint64",
            FBoolProperty or bool => "bool",
            FStrProperty or string => "FString",
            FFloatProperty or float => "float",
            FDoubleProperty or double => "double",
            FObjectProperty objct => property switch
            {
                FClassProperty clss => $"{clss.MetaClass?.Name ?? "UKN_ObjectMetaClass"} Class",
                FSoftClassProperty softClass => $"{softClass.MetaClass?.Name ?? "UKN_ObjectMetaClass"} Class (soft)",
                _ => objct.PropertyClass?.Name ?? "UKN_ObjectPropertyClass"
            },
            FPackageIndex pkg => pkg.ResolvedObject?.Class?.Name.ToString() ?? "Package",
            FName fme => fme.PlainText.Contains("::") ? fme.PlainText.Split("::")[0] : fme.PlainText,
            FEnumProperty enm => enm.Enum?.Name ?? "Enum",
            FByteProperty bt => bt.Enum?.ResolvedObject?.Name.Text ?? "Byte",
            FInterfaceProperty intrfc => $"{intrfc.InterfaceClass?.Name ?? "UKN_InterfaceClass"} interface",
            FStructProperty strct => strct.Struct?.ResolvedObject?.Name.Text ?? "Struct",
            FFieldPathProperty fieldPath => $"{fieldPath.PropertyClass.Text} field path",
            FDelegateProperty dlgt => $"{dlgt.SignatureFunction?.Name ?? "UKN_SignatureFunction"} (Delegate)",
            FMulticastDelegateProperty mdlgt => $"{mdlgt.SignatureFunction?.Name ?? "UKN_SignatureFunction"} (MulticastDelegateProperty)",
            FMulticastInlineDelegateProperty midlgt => $"{midlgt.SignatureFunction?.Name ?? "UKN_SignatureFunction"} (MulticastInlineDelegateProperty)",
            _ => GetUnknownFieldType(property)
        };
    }
    public static string GetPropertyType(FProperty? property)
    {
        if (property is null) return "None";

        bool isPointer(FProperty p) =>
            p.PropertyFlags.HasFlag(EPropertyFlags.InstancedReference) ||
            property.PropertyFlags.HasFlag(EPropertyFlags.ReferenceParm) ||
            p.PropertyFlags.HasFlag(EPropertyFlags.ContainsInstancedReference);

        return property switch
        {
            FSetProperty s => $"TSet<{GetPrefix(s.ElementProp.GetType().Name)}{GetPropertyType(s.ElementProp)}{(isPointer(s) ? "*" : "")}>",
            FMapProperty m => $"TMap<{GetPrefix(m.KeyProp.GetType().Name)}{GetPropertyType(m.KeyProp)}, {GetPrefix(m.ValueProp.GetType().Name)}{GetPropertyType(m.ValueProp)}{(isPointer(m) ? "*" : "")}>",
            FArrayProperty a => $"TArray<{GetPrefix(a.Inner.GetType().Name)}{GetPropertyType(a.Inner)}{(isPointer(a) || GetPropertyProperty(a.Inner.GetType().Name) ? "*" : "")}>",
            _ => GetPropertyType((object)property)
        };
    }

    public static bool GetPropertyProperty(object? property)
    {
        if (property is null)
            return false;

        return property switch
        {
            FObjectProperty => true,
            _ => false
        };
    }

    public static string FormatStructFallback(FStructFallback fallback)
    {
        if (fallback.Properties.Count == 0)
            return "[]";

        var tags = fallback.Properties.Select(tag =>
        {
            string tagDataFormatted;

            switch (tag.Tag)
            {
                case TextProperty text:
                    tagDataFormatted = $"\"{text.Value.Text}\"";
                    break;
                case NameProperty name:
                    tagDataFormatted = $"\"{name.Value.Text}\"";
                    break;
                case ObjectProperty obj:
                    tagDataFormatted = $"\"{obj.Value}\"";
                    break;
                default:
                {
                    if (tag.Tag.GenericValue is FScriptStruct { StructType: FStructFallback nestedFallback })
                    {
                        if (nestedFallback.Properties.Count > 0)
                        {
                            tagDataFormatted = "{ " + string.Join(", ",
                                nestedFallback.Properties.Select(nested =>
                                {
                                    string nestedVal = nested.Tag switch
                                    {
                                        TextProperty textProp => $"\"{textProp.Value.Text}\"",
                                        NameProperty nameProp => $"\"{nameProp.Value.Text}\"",
                                        _ => $"\"{nested.Tag.GenericValue}\""
                                    };

                                    return $"\"{nested.Name}\": {nestedVal}";
                                })) + " }";
                        }
                        else
                        {
                            tagDataFormatted = "{}";
                        }
                    }
                    else
                    {
                        tagDataFormatted = tag.Tag.GenericValue != null ? $"\"{tag.Tag.GenericValue}\"" : "{}";
                    }

                    break;
                }
            }

            return $"\t\t{{ \"{tag.Name}\": {tagDataFormatted} }}";
        });

        return "[\n" + string.Join(",\n", tags) + "\n\t]";
    }

    public static string FormatGameplayTagContainer(FGameplayTagContainer container)
    {
        var tags = container.GameplayTags.ToList();
        return tags.Count switch
        {
            0 => "[]",
            1 => $"\"{tags[0].TagName}\"",
            _ => "[\n" + string.Join(",\n", tags.Select(tag => $"\t\t\"{tag.TagName}\"")) + "\n\t]"
        };
    }

    public static string FormatStructType(object structType)
    {
        return structType switch
        {
            FVector vector => $"FVector({vector.X}, {vector.Y}, {vector.Z})",
            FVector2D vector2D => $"FVector2D({vector2D.X}, {vector2D.Y})",
            FRotator rotator => $"FRotator({rotator.Pitch}, {rotator.Yaw}, {rotator.Roll})",
            FQuat quat => $"FQuat({quat.X}, {quat.Y}, {quat.Z}, {quat.W})",
            FGuid guid => $"FGuid({guid.A}, {guid.B}, {guid.C}, {guid.D})",
            FColor color => $"FColor({color.R}, {color.G}, {color.B}, {color.A})",
            FLinearColor linearColor => $"FLinearColor({linearColor.R}, {linearColor.G}, {linearColor.B}, {linearColor.A})",
            FSoftObjectPath path => $"FSoftObjectPath({path.AssetPathName})",
            FUniqueNetIdRepl netId => $"FUniqueNetIdRepl({netId.UniqueNetId})",
            FNavAgentSelector agent => $"FNavAgentSelector({agent.PackedBits})",
            FBox box => $"FBox(FVector({box.Max.X}, {box.Max.Y}, {box.Max.Z}), FVector({box.Min.X}, {box.Min.Y}, {box.Min.Z}))",
            FBox2D box2D => $"FBox2D(FVector2D({box2D.Max.X}, {box2D.Max.Y}), FVector2D({box2D.Min.X}, {box2D.Min.Y}))",
            TIntVector3<int> intVec => $"FVector({intVec.X}, {intVec.Y}, {intVec.Z})",
            TIntVector3<float> floatVec => $"FVector({floatVec.X}, {floatVec.Y}, {floatVec.Z})",
            TIntVector2<float> floatVec2 => $"FVector2D({floatVec2.X}, {floatVec2.Y})",
            FDateTime dateTime => $"FDateTime({dateTime})",
            FStructFallback fallback => FormatStructFallback(fallback),
            FGameplayTagContainer tagContainer => FormatGameplayTagContainer(tagContainer),
            _ => structType?.ToString() ?? "Issue here"
        };
    }

    private static string ProcessTextProperty(FKismetPropertyPointer property, bool temp)
    {
        if (property.New is null)
        {
            return property.Old?.Name ?? string.Empty;
        }
        return string.Join('.', property.New.Path.Select(n => n.Text)).Replace(" ", "");
    }

    public static void ProcessExpression(EExprToken token, KismetExpression expression, StringBuilder outputBuilder, List<int> jumpCodeOffsets, bool isParameter = false)
    {
        if (jumpCodeOffsets.Contains(expression.StatementIndex))
        {
            outputBuilder.Append("\t\tLabel_" + expression.StatementIndex + ":\n");
        }

        switch (token)
        {
            case EExprToken.EX_LetValueOnPersistentFrame:
                {
                    EX_LetValueOnPersistentFrame op = (EX_LetValueOnPersistentFrame) expression;
                    EX_VariableBase opp = (EX_VariableBase) op.AssignmentExpression;
                    var destination = ProcessTextProperty(op.DestinationProperty, false);
                    var variable = ProcessTextProperty(opp.Variable, false);

                    outputBuilder.Append($"\t\t{(destination.Contains("K2Node_") ? "UberGraphFrame->" + destination : destination)} = {variable}{(!isParameter ? ";\n\n" : "")}"); // Hardcoded but works
                    break;
                }
            case EExprToken.EX_LocalFinalFunction:
                {
                    EX_FinalFunction op = (EX_FinalFunction) expression;
                    KismetExpression[] opp = op.Parameters;
                    if (isParameter)
                    {
                        outputBuilder.Append($"{op.StackNode.Name.Replace(" ", "")}(");
                    }
                    else if (opp.Length < 1)
                    {
                        outputBuilder.Append($"\t\t{op?.StackNode?.Name.Replace(" ", "")}(");
                    }
                    else
                    {
                        outputBuilder.Append($"\t\t{GetPrefix(op?.StackNode?.ResolvedObject?.Outer?.GetType()?.Name ?? string.Empty)}{op?.StackNode?.Name.Replace(" ", "")}(");
                    }

                    for (int i = 0; i < opp.Length; i++)
                    {
                        if (opp.Length > 4)
                            outputBuilder.Append("\n\t\t");
                        ProcessExpression(opp[i].Token, opp[i], outputBuilder, jumpCodeOffsets, true);
                        if (i < opp.Length - 1)
                        {
                            outputBuilder.Append(", ");
                        }
                    }
                    outputBuilder.Append(isParameter ? ")" : ");\n");
                    break;
                }
            case EExprToken.EX_FinalFunction:
                {
                    EX_FinalFunction op = (EX_FinalFunction) expression;
                    KismetExpression[] opp = op.Parameters;
                    if (isParameter)
                    {
                        outputBuilder.Append($"{op.StackNode.Name.Replace(" ", "")}(");
                    }
                    else if (opp.Length < 1)
                    {
                        outputBuilder.Append($"\t\t{op?.StackNode?.Name.Replace(" ", "")}(");
                    }
                    else
                    {
                        outputBuilder.Append($"\t\t{op?.StackNode?.Name.Replace(" ", "")}(");//{GetPrefix(op?.StackNode?.ResolvedObject?.Outer?.GetType()?.Name)}
                    }

                    for (int i = 0; i < opp.Length; i++)
                    {
                        if (opp.Length > 4)
                            outputBuilder.Append("\n\t\t");
                        ProcessExpression(opp[i].Token, opp[i], outputBuilder, jumpCodeOffsets, true);
                        if (i < opp.Length - 1)
                        {
                            outputBuilder.Append(", ");
                        }
                    }
                    outputBuilder.Append(isParameter ? ")" : ");\n\n");
                    break;
                }
            case EExprToken.EX_CallMath:
                {
                    EX_FinalFunction op = (EX_FinalFunction) expression;
                    KismetExpression[] opp = op.Parameters;
                    outputBuilder.Append(isParameter ? string.Empty : "\t\t");
                    outputBuilder.Append($"{GetPrefix(op.StackNode.ResolvedObject.Outer.GetType().Name)}{op.StackNode.ResolvedObject.Outer.Name.ToString().Replace(" ", "")}::{op.StackNode.Name}(");

                    for (int i = 0; i < opp.Length; i++)
                    {
                        if (opp.Length > 4)
                            outputBuilder.Append("\n\t\t\t");
                        ProcessExpression(opp[i].Token, opp[i], outputBuilder, jumpCodeOffsets, true);
                        if (i < opp.Length - 1)
                        {
                            outputBuilder.Append(", ");
                        }
                    }
                    outputBuilder.Append(isParameter ? ")" : ");\n\n");
                    break;
                }
            case EExprToken.EX_LocalVirtualFunction:
            case EExprToken.EX_VirtualFunction:
                {
                    EX_VirtualFunction op = (EX_VirtualFunction) expression;
                    KismetExpression[] opp = op.Parameters;
                    if (isParameter)
                    {
                        outputBuilder.Append($"{op.VirtualFunctionName.PlainText.Replace(" ", "")}(");
                    }
                    else
                    {
                        outputBuilder.Append($"\t\t{op.VirtualFunctionName.PlainText.Replace(" ", "")}(");
                    }
                    for (int i = 0; i < opp.Length; i++)
                    {
                        if (opp.Length > 4)
                            outputBuilder.Append("\n\t\t");

                        ProcessExpression(opp[i].Token, opp[i], outputBuilder, jumpCodeOffsets, true);
                        if (i < opp.Length - 1)
                        {
                            outputBuilder.Append(", ");
                        }
                    }
                    outputBuilder.Append(isParameter ? ")" : ");\n\n");
                    break;
                }
            case EExprToken.EX_ComputedJump:
                {
                    EX_ComputedJump op = (EX_ComputedJump) expression;
                    if (op.CodeOffsetExpression is EX_VariableBase opp)
                    {
                        outputBuilder.AppendLine($"\t\tgoto {ProcessTextProperty(opp.Variable, false)};\n");
                    }
                    else if (op.CodeOffsetExpression is EX_CallMath oppMath)
                    {
                        ProcessExpression(oppMath.Token, oppMath, outputBuilder, jumpCodeOffsets, true);
                    }

                    break;
                }
            case EExprToken.EX_PopExecutionFlowIfNot:
                {
                    EX_PopExecutionFlowIfNot op = (EX_PopExecutionFlowIfNot) expression;
                    outputBuilder.Append("\t\tif (!");
                    ProcessExpression(op.BooleanExpression.Token, op.BooleanExpression, outputBuilder, jumpCodeOffsets, true);
                    outputBuilder.Append(") \r\n");
                    outputBuilder.Append($"\t\t    FlowStack.Pop();\n\n");
                    break;
                }
            case EExprToken.EX_Cast:
                {
                    EX_Cast op = (EX_Cast) expression; // support CST_ObjectToInterface when I have an example of how it works

                    if (op.ConversionType is ECastToken.CST_ObjectToBool or ECastToken.CST_InterfaceToBool)
                    {
                        outputBuilder.Append("(bool)");
                    }
                    if (ECastToken.CST_DoubleToFloat == op.ConversionType)
                    {
                        outputBuilder.Append("(float)");
                    }
                    if (ECastToken.CST_FloatToDouble == op.ConversionType)
                    {
                        outputBuilder.Append("(double)");
                    }
                    ProcessExpression(op.Target.Token, op.Target, outputBuilder, jumpCodeOffsets);
                    break;
                }
            case EExprToken.EX_InterfaceContext:
                {
                    EX_InterfaceContext op = (EX_InterfaceContext) expression;
                    ProcessExpression(op.InterfaceValue.Token, op.InterfaceValue, outputBuilder, jumpCodeOffsets);
                    break;
                }
            case EExprToken.EX_ArrayConst:
                {
                    EX_ArrayConst op = (EX_ArrayConst) expression;
                    outputBuilder.Append("TArray {");
                    foreach (KismetExpression element in op.Elements)
                    {
                        outputBuilder.Append(' ');
                        ProcessExpression(element.Token, element, outputBuilder, jumpCodeOffsets);
                    }
                    outputBuilder.Append(op.Elements.Length < 1 ? "  " : ' ');

                    outputBuilder.Append('}');
                    break;
                }
            case EExprToken.EX_SetArray:
                {
                    EX_SetArray op = (EX_SetArray) expression;
                    outputBuilder.Append("\t\t");
                    ProcessExpression(op.AssigningProperty.Token, op.AssigningProperty, outputBuilder, jumpCodeOffsets);
                    outputBuilder.Append(" = ");
                    outputBuilder.Append("TArray {");
                    for (int i = 0; i < op.Elements.Length; i++)
                    {
                        KismetExpression element = op.Elements[i];
                        outputBuilder.Append(' ');
                        ProcessExpression(element.Token, element, outputBuilder, jumpCodeOffsets);

                        outputBuilder.Append(i < op.Elements.Length - 1 ? "," : "");
                    }

                    outputBuilder.Append(op.Elements.Length < 1 ? "  " : ' ');

                    outputBuilder.Append("};\n\n");
                    break;
                }
            case EExprToken.EX_SetSet:
                {
                    EX_SetSet op = (EX_SetSet) expression;
                    outputBuilder.Append("\t\t");
                    ProcessExpression(op.SetProperty.Token, op.SetProperty, outputBuilder, jumpCodeOffsets);
                    outputBuilder.Append(" = ");
                    outputBuilder.Append("TArray {");
                    for (int i = 0; i < op.Elements.Length; i++)
                    {
                        KismetExpression element = op.Elements[i];
                        outputBuilder.Append(' ');
                        ProcessExpression(element.Token, element, outputBuilder, jumpCodeOffsets);

                        outputBuilder.Append(i < op.Elements.Length - 1 ? "," : "");
                    }

                    outputBuilder.Append(op.Elements.Length < 1 ? "  " : ' ');

                    outputBuilder.Append("};\n\n");
                    break;
                }
            case EExprToken.EX_SetConst:
                {
                    EX_SetConst op = (EX_SetConst) expression;
                    outputBuilder.Append("TArray {");
                    for (int i = 0; i < op.Elements.Length; i++)
                    {
                        KismetExpression element = op.Elements[i];
                        outputBuilder.Append(' ');
                        ProcessExpression(element.Token, element, outputBuilder, jumpCodeOffsets, true);

                        outputBuilder.Append(i < op.Elements.Length - 1 ? "," : "");
                    }

                    outputBuilder.Append(op.Elements.Length < 1 ? "  " : ' ');

                    outputBuilder.Append("};\n\n");
                    break;
                }
            case EExprToken.EX_SetMap:
                {
                    EX_SetMap op = (EX_SetMap) expression;
                    outputBuilder.Append("\t\t");
                    ProcessExpression(op.MapProperty.Token, op.MapProperty, outputBuilder, jumpCodeOffsets);
                    outputBuilder.Append(" = ");
                    outputBuilder.Append("TMap {");
                    for (int i = 0; i < op.Elements.Length; i++)
                    {
                        var element = op.Elements[i];
                        outputBuilder.Append(' ');
                        ProcessExpression(element.Token, element, outputBuilder, jumpCodeOffsets); // sometimes the start of an array is a byte not a variable

                        if (i < op.Elements.Length - 1)
                        {
                            outputBuilder.Append(element.Token == EExprToken.EX_InstanceVariable ? ": " : ", ");
                        }
                        else
                        {
                            outputBuilder.Append(' ');
                        }
                    }

                    if (op.Elements.Length < 1)
                        outputBuilder.Append("  ");
                    outputBuilder.Append("}\n");
                    break;
                }
            case EExprToken.EX_MapConst:
                {
                    EX_MapConst op = (EX_MapConst) expression;
                    outputBuilder.Append("TMap {");
                    for (int i = 0; i < op.Elements.Length; i++)
                    {
                        var element = op.Elements[i];
                        outputBuilder.Append(' ');
                        ProcessExpression(element.Token, element, outputBuilder, jumpCodeOffsets, true); // sometimes the start of an array is a byte not a variable

                        if (i < op.Elements.Length - 1)
                        {
                            outputBuilder.Append(element.Token == EExprToken.EX_InstanceVariable ? ": " : ", ");
                        }
                        else
                        {
                            outputBuilder.Append(' ');
                        }
                    }

                    if (op.Elements.Length < 1)
                        outputBuilder.Append("  ");
                    outputBuilder.Append("}\n");
                    break;
                }
            case EExprToken.EX_SwitchValue:
                {
                    EX_SwitchValue op = (EX_SwitchValue) expression;

                    bool useTernary = op.Cases.Length <= 2 &&
                                      op.Cases.All(c => c.CaseIndexValueTerm.Token is EExprToken.EX_True or EExprToken.EX_False);

                    if (useTernary)
                    {
                        ProcessExpression(op.IndexTerm.Token, op.IndexTerm, outputBuilder, jumpCodeOffsets);
                        outputBuilder.Append(" ? ");

                        bool isFirst = true;
                        foreach (var caseItem in op.Cases.Where(c => c.CaseIndexValueTerm.Token == EExprToken.EX_True))
                        {
                            if (!isFirst)
                                outputBuilder.Append(" : ");

                            ProcessExpression(caseItem.CaseTerm.Token, caseItem.CaseTerm, outputBuilder, jumpCodeOffsets, true);
                            isFirst = false;
                        }

                        foreach (var caseItem in op.Cases.Where(c => c.CaseIndexValueTerm.Token == EExprToken.EX_False))
                        {
                            if (!isFirst)
                                outputBuilder.Append(" : ");

                            ProcessExpression(caseItem.CaseTerm.Token, caseItem.CaseTerm, outputBuilder, jumpCodeOffsets, true);
                        }
                    }
                    else
                    {
                        outputBuilder.Append("switch (");
                        ProcessExpression(op.IndexTerm.Token, op.IndexTerm, outputBuilder, jumpCodeOffsets);
                        outputBuilder.Append(")\n");
                        outputBuilder.Append("\t\t{\n");

                        foreach (var caseItem in op.Cases)
                        {
                            if (caseItem.CaseIndexValueTerm.Token == EExprToken.EX_IntConst)
                            {
                                int caseValue = ((EX_IntConst) caseItem.CaseIndexValueTerm).Value;
                                outputBuilder.Append($"\t\t\tcase {caseValue}:\n");
                            }
                            else
                            {
                                outputBuilder.Append("\t\t\tcase ");
                                ProcessExpression(caseItem.CaseIndexValueTerm.Token, caseItem.CaseIndexValueTerm, outputBuilder, jumpCodeOffsets);
                                outputBuilder.Append(":\n");
                            }

                            outputBuilder.Append("\t\t\t{\n");
                            outputBuilder.Append("\t\t\t    ");
                            ProcessExpression(caseItem.CaseTerm.Token, caseItem.CaseTerm, outputBuilder, jumpCodeOffsets);
                            outputBuilder.Append(";\n");
                            outputBuilder.Append("\t\t\t    break;\n");
                            outputBuilder.Append("\t\t\t}\n");
                        }

                        outputBuilder.Append("\t\t\tdefault:\n");
                        outputBuilder.Append("\t\t\t{\n");
                        outputBuilder.Append("\t\t\t    ");
                        ProcessExpression(op.DefaultTerm.Token, op.DefaultTerm, outputBuilder, jumpCodeOffsets);
                        outputBuilder.Append("\n\t\t\t}\n");

                        outputBuilder.Append("\t\t}");
                    }
                    break;
                }
            case EExprToken.EX_ArrayGetByRef: // I assume get array with index
                {
                    EX_ArrayGetByRef op = (EX_ArrayGetByRef) expression; // FortniteGame/Plugins/GameFeatures/FM/PilgrimCore/Content/Player/Components/BP_PilgrimPlayerControllerComponent.uasset
                    ProcessExpression(op.ArrayVariable.Token, op.ArrayVariable, outputBuilder, jumpCodeOffsets, true);
                    outputBuilder.Append('[');
                    ProcessExpression(op.ArrayIndex.Token, op.ArrayIndex, outputBuilder, jumpCodeOffsets);
                    outputBuilder.Append(']');
                    break;
                }
            case EExprToken.EX_MetaCast:
            case EExprToken.EX_DynamicCast:
            case EExprToken.EX_ObjToInterfaceCast:
            case EExprToken.EX_CrossInterfaceCast:
            case EExprToken.EX_InterfaceToObjCast:
                {
                    EX_CastBase op = (EX_CastBase) expression;
                    outputBuilder.Append($"Cast<U{op.ClassPtr.Name}*>(");// m?
                    ProcessExpression(op.Target.Token, op.Target, outputBuilder, jumpCodeOffsets, true);
                    outputBuilder.Append(')');
                    break;
                }
            case EExprToken.EX_StructConst:
                {
                    EX_StructConst op = (EX_StructConst) expression;
                    outputBuilder.Append($"{GetPrefix(op.Struct.GetType().Name)}{op.Struct.Name}");
                    outputBuilder.Append('(');
                    for (int i = 0; i < op.Properties.Length; i++)
                    {
                        var property = op.Properties[i];
                        ProcessExpression(property.Token, property, outputBuilder, jumpCodeOffsets);
                        if (i < op.Properties.Length - 1 && property.Token != EExprToken.EX_ArrayConst)
                            outputBuilder.Append(", ");
                    }
                    outputBuilder.Append(')');
                    break;
                }
            case EExprToken.EX_ObjectConst:
                {
                    EX_ObjectConst op = (EX_ObjectConst) expression;
                    outputBuilder.Append(!isParameter ? "\t\tFindObject<" : outputBuilder.ToString().EndsWith('\n') ? "\t\tFindObject<" : "FindObject<"); // please don't complain, I know this is bad but I MUST do it.
                    string classString = op.Value.ResolvedObject?.Class?.ToString().Replace("'", "");

                    if (classString?.Contains('.') == true)
                    {

                        outputBuilder.Append(GetPrefix(op?.Value?.ResolvedObject?.Class?.GetType().Name) + classString.Split('.')[1]);
                    }
                    else
                    {
                        outputBuilder.Append(GetPrefix(op?.Value?.ResolvedObject?.Class?.GetType().Name) + classString);
                    }
                    outputBuilder.Append(">(\"");
                    var resolvedObject = op?.Value?.ResolvedObject;
                    var outerString = resolvedObject?.Outer?.ToString()?.Replace("'", "") ?? "outerUnknown";
                    var outerClassString = resolvedObject?.Class?.ToString()?.Replace("'", "") ?? "outerClassUnknown";
                    var name = op?.Value?.Name ?? string.Empty;

                    outputBuilder.Append(outerString.Replace(outerClassString, "") + "." + name);
                    outputBuilder.Append("\")");
                    break;
                }
            case EExprToken.EX_BindDelegate:
                {
                    EX_BindDelegate op = (EX_BindDelegate) expression;
                    outputBuilder.Append("\t\t");
                    ProcessExpression(op.Delegate.Token, op.Delegate, outputBuilder, jumpCodeOffsets);
                    outputBuilder.Append(".BindUFunction(");
                    ProcessExpression(op.ObjectTerm.Token, op.ObjectTerm, outputBuilder, jumpCodeOffsets);
                    outputBuilder.Append($", \"{op.FunctionName}\"");
                    outputBuilder.Append(");\n\n");
                    break;
                }
            // all the delegate functions suck
            case EExprToken.EX_AddMulticastDelegate:
                {
                    EX_AddMulticastDelegate op = (EX_AddMulticastDelegate) expression;
                    if (op.Delegate.Token is EExprToken.EX_LocalVariable or EExprToken.EX_InstanceVariable)
                    {
                        outputBuilder.Append("\t\t");
                        ProcessExpression(op.Delegate.Token, op.Delegate, outputBuilder, jumpCodeOffsets, true);
                        outputBuilder.Append(".AddDelegate(");
                        ProcessExpression(op.DelegateToAdd.Token, op.DelegateToAdd, outputBuilder, jumpCodeOffsets);
                        outputBuilder.Append(");\n\n");
                    }
                    else if (op.Delegate.Token != EExprToken.EX_Context)
                    {}
                    else
                    {
                        //EX_Context opp = (EX_Context) op.Delegate;
                        outputBuilder.Append("\t\t");
                        ProcessExpression(op.Delegate.Token, op.Delegate, outputBuilder, jumpCodeOffsets, true);
                        //outputBuilder.Append("->");
                        //ProcessExpression(opp.ContextExpression.Token, opp.ContextExpression, outputBuilder, jumpCodeOffsets);
                        outputBuilder.Append(".AddDelegate(");
                        ProcessExpression(op.DelegateToAdd.Token, op.DelegateToAdd, outputBuilder, jumpCodeOffsets);
                        outputBuilder.Append(");\n\n");
                    }
                    break;
                }
            case EExprToken.EX_RemoveMulticastDelegate: // everything here has been guessed not compared to actual UE but does work fine and displays all information
                {
                    EX_RemoveMulticastDelegate op = (EX_RemoveMulticastDelegate) expression;
                    if (op.Delegate.Token is EExprToken.EX_LocalVariable or EExprToken.EX_InstanceVariable)
                    {
                        outputBuilder.Append("\t\t");
                        ProcessExpression(op.Delegate.Token, op.Delegate, outputBuilder, jumpCodeOffsets, true);
                        outputBuilder.Append(".RemoveDelegate(");
                        ProcessExpression(op.DelegateToAdd.Token, op.DelegateToAdd, outputBuilder, jumpCodeOffsets);
                        outputBuilder.Append(");\n\n");
                    }
                    else if (op.Delegate.Token != EExprToken.EX_Context)
                    {

                    }
                    else
                    {
                        EX_Context opp = (EX_Context) op.Delegate;
                        outputBuilder.Append("\t\t");
                        ProcessExpression(op.Delegate.Token, op.Delegate, outputBuilder, jumpCodeOffsets, true);
                        outputBuilder.Append("->");
                        ProcessExpression(opp.ContextExpression.Token, opp.ContextExpression, outputBuilder, jumpCodeOffsets);
                        outputBuilder.Append(".RemoveDelegate(");
                        ProcessExpression(op.DelegateToAdd.Token, op.DelegateToAdd, outputBuilder, jumpCodeOffsets);
                        outputBuilder.Append(");\n\n");
                    }
                    break;
                }
            case EExprToken.EX_ClearMulticastDelegate: // this also
                {
                    EX_ClearMulticastDelegate op = (EX_ClearMulticastDelegate) expression;
                    outputBuilder.Append("\t\t");
                    ProcessExpression(op.DelegateToClear.Token, op.DelegateToClear, outputBuilder, jumpCodeOffsets, true);
                    outputBuilder.Append(".Clear();\n\n");
                    break;
                }
            case EExprToken.EX_CallMulticastDelegate: // this also
                {
                    EX_CallMulticastDelegate op = (EX_CallMulticastDelegate) expression;
                    KismetExpression[] opp = op.Parameters;
                    if (op.Delegate.Token == EExprToken.EX_LocalVariable || op.Delegate.Token == EExprToken.EX_InstanceVariable)
                    {
                        outputBuilder.Append("\t\t");
                        ProcessExpression(op.Delegate.Token, op.Delegate, outputBuilder, jumpCodeOffsets, true);
                        outputBuilder.Append(".Call(");
                        for (int i = 0; i < opp.Length; i++)
                        {
                            if (opp.Length > 4)
                                outputBuilder.Append("\n\t\t");
                            ProcessExpression(opp[i].Token, opp[i], outputBuilder, jumpCodeOffsets, true);
                            if (i < opp.Length - 1)
                            {
                                outputBuilder.Append(", ");
                            }
                        }
                        outputBuilder.Append(");\n\n");
                    }
                    else if (op.Delegate.Token != EExprToken.EX_Context)
                    {

                    }
                    else
                    {
                        outputBuilder.Append("\t\t");
                        ProcessExpression(op.Delegate.Token, op.Delegate, outputBuilder, jumpCodeOffsets, true);
                        outputBuilder.Append(".Call(");
                        for (int i = 0; i < opp.Length; i++)
                        {
                            if (opp.Length > 4)
                                outputBuilder.Append("\n\t\t");
                            ProcessExpression(opp[i].Token, opp[i], outputBuilder, jumpCodeOffsets, true);
                            if (i < opp.Length - 1)
                            {
                                outputBuilder.Append(", ");
                            }
                        }
                        outputBuilder.Append(");\n\n");
                    }
                    break;
                }
            case EExprToken.EX_ClassContext:
            case EExprToken.EX_Context:
                {
                    EX_Context op = (EX_Context) expression;
                    outputBuilder.Append(outputBuilder.ToString().EndsWith('\n') ? "\t\t" : "");
                    ProcessExpression(op.ObjectExpression.Token, op.ObjectExpression, outputBuilder, jumpCodeOffsets, true);

                    outputBuilder.Append("->");
                    ProcessExpression(op.ContextExpression.Token, op.ContextExpression, outputBuilder, jumpCodeOffsets, true);
                    if (!isParameter)
                    {
                        outputBuilder.Append(";\n\n");
                    }
                    break;
                }
            case EExprToken.EX_Context_FailSilent:
                {
                    EX_Context op = (EX_Context) expression;
                    outputBuilder.Append("\t\t");
                    ProcessExpression(op.ObjectExpression.Token, op.ObjectExpression, outputBuilder, jumpCodeOffsets, true);
                    if (!isParameter)
                    {
                        outputBuilder.Append("->");
                        ProcessExpression(op.ContextExpression.Token, op.ContextExpression, outputBuilder, jumpCodeOffsets, true);
                        outputBuilder.Append(";\n\n");
                    }
                    break;
                }
            case EExprToken.EX_Let:
                {
                    EX_Let op = (EX_Let) expression;
                    if (!isParameter)
                    {
                        outputBuilder.Append("\t\t");
                    }
                    ProcessExpression(op.Variable.Token, op.Variable, outputBuilder, jumpCodeOffsets, true);
                    outputBuilder.Append(" = ");
                    ProcessExpression(op.Assignment.Token, op.Assignment, outputBuilder, jumpCodeOffsets, true);
                    if (!isParameter)
                    {
                        outputBuilder.Append(";\n\n");
                    }
                    break;
                }
            case EExprToken.EX_LetObj:
            case EExprToken.EX_LetWeakObjPtr:
            case EExprToken.EX_LetBool:
            case EExprToken.EX_LetDelegate:
            case EExprToken.EX_LetMulticastDelegate:
                {
                    EX_LetBase op = (EX_LetBase) expression;
                    if (!isParameter)
                    {
                        outputBuilder.Append("\t\t");
                    }
                    ProcessExpression(op.Variable.Token, op.Variable, outputBuilder, jumpCodeOffsets, true);
                    outputBuilder.Append(" = ");
                    ProcessExpression(op.Assignment.Token, op.Assignment, outputBuilder, jumpCodeOffsets, true);
                    if (!isParameter || op.Assignment.Token == EExprToken.EX_LocalFinalFunction || op.Assignment.Token == EExprToken.EX_FinalFunction || op.Assignment.Token == EExprToken.EX_CallMath)
                    {
                        outputBuilder.Append(";\n\n");
                    }
                    else
                    {
                        outputBuilder.Append(';');
                    }
                    break;
                }
            case EExprToken.EX_JumpIfNot:
                {
                    EX_JumpIfNot op = (EX_JumpIfNot) expression;
                    outputBuilder.Append("\t\tif (!");
                    ProcessExpression(op.BooleanExpression.Token, op.BooleanExpression, outputBuilder, jumpCodeOffsets, true);
                    outputBuilder.Append(") \r\n");
                    outputBuilder.Append("\t\t    goto Label_");
                    outputBuilder.Append(op.CodeOffset);
                    outputBuilder.Append(";\n\n");
                    break;
                }
            case EExprToken.EX_Jump:
                {
                    EX_Jump op = (EX_Jump) expression;
                    outputBuilder.Append($"\t\tgoto Label_{op.CodeOffset};\n\n");
                    break;
                }
            // Static expressions

            case EExprToken.EX_TextConst:
                {
                    EX_TextConst op = (EX_TextConst) expression;

                    if (op.Value is FScriptText scriptText)
                    {
                        if (scriptText.SourceString == null)
                        {
                            outputBuilder.Append("nullptr");
                        }
                        else
                            ProcessExpression(scriptText.SourceString.Token, scriptText.SourceString, outputBuilder, jumpCodeOffsets, true);
                    }
                    else
                    {
                        outputBuilder.Append(op.Value); // impossible to reach?
                    }
                }
                break;
            case EExprToken.EX_StructMemberContext:
                {
                    EX_StructMemberContext op = (EX_StructMemberContext) expression;
                    ProcessExpression(op.StructExpression.Token, op.StructExpression, outputBuilder, jumpCodeOffsets);
                    outputBuilder.Append('.');
                    outputBuilder.Append(ProcessTextProperty(op.Property, false));
                    break;
                }
            case EExprToken.EX_Return:
                {
                    EX_Return op = (EX_Return) expression;
                    bool check = op.ReturnExpression.Token == EExprToken.EX_Nothing;
                    outputBuilder.Append("\t\treturn");
                    if (!check)
                        outputBuilder.Append(' ');
                    ProcessExpression(op.ReturnExpression.Token, op.ReturnExpression, outputBuilder, jumpCodeOffsets, true);
                    outputBuilder.AppendLine(";\n\n");
                    break;
                }
            case EExprToken.EX_RotationConst:
                {
                    EX_RotationConst op = (EX_RotationConst) expression;
                    FRotator value = op.Value;
                    outputBuilder.Append($"FRotator({value.Pitch}, {value.Yaw}, {value.Roll})");
                    break;
                }
            case EExprToken.EX_VectorConst:
                {
                    EX_VectorConst op = (EX_VectorConst) expression;
                    FVector value = op.Value;
                    outputBuilder.Append($"FVector({value.X}, {value.Y}, {value.Z})");
                    break;
                }
            case EExprToken.EX_Vector3fConst:
                {
                    EX_Vector3fConst op = (EX_Vector3fConst) expression;
                    FVector value = op.Value;
                    outputBuilder.Append($"FVector3f({value.X}, {value.Y}, {value.Z})");
                    break;
                }
            case EExprToken.EX_TransformConst:
                {
                    EX_TransformConst op = (EX_TransformConst) expression;
                    FTransform value = op.Value;
                    outputBuilder.Append($"FTransform(FQuat({value.Rotation.X}, {value.Rotation.Y}, {value.Rotation.Z}, {value.Rotation.W}), FVector({value.Translation.X}, {value.Translation.Y}, {value.Translation.Z}), FVector({value.Scale3D.X}, {value.Scale3D.Y}, {value.Scale3D.Z}))");
                    break;
                }


            case EExprToken.EX_LocalVariable:
            case EExprToken.EX_DefaultVariable:
            case EExprToken.EX_LocalOutVariable:
            case EExprToken.EX_ClassSparseDataVariable:
                outputBuilder.Append(ProcessTextProperty(((EX_VariableBase) expression).Variable, false));
                break;
            case EExprToken.EX_InstanceVariable:
                outputBuilder.Append(ProcessTextProperty(((EX_VariableBase) expression).Variable, true));
                break;

            case EExprToken.EX_ByteConst:
            case EExprToken.EX_IntConstByte:
                outputBuilder.Append($"0x{((KismetExpression<byte>) expression).Value.ToString("X")}");
                break;
            case EExprToken.EX_SoftObjectConst:
                ProcessExpression(((EX_SoftObjectConst) expression).Value.Token, ((EX_SoftObjectConst) expression).Value, outputBuilder, jumpCodeOffsets);
                break;
            case EExprToken.EX_DoubleConst:
                {
                    double value = ((EX_DoubleConst) expression).Value;
                    outputBuilder.Append(Math.Abs(value - Math.Floor(value)) < 1e-10 ? (int) value : value.ToString("R"));
                    break;
                }
            case EExprToken.EX_NameConst:
                outputBuilder.Append($"\"{((EX_NameConst) expression).Value}\"");
                break;
            case EExprToken.EX_IntConst:
                outputBuilder.Append(((EX_IntConst) expression).Value.ToString());
                break;
            case EExprToken.EX_PropertyConst:
                outputBuilder.Append(ProcessTextProperty(((EX_PropertyConst) expression).Property, false));
                break;
            case EExprToken.EX_StringConst:
                outputBuilder.Append($"\"{((EX_StringConst) expression).Value}\"");
                break;
            case EExprToken.EX_FieldPathConst:
                ProcessExpression(((EX_FieldPathConst) expression).Value.Token, ((EX_FieldPathConst) expression).Value, outputBuilder, jumpCodeOffsets);
                break;
            case EExprToken.EX_Int64Const:
                outputBuilder.Append(((EX_Int64Const) expression).Value.ToString());
                break;
            case EExprToken.EX_UInt64Const:
                outputBuilder.Append(((EX_UInt64Const) expression).Value.ToString());
                break;
            case EExprToken.EX_SkipOffsetConst:
                outputBuilder.Append(((EX_SkipOffsetConst) expression).Value.ToString());
                break;
            case EExprToken.EX_FloatConst:
                outputBuilder.Append(((EX_FloatConst) expression).Value.ToString(CultureInfo.GetCultureInfo("en-US")));
                break;
            case EExprToken.EX_BitFieldConst:
                outputBuilder.Append(((EX_BitFieldConst) expression).ConstValue);
                break;
            case EExprToken.EX_UnicodeStringConst:
                outputBuilder.Append(((EX_UnicodeStringConst) expression).Value);
                break;
            case EExprToken.EX_InstanceDelegate:
                outputBuilder.Append($"\"{((EX_InstanceDelegate) expression).FunctionName}\"");
                break;
            case EExprToken.EX_EndOfScript:
            case EExprToken.EX_EndParmValue:
                outputBuilder.Append("\t}\n");
                break;
            case EExprToken.EX_NoObject:
            case EExprToken.EX_NoInterface:
                outputBuilder.Append("nullptr");
                break;
            case EExprToken.EX_IntOne:
                outputBuilder.Append(1);
                break;
            case EExprToken.EX_IntZero:
                outputBuilder.Append(0);
                break;
            case EExprToken.EX_True:
                outputBuilder.Append("true");
                break;
            case EExprToken.EX_False:
                outputBuilder.Append("false");
                break;
            case EExprToken.EX_Self:
                outputBuilder.Append("this");
                break;

            case EExprToken.EX_Nothing:
            case EExprToken.EX_NothingInt32:
            case EExprToken.EX_EndFunctionParms:
            case EExprToken.EX_EndStructConst:
            case EExprToken.EX_EndArray:
            case EExprToken.EX_EndArrayConst:
            case EExprToken.EX_EndSet:
            case EExprToken.EX_EndMap:
            case EExprToken.EX_EndMapConst:
            case EExprToken.EX_EndSetConst:
            case EExprToken.EX_PushExecutionFlow:
            case EExprToken.EX_PopExecutionFlow:
            case EExprToken.EX_DeprecatedOp4A:
            case EExprToken.EX_WireTracepoint:
            case EExprToken.EX_Tracepoint:
            case EExprToken.EX_Breakpoint:
            case EExprToken.EX_AutoRtfmStopTransact:
            case EExprToken.EX_AutoRtfmTransact:
            case EExprToken.EX_AutoRtfmAbortIfNot:
                // some here are "useful" and unsupported
                break;
            /*
             Todo: check what uses these, fortnite has none instances
            EExprToken.EX_Assert
            EExprToken.EX_Skip
            EExprToken.EX_InstrumentationEvent
            EExprToken.EX_FieldPathConst
            */
            default:
                outputBuilder.Append($"{token}");
                break;
        }
    }
}