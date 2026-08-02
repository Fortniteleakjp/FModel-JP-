namespace CUE4Parse_Conversion.Options;

// 新APIのオプションを内部ライターの設定へ変換する。
internal static class WriterOptionsAdapter
{
    internal static WriterOptions ToWriterOptions(this ExportOptions options) => new()
    {
        LodFormat = CUE4Parse_Conversion.Meshes.ELodFormat.AllLods,
        MeshFormat = (CUE4Parse_Conversion.Meshes.EMeshFormat)(int)options.MeshFormat,
        NaniteMeshFormat = (CUE4Parse.UE4.Assets.Exports.Nanite.ENaniteMeshFormat)(int)options.NaniteMeshFormat,
        AnimFormat = options.MeshFormat == EMeshFormat.UEFormat
            ? CUE4Parse_Conversion.Animations.EAnimFormat.UEFormat
            : CUE4Parse_Conversion.Animations.EAnimFormat.ActorX,
        TextureFormat = (CUE4Parse_Conversion.Textures.ETextureFormat)(int)options.TextureFormat,
        SocketFormat = (CUE4Parse_Conversion.Meshes.ESocketFormat)(int)options.SocketFormat,
        CompressionFormat = (CUE4Parse_Conversion.UEFormat.Enums.EFileCompressionFormat)(int)options.CompressionFormat,
        Platform = options.TexturePlatform,
        ExportMorphTargets = options.ExportMorphTargets,
        ExportMaterials = options.ExportMaterials,
        ExportHdrTexturesAsHdr = options.ExportHdrTexturesAsHdr
    };
}
