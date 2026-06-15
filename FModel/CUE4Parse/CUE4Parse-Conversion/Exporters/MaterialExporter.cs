using System.Collections.Generic;
using System.Threading;
using CUE4Parse_Conversion.Formats.Materials;
using CUE4Parse_Conversion.Options;
using CUE4Parse.UE4.Assets.Exports.Material;

namespace CUE4Parse_Conversion.Exporters;

public sealed class MaterialExporter(UMaterialInterface material) : ExporterBase(material)
{
    protected override IReadOnlyList<ExportFile> BuildExportFiles(CancellationToken ct = default)
    {
        Log.Debug("Extracting material parameters (depth: {Depth})", Session.Options.MaterialDepth);

        var parameters = new CMaterialParams2();
        // PR #358 back-port (JP適合): 新 EMaterialDepth を JP の GetParams が取る EMaterialFormat へマップ(序数一致)。
        var materialFormat = Session.Options.MaterialDepth switch
        {
            EMaterialDepth.AllLayersNoRef => EMaterialFormat.AllLayersNoRef,
            EMaterialDepth.AllLayers => EMaterialFormat.AllLayers,
            _ => EMaterialFormat.FirstLayer,
        };
        material.GetParams(parameters, materialFormat);

        var files = new List<ExportFile> { new JsonMaterialFormat().Build(ObjectName, parameters) };
        if (Session.Options.MeshFormat == EMeshFormat.USD)
        {
            files.Add(new UsdMaterialFormat().Build(ObjectName, parameters, PackageDirectory));
        }

        foreach (var texture in parameters.Textures.Values)
        {
            ct.ThrowIfCancellationRequested();
            Session.Add(texture);
        }

        return files;
    }
}
