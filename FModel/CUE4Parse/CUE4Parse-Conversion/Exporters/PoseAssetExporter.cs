using System;
using System.Collections.Generic;
using System.Threading;
using CUE4Parse_Conversion.PoseAsset;
using CUE4Parse_Conversion.Formats.PoseAsset;
using CUE4Parse_Conversion.Options;
using CUE4Parse.UE4.Objects.Engine.Animation;

namespace CUE4Parse_Conversion.Exporters;

public class PoseAssetExporter(UPoseAsset poseAsset) : ExporterBase(poseAsset)
{
    protected override IReadOnlyList<ExportFile> BuildExportFiles(CancellationToken ct = default)
    {
        Log.Debug("Converting pose asset to {Format}", Session.Options.MeshFormat);

        if (!poseAsset.TryConvert(out var convertedPoseAsset))
        {
            throw new Exception("Failed to convert");
        }

        var format = GetPoseFormat(Session.Options.MeshFormat);
        return [format.Build(ObjectName, Session.Options, convertedPoseAsset)];
    }

    // JP適合: PoseAsset は UEFormat(uepose)のみ対応。他形式選択時も uepose で書き出す。
    private IPoseExportFormat GetPoseFormat(EMeshFormat format) => new UEFormatPoseFormat();
}
