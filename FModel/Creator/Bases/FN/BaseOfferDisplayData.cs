using System;
using System.Collections.Generic;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using SkiaSharp;

namespace FModel.Creator.Bases.FN;

public class BaseOfferDisplayData : UCreator
{
    private readonly List<BaseMaterialInstance> _offerImages;

    public BaseOfferDisplayData(UObject uObject, EIconStyle style) : base(uObject, style)
    {
        _offerImages = new List<BaseMaterialInstance>();
    }

    public override void ParseForInfo()
    {
        _offerImages.Clear();
        if (!Object.TryGetValue(out FStructFallback[] contextualPresentations, "ContextualPresentations"))
            return;

        SKBitmap companionIcon = null;
        var companionIconChecked = false;
        foreach (var context in contextualPresentations)
        {
            // New display assets reference textures directly; older ones use a material.
            var preview = GetImage(context, "FullTileOverrideRenderImage") ?? GetImage(context, "RenderImage");
            if (preview != null)
            {
                _offerImages.Add(new BaseMaterialInstance(Object, Style) { Preview = preview });
                continue;
            }

            BaseMaterialInstance offerImage = null;
            foreach (var name in new[] { "OverrideImageMaterial", "Material" })
            {
                if (!context.TryGetValue(out FSoftObjectPath path, name) ||
                    !TryLoad(path, out UMaterialInterface material)) continue;
                offerImage = new BaseMaterialInstance(material, Style);
                offerImage.ParseForInfo();
                if (offerImage.Preview != null) break;
            }

            // Some companion offers contain only a stale placeholder. Prefer their item icon.
            if (offerImage?.Preview == null)
            {
                if (!companionIconChecked)
                {
                    companionIcon = GetCompanionIcon();
                    companionIconChecked = true;
                }
                preview = companionIcon ?? GetImage(context, "FullTileOverrideRenderImage", true) ??
                    GetImage(context, "RenderImage", true);
                offerImage ??= new BaseMaterialInstance(Object, Style);
                offerImage.Preview = preview;
            }
            _offerImages.Add(offerImage);
        }
    }

    private SKBitmap GetImage(FStructFallback context, string name, bool allowPlaceholder = false)
    {
        if (!context.TryGetValue(out FSoftObjectPath path, name)) return null;
        var assetPath = path.AssetPathName.Text;
        if (IsPlaceholder(assetPath))
        {
            if (!allowPlaceholder) return null;
            if (TryLoad(path, out UTexture2D original)) return Utils.GetBitmap(original);

            // The OfferCatalog reference survives after the shared texture moved to BRCosmetics.
            const string oldRoot = "/OfferCatalog/Art/A_Shop_Tiles_Textures/";
            if (assetPath.StartsWith(oldRoot, StringComparison.OrdinalIgnoreCase))
                path = new FSoftObjectPath("/BRCosmetics/Art/A_Shop_Tiles_Textures/" + assetPath[oldRoot.Length..],
                    path.SubPathString, path.Owner ?? Object.Owner);
        }
        return TryLoad(path, out UTexture2D texture) ? Utils.GetBitmap(texture) : null;
    }

    private static bool IsPlaceholder(string path) => path?.Contains("/T_UI_PlaceholderCube.",
        StringComparison.OrdinalIgnoreCase) == true;

    private SKBitmap GetCompanionIcon()
    {
        if (!Object.Name.StartsWith("DAv2_Companion_", StringComparison.OrdinalIgnoreCase)) return null;
        var itemName = Object.Name["DAv2_".Length..];
        var path = new FSoftObjectPath($"/CosmeticCompanions/Assets/Items/{itemName}.{itemName}", "", Object.Owner);
        if (!TryLoad(path, out UObject item) || !item.TryGetValue(out FInstancedStruct[] data, "DataList")) return null;
        foreach (var name in new[] { "LargeIcon", "Icon" })
        {
            foreach (var entry in data)
            {
                if (entry.NonConstStruct is { } value && GetImage(value, name) is { } image) return image;
            }
        }
        return null;
    }

    private bool TryLoad<T>(FSoftObjectPath path, out T export) where T : UObject
    {
        export = null;
        IFileProvider provider = path.Owner?.Provider ?? Object.Owner?.Provider;
        if (provider == null || path.AssetPathName.IsNone || string.IsNullOrEmpty(path.AssetPathName.Text)) return false;
        var packagePath = path.AssetPathName.Text;
        var dot = packagePath.LastIndexOf('.');
        if (dot >= 0) packagePath = packagePath[..dot];
        // Missing optional images are common. Do not invoke the throwing file indexer for them.
        return provider.TryGetGameFile(packagePath, out _) && path.TryLoad(provider, out export);
    }

    public override SKBitmap[] Draw()
    {
        var ret = new SKBitmap[_offerImages.Count];
        for (var i = 0; i < ret.Length; i++)
        {
            ret[i] = _offerImages[i]?.Draw()[0];
        }

        return ret;
    }
}
