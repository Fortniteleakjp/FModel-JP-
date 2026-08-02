using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.PoseAsset.Conversion; // JP適合: CPoseAsset は JP レガシー(PoseAsset.Conversion)を再利用

namespace CUE4Parse_Conversion.Formats.PoseAsset;

public interface IPoseExportFormat : IExportFormat
{
    public ExportFile Build(string objectName, ExportOptions options, CPoseAsset poseAsset);
}
