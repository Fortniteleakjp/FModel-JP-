using System.Collections.Generic;
using CUE4Parse_Conversion.Animations.PSA; // JP適合: CAnimSet は JP レガシー(Animations.PSA)を再利用
using CUE4Parse_Conversion.Options;

namespace CUE4Parse_Conversion.Formats.Animations;

public interface IAnimExportFormat : IExportFormat
{
    public IReadOnlyList<ExportFile> Build(string objectName, ExportOptions options, CAnimSet animSet);
}
