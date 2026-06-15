namespace CUE4Parse_Conversion.Options;

// PR #358 back-port (JP適合):
// 新パイプラインの Anim/Pose フォーマット wrapper は新 ExportOptions を受け取るが、
// 再利用する JP レガシーのライター(ActorXAnim/UEAnim/UEPose)は旧 ExporterOptions(struct)を要求する。
// 必要なフィールドのみを橋渡しする。
public static class LegacyOptionsBridge
{
    public static ExporterOptions ToExporterOptions(this ExportOptions options) => new()
    {
        CompressionFormat = options.CompressionFormat, // どちらも CUE4Parse_Conversion.UEFormat.Enums.EFileCompressionFormat
        Platform = options.TexturePlatform,
        ExportMorphTargets = options.ExportMorphTargets,
        ExportMaterials = options.ExportMaterials,
    };
}
