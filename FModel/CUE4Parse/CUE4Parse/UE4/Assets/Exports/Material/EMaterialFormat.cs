using System.ComponentModel;

namespace CUE4Parse.UE4.Assets.Exports.Material
{
    public enum EMaterialFormat
    {
        [Description("First Layer Only")]
        FirstLayer,
        [Description("All Layers (Without Referenced Textures)")]
        AllLayersNoRef,
        [Description("All Layers (With All Referenced Textures)")]
        AllLayers
    }

    // Back-ported from CUE4Parse PR #358 (new export pipeline / ExportOptions).
    public enum EMaterialDepth
    {
        [Description("Top Layer Only")]
        TopLayerOnly,
        [Description("All Layers (Without Referenced Textures)")]
        AllLayersNoRef,
        [Description("All Layers (With All Referenced Textures)")]
        AllLayers,
    }
}
