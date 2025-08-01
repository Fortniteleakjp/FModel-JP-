using System.Collections.Generic;
using CUE4Parse.GameTypes.FF7.Assets.Objects;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.Meshes;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;
using CUE4Parse.GameTypes.MK1.Assets.Objects;

namespace CUE4Parse.UE4.Assets.Exports.SkeletalMesh;

public enum EClassDataStripFlag : byte
{
    CDSF_AdjacencyData = 1,
    CDSF_MinLodData = 2,
};

[JsonConverter(typeof(FStaticLODModelConverter))]
public class FStaticLODModel
{
    public FSkelMeshSection[] Sections = [];
    public FMultisizeIndexContainer? Indices;
    public short[] ActiveBoneIndices;
    public FSkelMeshChunk[] Chunks;
    public int Size;
    public int NumVertices;
    public short[] RequiredBones;
    public FIntBulkData RawPointIndices;
    public int[] MeshToImportVertexMap;
    public int MaxImportVertex;
    public int NumTexCoords;
    public FMorphTargetVertexInfoBuffers? MorphTargetVertexInfoBuffers;
    public Dictionary<FName, FSkeletalMeshAttributeVertexBuffer>? VertexAttributeBuffers;
    public FSkeletalMeshVertexBuffer VertexBufferGPUSkin;
    public FSkeletalMeshVertexColorBuffer ColorVertexBuffer;
    public FMultisizeIndexContainer AdjacencyIndexBuffer;
    public FSkeletalMeshVertexClothBuffer ClothVertexBuffer;
    public FSkeletalMeshHalfEdgeBuffer HalfEdgeBuffer;
    public bool SkipLod => Indices == null || Indices.Indices16.Length < 1 && Indices.Indices32.Length < 1;
    // Game specific data
    public object? AdditionalBuffer;

    public FStaticLODModel()
    {
        Chunks = [];
        MeshToImportVertexMap = [];
        ColorVertexBuffer = new FSkeletalMeshVertexColorBuffer();
    }

    // special version for reading from BulkData
    public FStaticLODModel(FArchive Ar, bool bHasVertexColors, bool isFilterEditorOnly) : this()
    {
        var stripDataFlags = Ar.Read<FStripDataFlags>();
        var skelMeshVer = FSkeletalMeshCustomVersion.Get(Ar);

        Sections = Ar.ReadArray(() => new FSkelMeshSection(Ar, isFilterEditorOnly));

        if (skelMeshVer < FSkeletalMeshCustomVersion.Type.SplitModelAndRenderData)
        {
            Indices = new FMultisizeIndexContainer(Ar);
        }
        else
        {
            // UE4.19+ uses 32-bit index buffer (for editor data)
            Indices = new FMultisizeIndexContainer { Indices32 = Ar.ReadBulkArray<uint>() };
        }

        ActiveBoneIndices = Ar.ReadArray<short>();

        if (skelMeshVer < FSkeletalMeshCustomVersion.Type.CombineSectionWithChunk)
        {
            Chunks = Ar.ReadArray(() => new FSkelMeshChunk(Ar));
        }

        Size = Ar.Read<int>();
        if (!stripDataFlags.IsAudioVisualDataStripped())
            NumVertices = Ar.Read<int>();

        RequiredBones = Ar.ReadArray<short>();
        if (!stripDataFlags.IsEditorDataStripped())
        {
            //RawPointIndices = new FIntBulkData(Ar);
            throw new ParserException("RawPointIndices is unsupported");
        }

        if (Ar.Ver >= EUnrealEngineObjectUE4Version.ADD_SKELMESH_MESHTOIMPORTVERTEXMAP)
        {
            MeshToImportVertexMap = Ar.ReadArray<int>();
            MaxImportVertex = Ar.Read<int>();
        }

        if (!stripDataFlags.IsAudioVisualDataStripped())
        {
            NumTexCoords = Ar.Read<int>();
            if (skelMeshVer < FSkeletalMeshCustomVersion.Type.SplitModelAndRenderData)
            {
                VertexBufferGPUSkin = new FSkeletalMeshVertexBuffer(Ar);
                if (skelMeshVer >= FSkeletalMeshCustomVersion.Type.UseSeparateSkinWeightBuffer)
                {
                    var skinWeights = new FSkinWeightVertexBuffer(Ar, VertexBufferGPUSkin.bExtraBoneInfluences);
                    if (skinWeights.Weights.Length > 0)
                    {
                        // Copy data to VertexBufferGPUSkin
                        if (VertexBufferGPUSkin.bUseFullPrecisionUVs)
                        {
                            for (var i = 0; i < NumVertices; i++)
                            {
                                VertexBufferGPUSkin.VertsFloat[i].Infs = skinWeights.Weights[i];
                            }
                        }
                        else
                        {
                            for (var i = 0; i < NumVertices; i++)
                            {
                                VertexBufferGPUSkin.VertsHalf[i].Infs = skinWeights.Weights[i];
                            }
                        }
                    }
                }

                if (bHasVertexColors)
                {
                    if (skelMeshVer < FSkeletalMeshCustomVersion.Type.UseSharedColorBufferFormat)
                    {
                        ColorVertexBuffer = new FSkeletalMeshVertexColorBuffer(Ar);
                    }
                    else
                    {
                        var newColorVertexBuffer = new FColorVertexBuffer(Ar);
                        ColorVertexBuffer = new FSkeletalMeshVertexColorBuffer(newColorVertexBuffer.Data);
                    }
                }

                if (Ar.Ver < EUnrealEngineObjectUE4Version.REMOVE_EXTRA_SKELMESH_VERTEX_INFLUENCES)
                    throw new ParserException("Unsupported: extra SkelMesh vertex influences (old mesh format)");

                if (!stripDataFlags.IsClassDataStripped((byte) EClassDataStripFlag.CDSF_AdjacencyData))
                    AdjacencyIndexBuffer = new FMultisizeIndexContainer(Ar);

                if (Ar.Ver >= EUnrealEngineObjectUE4Version.APEX_CLOTH && HasClothData())
                    ClothVertexBuffer = new FSkeletalMeshVertexClothBuffer(Ar);
            }
        }
    }

    public FStaticLODModel(FAssetArchive Ar, bool bHasVertexColors) : this()
    {
        if (Ar.Game == EGame.GAME_SeaOfThieves) Ar.Position += 4;
        var stripDataFlags = Ar.Read<FStripDataFlags>();
        var skelMeshVer = FSkeletalMeshCustomVersion.Get(Ar);
        if (Ar.Game == EGame.GAME_SeaOfThieves) Ar.Position += 4;

        Sections = Ar.ReadArray(() => new FSkelMeshSection(Ar, Ar.IsFilterEditorOnly));

        if (skelMeshVer < FSkeletalMeshCustomVersion.Type.SplitModelAndRenderData)
        {
            Indices = new FMultisizeIndexContainer(Ar);
        }
        else
        {
            // UE4.19+ uses 32-bit index buffer (for editor data)
            Indices = new FMultisizeIndexContainer { Indices32 = Ar.ReadBulkArray<uint>() };
        }

        ActiveBoneIndices = Ar.ReadArray<short>();

        if (skelMeshVer < FSkeletalMeshCustomVersion.Type.CombineSectionWithChunk)
        {
            Chunks = Ar.ReadArray(() => new FSkelMeshChunk(Ar));
        }

        Size = Ar.Read<int>();
        if (!stripDataFlags.IsAudioVisualDataStripped())
            NumVertices = Ar.Read<int>();

        RequiredBones = Ar.ReadArray<short>();
        if (!stripDataFlags.IsEditorDataStripped())
            RawPointIndices = new FIntBulkData(Ar);

        if (Ar.Game != EGame.GAME_StateOfDecay2 && Ar.Ver >= EUnrealEngineObjectUE4Version.ADD_SKELMESH_MESHTOIMPORTVERTEXMAP)
        {
            MeshToImportVertexMap = Ar.ReadArray<int>();
            MaxImportVertex = Ar.Read<int>();
        }

        if (!stripDataFlags.IsAudioVisualDataStripped())
        {
            NumTexCoords = Ar.Read<int>();
            if (skelMeshVer < FSkeletalMeshCustomVersion.Type.SplitModelAndRenderData)
            {
                VertexBufferGPUSkin = new FSkeletalMeshVertexBuffer(Ar);
                if (skelMeshVer >= FSkeletalMeshCustomVersion.Type.UseSeparateSkinWeightBuffer)
                {
                    var skinWeights = new FSkinWeightVertexBuffer(Ar, VertexBufferGPUSkin.bExtraBoneInfluences);
                    if (skinWeights.Weights.Length > 0)
                    {
                        // Copy data to VertexBufferGPUSkin
                        if (VertexBufferGPUSkin.bUseFullPrecisionUVs)
                        {
                            for (var i = 0; i < NumVertices; i++)
                            {
                                VertexBufferGPUSkin.VertsFloat[i].Infs = skinWeights.Weights[i];
                            }
                        }
                        else
                        {
                            for (var i = 0; i < NumVertices; i++)
                            {
                                VertexBufferGPUSkin.VertsHalf[i].Infs = skinWeights.Weights[i];
                            }
                        }
                    }
                }

                if (bHasVertexColors)
                {
                    if (skelMeshVer < FSkeletalMeshCustomVersion.Type.UseSharedColorBufferFormat)
                    {
                        ColorVertexBuffer = new FSkeletalMeshVertexColorBuffer(Ar);
                    }
                    else
                    {
                        var newColorVertexBuffer = new FColorVertexBuffer(Ar);
                        ColorVertexBuffer = new FSkeletalMeshVertexColorBuffer(newColorVertexBuffer.Data);
                    }
                }

                if (Ar.Ver < EUnrealEngineObjectUE4Version.REMOVE_EXTRA_SKELMESH_VERTEX_INFLUENCES)
                    throw new ParserException("Unsupported: extra SkelMesh vertex influences (old mesh format)");

                // https://github.com/gildor2/UEViewer/blob/master/Unreal/UnrealMesh/UnMesh4.cpp#L1415
                if (Ar.Game == EGame.GAME_StateOfDecay2)
                    stripDataFlags.ClassStripFlags |= (byte) EClassDataStripFlag.CDSF_AdjacencyData;

                if (Ar.Game == EGame.GAME_SeaOfThieves)
                {
                    var arraySize = Ar.Read<int>();
                    Ar.Position += arraySize * 44;

                    for (var i = 0; i < 4; i++)
                    {
                        Ar.ReadArray<int>(); // 4 arrays worth
                    }

                    Ar.Position += 13;
                }

                if (Ar.Game == EGame.GAME_FinalFantasy7Remake)
                {
                    var checkInt = Ar.Read<int>();
                    if (checkInt >= 10)
                    {
                        Ar.Position -= 4;
                        AdjacencyIndexBuffer = new FMultisizeIndexContainer(Ar);
                    }

                    checkInt = Ar.Read<int>();
                    if (checkInt is 0 or 1) return;
                    Ar.Position -= 4;

                    var internalStripFlags = new FStripDataFlags(Ar);
                    if (internalStripFlags.IsClassDataStripped((byte) EClassDataStripFlag.CDSF_AdjacencyData))
                    {
                        Ar.Position -= 2;
                        return;
                    }

                    var size = Ar.Read<int>();
                    var count = Ar.Read<int>();

                    if (count < 30)
                    {
                        Ar.Position -= 10;
                        return;
                    }

                    Ar.Position += size * count;

                    ColorVertexBuffer = new FSkeletalMeshVertexColorBuffer(Ar);

                    return;
                }

                if (!stripDataFlags.IsClassDataStripped((byte) EClassDataStripFlag.CDSF_AdjacencyData))
                    AdjacencyIndexBuffer = new FMultisizeIndexContainer(Ar);

                if (Ar.Ver >= EUnrealEngineObjectUE4Version.APEX_CLOTH && HasClothData())
                    ClothVertexBuffer = new FSkeletalMeshVertexClothBuffer(Ar);

                if (Ar.Game == EGame.GAME_DaysGone)
                {
                    _ = new FMultisizeIndexContainer(Ar);
                    Ar.Position += 12;
                }
                if (Ar.Game == EGame.GAME_StateOfDecay2) Ar.Position += 8;
            }
        }

        if (Ar.Game == EGame.GAME_SeaOfThieves)
        {
            _ = new FMultisizeIndexContainer(Ar);
        }
    }

    // UE ref https://github.com/EpicGames/UnrealEngine/blob/26450a5a59ef65d212cf9ce525615c8bd673f42a/Engine/Source/Runtime/Engine/Private/SkeletalMeshLODRenderData.cpp#L710
    public void SerializeRenderItem(FAssetArchive Ar, bool bHasVertexColors, byte numVertexColorChannels)
    {
        var stripDataFlags = Ar.Read<FStripDataFlags>();
        var bIsLODCookedOut = false;
        if (Ar.Game != EGame.GAME_Splitgate)
            bIsLODCookedOut = Ar.ReadBoolean();
        var bInlined = Ar.ReadBoolean();

        RequiredBones = Ar.ReadArray<short>();
        if (!stripDataFlags.IsAudioVisualDataStripped() && !bIsLODCookedOut)
        {
            Sections = new FSkelMeshSection[Ar.Read<int>()];
            for (var i = 0; i < Sections.Length; i++)
            {
                Sections[i] = new FSkelMeshSection();
                Sections[i].SerializeRenderItem(Ar);
            }

            ActiveBoneIndices = Ar.ReadArray<short>();

            if (Ar.Game is EGame.GAME_KenaBridgeofSpirits)
                Ar.ReadArray<byte>(); // EAssetType_array1
            if (Ar.Game is EGame.GAME_FragPunk)
                Ar.Read<int>();

            Ar.Position += 4; //var buffersSize = Ar.Read<uint>();

            if (bInlined)
            {
                SerializeStreamedData(Ar, bHasVertexColors);

                if (Ar.Game == EGame.GAME_RogueCompany)
                {
                    Ar.Position += 12; // 1 (Long) + 2^16 (Int)
                    var elementSize = Ar.Read<int>();
                    var elementCount = Ar.Read<int>();
                    if (elementSize > 0 && elementCount > 0)
                        Ar.SkipBulkArrayData();
                }

                if (Ar.Game == EGame.GAME_MortalKombat1 && Ar.ReadBoolean())
                {
                    AdditionalBuffer = new FMorphTargetVertexInfoBufferMK1(Ar);
                }
            }
            else
            {
                var bulk = new FByteBulkData(Ar);
                if (bulk.Header.ElementCount > 0 && bulk.Data != null)
                {
                    if (Ar.Game == EGame.GAME_FinalFantasy7Rebirth)
                    {
                        FF7FStaticLodModel.ReadFStaticLodModel(Ar, bHasVertexColors, bulk, out Indices, out VertexBufferGPUSkin, out ColorVertexBuffer, out NumVertices, out NumTexCoords);
                        return;
                    }

                    using (var tempAr = new FByteArchive("LodReader", bulk.Data, Ar.Versions))
                    {
                        SerializeStreamedData(tempAr, bHasVertexColors);
                    }

                    var skipBytes = 5;
                    if (FUE5ReleaseStreamObjectVersion.Get(Ar) < FUE5ReleaseStreamObjectVersion.Type.RemovingTessellation && !stripDataFlags.IsClassDataStripped((byte) EClassDataStripFlag.CDSF_AdjacencyData))
                        skipBytes += 5;
                    skipBytes += 4 * 4 + 2 * 4 + 2 * 4;
                    skipBytes += FSkinWeightVertexBuffer.MetadataSize(Ar);
                    Ar.Position += skipBytes;

                    if (Ar.Game == EGame.GAME_StarWarsJediSurvivor) Ar.Position += 4;

                    if (HasClothData())
                    {
                        var clothIndexMapping = Ar.ReadArray<long>();
                        Ar.Position += 2 * 4;
                        if (FUE5ReleaseStreamObjectVersion.Get(Ar) >= FUE5ReleaseStreamObjectVersion.Type.AddClothMappingLODBias)
                        {
                            Ar.Position += 4 * clothIndexMapping.Length;
                        }
                    }

                    var profileNames = Ar.ReadArray(Ar.ReadFName);

                    if (Ar.Versions["SkeletalMesh.HasRayTracingData"] && Ar.Game >= EGame.GAME_UE5_6)
                    {
                        Ar.Position += 6 * 4; // FRayTracingGeometryOfflineDataHeader
                    }
                }
            }
        }

        if (Ar.Game == EGame.GAME_ReadyOrNot)
            Ar.Position += 4;
    }

    public void SerializeRenderItem_Legacy(FAssetArchive Ar, bool bHasVertexColors, byte numVertexColorChannels)
    {
        var stripDataFlags = Ar.Read<FStripDataFlags>();

        Sections = new FSkelMeshSection[Ar.Read<int>()];
        for (var i = 0; i < Sections.Length; i++)
        {
            Sections[i] = new FSkelMeshSection();
            Sections[i].SerializeRenderItem(Ar);
        }

        Indices = new FMultisizeIndexContainer(Ar);
        VertexBufferGPUSkin = new FSkeletalMeshVertexBuffer { bUseFullPrecisionUVs = true };

        ActiveBoneIndices = Ar.ReadArray<short>();
        RequiredBones = Ar.ReadArray<short>();

        if (!stripDataFlags.IsAudioVisualDataStripped() && !stripDataFlags.IsClassDataStripped((byte) EClassDataStripFlag.CDSF_MinLodData))
        {
            var positionVertexBuffer = new FPositionVertexBuffer(Ar);
            var staticMeshVertexBuffer = new FStaticMeshVertexBuffer(Ar);
            var skinWeightVertexBuffer = new FSkinWeightVertexBuffer(Ar, VertexBufferGPUSkin.bExtraBoneInfluences);

            if (!bHasVertexColors && Ar.Game == EGame.GAME_Borderlands3)
            {
                for (var i = 0; i < numVertexColorChannels; i++)
                {
                    var newColorVertexBuffer = new FColorVertexBuffer(Ar);
                    ColorVertexBuffer = new FSkeletalMeshVertexColorBuffer(newColorVertexBuffer.Data);
                }
            }
            else if (bHasVertexColors)
            {
                var newColorVertexBuffer = new FColorVertexBuffer(Ar);
                ColorVertexBuffer = new FSkeletalMeshVertexColorBuffer(newColorVertexBuffer.Data);
            }

            if (!stripDataFlags.IsClassDataStripped((byte) EClassDataStripFlag.CDSF_AdjacencyData))
                AdjacencyIndexBuffer = new FMultisizeIndexContainer(Ar);

            if (HasClothData())
                ClothVertexBuffer = new FSkeletalMeshVertexClothBuffer(Ar);

            NumVertices = positionVertexBuffer.NumVertices;
            NumTexCoords = staticMeshVertexBuffer.NumTexCoords;

            VertexBufferGPUSkin.VertsFloat = new FGPUVertFloat[NumVertices];
            for (var i = 0; i < VertexBufferGPUSkin.VertsFloat.Length; i++)
            {
                VertexBufferGPUSkin.VertsFloat[i] = new FGPUVertFloat
                {
                    Pos = positionVertexBuffer.Verts[i],
                    Infs = skinWeightVertexBuffer.Weights[i],
                    Normal = staticMeshVertexBuffer.UV[i].Normal,
                    UV = staticMeshVertexBuffer.UV[i].UV
                };
            }
        }

        if (Ar.Game >= EGame.GAME_UE4_23)
        {
            var skinWeightProfilesData = new FSkinWeightProfilesData(Ar);
        }
    }

    private void SerializeStreamedData(FArchive Ar, bool bHasVertexColors)
    {
        var stripDataFlags = Ar.Read<FStripDataFlags>();

        Indices = new FMultisizeIndexContainer(Ar);
        VertexBufferGPUSkin = new FSkeletalMeshVertexBuffer { bUseFullPrecisionUVs = true };

        var positionVertexBuffer = new FPositionVertexBuffer(Ar);
        var staticMeshVertexBuffer = new FStaticMeshVertexBuffer(Ar);
        var skinWeightVertexBuffer = new FSkinWeightVertexBuffer(Ar, VertexBufferGPUSkin.bExtraBoneInfluences);

        if (Ar.Game == EGame.GAME_EvilWest) Ar.Position += 22;

        if (bHasVertexColors)
        {
            var newColorVertexBuffer = new FColorVertexBuffer(Ar);
            ColorVertexBuffer = new FSkeletalMeshVertexColorBuffer(newColorVertexBuffer.Data);
        }

        if (FUE5ReleaseStreamObjectVersion.Get(Ar) < FUE5ReleaseStreamObjectVersion.Type.RemovingTessellation &&
            !stripDataFlags.IsClassDataStripped((byte) EClassDataStripFlag.CDSF_AdjacencyData))
        {
            if (Ar.Game != EGame.GAME_GTATheTrilogyDefinitiveEdition && Ar.Game != EGame.GAME_FinalFantasy7Rebirth)
                AdjacencyIndexBuffer = new FMultisizeIndexContainer(Ar);
        }

        if (HasClothData())
            ClothVertexBuffer = new FSkeletalMeshVertexClothBuffer(Ar);

        if (Ar.Game == EGame.GAME_Spectre)
        {
            _ = new FMultisizeIndexContainer(Ar);
        }

        var skinWeightProfilesData = new FSkinWeightProfilesData(Ar);

        if (Ar.Versions["SkeletalMesh.HasRayTracingData"])
        {
            if (Ar.Game >= EGame.GAME_UE5_6)
            {
                Ar.Position += 6 * 4; // FRayTracingGeometryOfflineDataHeader
                Ar.ReadBulkArray<byte>();
            }
            else
            {
                Ar.SkipFixedArray(1);
            }
        }

        if (FUE5SpecialProjectStreamObjectVersion.Get(Ar) >= FUE5SpecialProjectStreamObjectVersion.Type.SerializeSkeletalMeshMorphTargetRenderData)
        {
            bool bSerializeCompressedMorphTargets = Ar.ReadBoolean();
            if (bSerializeCompressedMorphTargets)
            {
                MorphTargetVertexInfoBuffers = new FMorphTargetVertexInfoBuffers(Ar);
            }
        }

        if (FUE5MainStreamObjectVersion.Get(Ar) >= FUE5MainStreamObjectVersion.Type.SkeletalVertexAttributes)
        {
            var count = Ar.Read<int>();
            VertexAttributeBuffers = new Dictionary<FName, FSkeletalMeshAttributeVertexBuffer>(count);
            for (int i = 0; i < count; i++)
            {
                VertexAttributeBuffers[Ar.ReadFName()] = new FSkeletalMeshAttributeVertexBuffer(Ar);
            }
        }

        if (FFortniteMainBranchObjectVersion.Get(Ar) >= FFortniteMainBranchObjectVersion.Type.SkeletalHalfEdgeData)
        {
            const byte MeshDeformerStripFlag = 1;
            var meshDeformerStripFlags = Ar.Read<FStripDataFlags>();
            if (!meshDeformerStripFlags.IsClassDataStripped(MeshDeformerStripFlag))
            {
                HalfEdgeBuffer = new FSkeletalMeshHalfEdgeBuffer(Ar);
            }
        }

        NumVertices = positionVertexBuffer.NumVertices;
        NumTexCoords = staticMeshVertexBuffer.NumTexCoords;

        VertexBufferGPUSkin.VertsFloat = new FGPUVertFloat[NumVertices];
        for (var i = 0; i < VertexBufferGPUSkin.VertsFloat.Length; i++)
        {
            VertexBufferGPUSkin.VertsFloat[i] = new FGPUVertFloat
            {
                Pos = positionVertexBuffer.Verts[i],
                Infs = skinWeightVertexBuffer.Weights[i],
                Normal = staticMeshVertexBuffer.UV[i].Normal,
                UV = staticMeshVertexBuffer.UV[i].UV
            };
        }
    }

    public bool HasClothData()
    {
        for (var i = 0; i < Chunks.Length; i++)
            if (Chunks[i].HasClothData) // pre-UE4.13 code
                return true;
        for (var i = 0; i < Sections.Length; i++) // UE4.13+
            if (Sections[i].HasClothData)
                return true;
        return false;
    }
}
