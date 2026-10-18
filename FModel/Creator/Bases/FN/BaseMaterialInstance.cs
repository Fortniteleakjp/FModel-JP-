using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.UObject;
using SkiaSharp;

namespace FModel.Creator.Bases.FN;

public class BaseMaterialInstance : BaseIcon
{
    public BaseMaterialInstance(UObject uObject, EIconStyle style) : base(uObject, style)
    {
        Background = new[] { SKColor.Parse("4F4F69"), SKColor.Parse("4F4F69") };
        Border = new[] { SKColor.Parse("9092AB") };
    }

    public override void ParseForInfo()
    {
        if (Object is not UMaterialInstanceConstant material) return;

        var visited = new HashSet<UMaterialInstanceConstant>();
        var colorsFound = false;
        while (material != null && visited.Add(material))
        {
            if (Preview == null)
            {
                foreach (var parameter in material.TextureParameterValues)
                {
                    var name = parameter.Name;
                    if (name is not ("SeriesTexture" or "TextureA" or "TextureB" or "OfferImage" or "CarTexture")) continue;
                    if (parameter.ParameterValue?.TryLoad<UTexture2D>(out var texture) != true) continue;
                    if (name == "SeriesTexture") GetSeries(texture);
                    else if (Preview == null) Preview = Utils.GetBitmap(texture);
                }
            }

            if (!colorsFound && material.VectorParameterValues.Any(x => x.Name != "FallOff_Color"))
            {
                foreach (var parameter in material.VectorParameterValues)
                {
                    if (parameter.ParameterValue == null) continue;
                    switch (parameter.Name)
                    {
                        case "Background_Color_A":
                            Background[0] = SKColor.Parse(parameter.ParameterValue.Value.Hex);
                            Border[0] = Background[0];
                            break;
                        case "Background_Color_B":
                            Background[1] = SKColor.Parse(parameter.ParameterValue.Value.Hex);
                            break;
                    }
                }
                colorsFound = true;
            }

            if (Preview != null && colorsFound) break;
            if (!material.TryGetValue(out FPackageIndex parent, "Parent") ||
                !Utils.TryGetPackageIndexExport(parent, out material)) break;
        }
    }

    public override SKBitmap[] Draw()
    {
        var ret = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var c = new SKCanvas(ret);

        switch (Style)
        {
            case EIconStyle.NoBackground:
                DrawPreview(c);
                break;
            default:
                DrawBackground(c);
                DrawPreview(c);
                break;
        }

        return new[] { ret };
    }
}
