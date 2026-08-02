using CUE4Parse_Conversion.Animations;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.PoseAsset;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.UEFormat.Enums;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;

namespace CUE4Parse_Conversion;

// 内部ライターが共有する設定。旧Exporter APIではない。
public struct WriterOptions
{
    public ELodFormat LodFormat;
    public EMeshFormat MeshFormat;
    public CUE4Parse.UE4.Assets.Exports.Nanite.ENaniteMeshFormat NaniteMeshFormat;
    public EAnimFormat AnimFormat;
    public EPoseFormat PoseFormat;
    public EMaterialFormat MaterialFormat;
    public ETextureFormat TextureFormat;
    public EFileCompressionFormat CompressionFormat;
    public ETexturePlatform Platform;
    public ESocketFormat SocketFormat;
    public bool ExportMorphTargets;
    public bool ExportMaterials;
    public bool ExportHdrTexturesAsHdr;

    public WriterOptions()
    {
        LodFormat = ELodFormat.FirstLod;
        MeshFormat = EMeshFormat.ActorX;
        NaniteMeshFormat = CUE4Parse.UE4.Assets.Exports.Nanite.ENaniteMeshFormat.OnlyNaniteLOD;
        AnimFormat = EAnimFormat.ActorX;
        PoseFormat = EPoseFormat.UEFormat;
        MaterialFormat = EMaterialFormat.AllLayersNoRef;
        TextureFormat = ETextureFormat.Png;
        CompressionFormat = EFileCompressionFormat.None;
        Platform = ETexturePlatform.DesktopMobile;
        SocketFormat = ESocketFormat.Bone;
        ExportMorphTargets = true;
        ExportMaterials = true;
        ExportHdrTexturesAsHdr = true;
    }
}
