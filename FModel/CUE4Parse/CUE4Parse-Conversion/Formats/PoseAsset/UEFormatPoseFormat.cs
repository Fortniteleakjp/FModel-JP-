using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.PoseAsset.Conversion; // JP適合: CPoseAsset
using CUE4Parse_Conversion.PoseAsset.UEFormat;   // JP適合: UEPose
using CUE4Parse.UE4.Writers;

namespace CUE4Parse_Conversion.Formats.PoseAsset;

public sealed class UEFormatPoseFormat : IPoseExportFormat
{
    public string DisplayName => "UEFormat (uepose)";

    public ExportFile Build(string objectName, ExportOptions options, CPoseAsset poseAsset)
    {
        using var ar = new FArchiveWriter();
        new UEPose(objectName, poseAsset, options.ToExporterOptions()).Save(ar);
        return new ExportFile("uepose", ar.GetBuffer());
    }
}
