using System.Collections.Generic;
using CUE4Parse_Conversion.Animations.PSA; // JP適合: CAnimSet / ActorXAnim は JP レガシー(Animations.PSA)を再利用
using CUE4Parse_Conversion.Options;
using CUE4Parse.UE4.Writers;

namespace CUE4Parse_Conversion.Formats.Animations;

public sealed class ActorXAnimFormat : IAnimExportFormat
{
    public string DisplayName => "ActorX (psa)";

    public IReadOnlyList<ExportFile> Build(string objectName, ExportOptions options, CAnimSet animSet)
    {
        var legacy = options.ToExporterOptions();
        var results = new List<ExportFile>(animSet.Sequences.Count);
        for (var i = 0; i < results.Capacity; i++)
        {
            using var ar = new FArchiveWriter();
            new ActorXAnim(animSet, i, legacy).Save(ar);

            var suffix = i == 0 ? "" : $"_SEQ{i}";
            results.Add(new ExportFile("psa", ar.GetBuffer(), suffix));
        }

        return results;
    }
}
