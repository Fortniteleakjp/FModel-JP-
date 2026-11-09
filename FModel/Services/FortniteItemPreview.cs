using System;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using Serilog;

namespace FModel.Services;

/// <summary>
/// the image the asset explorer shows for a Fortnite item definition
/// recent builds keep it in the DataList, older ones (e.g. 14.40) on the hero / weapon definition or the item itself
/// </summary>
public static class FortniteItemPreview
{
    /// <summary>
    /// versioned builds have no mappings, so the item definition classes can't be walked up to a registered type
    /// and the export is a plain <see cref="UObject"/>
    /// </summary>
    public static bool IsUntypedItemDefinition(UObject export, string projectName)
    {
        if (export is null || export.GetType() != typeof(UObject)) return false;
        if (!"FortniteGame".Equals(projectName, StringComparison.OrdinalIgnoreCase)) return false;

        var type = export.ExportType;
        if (string.IsNullOrEmpty(type)) return false;

        return type.EndsWith("ItemDefinition", StringComparison.Ordinal) ||
                type.EndsWith("ItemWrapDefinition", StringComparison.Ordinal) ||
                type is "FortHeroType" or "FortWorkerType" or "FortTokenType" or "FortVariantTokenType" or "FortBannerTokenType";
    }

    public static UTexture2D FindTexture(UObject item) => FindTexture(item, true);

    private static UTexture2D FindTexture(UObject item, bool followDefinitions)
    {
        if (item is null) return null;

        try
        {
            // recent builds
            if (item.TryGetValue(out FInstancedStruct[] dataList, "DataList"))
            {
                foreach (var data in dataList)
                {
                    if (data.NonConstStruct?.TryGetValue(out FSoftObjectPath icon, "Icon", "LargeIcon") == true &&
                        icon.TryLoad<UTexture2D>(out var texture))
                        return texture;
                }
            }

            // outfits and pickaxes show the image of their hero / weapon definition, like the icon creator does
            if (followDefinitions &&
                item.TryGetValue(out ILoadableObject definition, "HeroDefinition", "WeaponDefinition") &&
                definition.TryLoad(out UObject linked) &&
                FindTexture(linked, false) is { } linkedTexture)
                return linkedTexture;

            // older builds, the small one is enough for a thumbnail
            if (item.TryGetValue(out ILoadableObject preview, "SmallPreviewImage", "LargePreviewImage") &&
                preview.TryLoad(out UObject image) && image is UTexture2D previewTexture)
                return previewTexture;
        }
        catch (Exception e)
        {
            Log.Debug(e, "Failed to find the preview of {Name}", item.Name);
        }

        return null;
    }
}
