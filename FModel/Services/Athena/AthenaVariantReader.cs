using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;

namespace FModel.Services.Athena;

/// <summary>
/// コスメティクスの ItemVariants をプロファイル用の <see cref="Variant"/> に変換する。
/// djlorenzouasset/Athena (Utils/Utils.cs GetCosmeticVariants) の移植。
/// </summary>
public static class AthenaVariantReader
{
    private static readonly string[] _specialPropertyTags =
    [
        "Property.Color", "Vehicle.Painted", "Vehicle.Tier", "Property.Outfit", "Property.Theme"
    ];

    private static readonly string[] _specialChannelTags =
    [
        "Slot", "Vehicle", "TagDriven", "Theme", "Immutable"
    ];

    public static List<Variant> GetCosmeticVariants(UObject obj)
    {
        var cosmeticVariants = new List<Variant>();

        var variants = obj.GetOrDefault("ItemVariants", Array.Empty<UObject>());
        foreach (var variant in variants)
        {
            var ownedParts = new List<string>();

            var optionName = variant.ExportType switch
            {
                "FortCosmeticTextVariant" => "CustomName",
                "FortCosmeticMeshVariant" => "MeshOptions",
                "FortCosmeticMaterialVariant" => "MaterialOptions",
                "FortCosmeticParticleVariant" => "ParticleOptions",
                "FortCosmeticPropertyVariant" => "GenericPropertyOptions",
                // FortCosmeticRichColorVariant は挙動が不明なため Athena 同様に除外
                "FortCosmeticGameplayTagVariant" => "GenericTagOptions",
                "FortCosmeticMorphTargetVariant" => "MorphTargetOptions",
                "FortCosmeticAdditivePoseVariant" => "AdditivePoseOptions",
                "FortCosmeticCharacterPartVariant" => "PartOptions",
                "FortCosmeticCIDRedirectorVariant" => "CIDRedirectors",
                "FortCosmeticLoadoutTagDrivenVariant" => "Variants",
                "FortCustomizableObjectSprayVariant" => "ActiveSelectionTag",
                "FortCustomizableObjectParameterVariant" => "ParameterOptions",
                _ => null
            };

            if (optionName is null)
                continue;

            if (optionName == "CustomName")
            {
                ownedParts.Add("[PH]CompanionName - Athena");
            }
            else if (optionName == "ActiveSelectionTag")
            {
                var activeSelectionTag = variant.GetOrDefault<FStructFallback>(optionName);
                if (activeSelectionTag is null)
                    continue;

                var tag = activeSelectionTag.GetOrDefault("TagName", new FName("Variant.Tag.TBD")).Text;
                if (tag is null)
                    continue;

                ownedParts.Add(tag.Split("Property.").Last() + ".X=ffff0000ffffSD=");
            }
            else
            {
                var options = variant.GetOrDefault<FStructFallback[]>(optionName);
                if (options is not { Length: > 0 })
                    continue;

                foreach (var option in options)
                {
                    var customizationVariantTag = option.GetOrDefault<FStructFallback>("CustomizationVariantTag");
                    if (customizationVariantTag is null) continue;

                    var tag = customizationVariantTag.GetOrDefault("TagName", new FName("Variant.Tag.TBD")).Text;
                    if (tag is null) continue;

                    ownedParts.Add(_specialPropertyTags.Any(st => tag.Contains(st, StringComparison.OrdinalIgnoreCase))
                        ? tag.Split("Property.").Last()
                        : tag.Split('.').Last());
                }
            }

            string channel = null;

            var variantChannelTag = variant.GetOrDefault<FStructFallback>("VariantChannelTag");
            if (variantChannelTag is null && optionName == "Variants")
            {
                // FortCosmeticLoadoutTagDrivenVariant の最初のバリアントは VariantChannelTag を持たないため
                // 実際のプロファイルに合わせて "TagDriven" を補う
                channel = "TagDriven";
            }
            else if (variantChannelTag is not null)
            {
                var channelName = variantChannelTag.GetOrDefault("TagName", new FName("Variant.Channel.TBD")).Text;
                channel = _specialChannelTags.Any(st => channelName.Contains(st, StringComparison.OrdinalIgnoreCase))
                    ? channelName.Split("Channel.").Last()
                    : channelName.Split('.').Last();
            }

            if (string.IsNullOrEmpty(channel))
                continue;

            cosmeticVariants.Add(new Variant
            {
                Channel = channel,
                Active = ownedParts.FirstOrDefault() ?? string.Empty,
                Owned = ownedParts
            });
        }

        return cosmeticVariants;
    }
}
