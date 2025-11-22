using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Documents;
using System.Windows.Forms;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Actor;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using FModel.AthenaProfile;
using FModel.ViewModels.CUE4Parse;
using FModel.Views.Resources.Controls;
using Newtonsoft.Json.Linq;
using static SharpGLTF.Scenes.LightBuilder;

namespace FModel.Features.Athena
{

    public class GenerateAllCosmeticsFeature
    {
        public static async Task ExecuteAsync(AbstractVfsFileProvider abstractVfsFileProvider)
        {
            AthenaGenerator athenaGenerator = new AthenaGenerator(false, false);

            if (!athenaGenerator.IsValid())
            {
                FLogger.Append(ELog.Error, () => FLogger.Text("プロファイルの初期化に失敗しました", Constants.WHITE, true));
                return;
            }

            FLogger.Append(ELog.Information, () => FLogger.Text("プロファイルを生成しています...", Constants.WHITE, true));

            foreach (IAesVfsReader mountedVf in abstractVfsFileProvider.MountedVfs)
            {
                foreach (GameFile value in mountedVf.Files.Values)
                {
                    if (value is not VfsEntry vfsEntry || !vfsEntry.IsUePackage || !vfsEntry.Name.EndsWith(".uasset")) // これでおそらく例外は完全に流せる
                        continue;

                    if (vfsEntry.Path.StartsWith("FortniteGame/Content/Athena/Items/Cosmetics/")
                        // バトルロイヤル
                        || vfsEntry.Path.StartsWith("FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Athena/Items/Cosmetics/")
                        // フェスティバル
                        || vfsEntry.Path.StartsWith("FortniteGame/Plugins/GameFeatures/FM/SparksCosmetics/Content/Instrument/Guitar")
                        || vfsEntry.Path.StartsWith("FortniteGame/Plugins/GameFeatures/FM/SparksCosmetics/Content/Instrument/Bass")
                        || vfsEntry.Path.StartsWith("FortniteGame/Plugins/GameFeatures/FM/SparksCosmetics/Content/Instrument/Drum")
                        || vfsEntry.Path.StartsWith("FortniteGame/Plugins/GameFeatures/FM/SparksCosmetics/Content/Instrument/Mic"))
                    {
                        IPackage package = await abstractVfsFileProvider.LoadPackageAsync(vfsEntry.Path);

                        if (package == null)
                            continue;

                        IEnumerable<UObject> exports = package.GetExports();

                        foreach (UObject export in exports)
                        {
                            if (Filters.ItemDefinition.Contains(export.ExportType))
                            {
                                UObject[] itemVariants = export.GetOrDefault<UObject[]>("ItemVariants");

                                if (itemVariants == null || itemVariants.Length == 0)
                                {
                                    athenaGenerator.AddItem(export);
                                }
                                else
                                {
                                    List<KeyValuePair<string, List<string>>> list = [];

                                    foreach (UObject itemVariant in itemVariants)
                                    {
                                        if (itemVariant == null || itemVariant.Class == null)
                                            continue;

                                        if (!IsSupportsVariant(itemVariant))
                                            continue;

                                        string variantChannelTag = GetVariantChannelTag(itemVariant);
                                        List<string> customizationVariantTags = GetCustomizationVariantTags(itemVariant);

                                        if (variantChannelTag != null && customizationVariantTags.Count != 0)
                                        {
                                            list.Add(new KeyValuePair<string, List<string>>(variantChannelTag, customizationVariantTags));
                                        }
                                    }

                                    athenaGenerator.AddItem(export, list);
                                }
                            }
                        }
                    }
                }
            }

            athenaGenerator.SaveFile();
        }

        private static bool IsSupportsVariant(UObject itemVariant)
        {
            return itemVariant.Class.Name == UFortCosmeticMeshVariant.GetName()
                || itemVariant.Class.Name == UFortCosmeticPropertyVariant.GetName()
                || itemVariant.Class.Name == UFortCosmeticMaterialVariant.GetName()
                || itemVariant.Class.Name == UFortCosmeticCharacterPartVariant.GetName()
                || itemVariant.Class.Name == UFortCosmeticGameplayTagVariant.GetName()
                || itemVariant.Class.Name == UFortCosmeticParticleVariant.GetName();
        }

        private static string GetVariantChannelTag(UObject itemVariant)
        {
            if (itemVariant == null || itemVariant.Class == null)
                return null;

            FStructFallback variantChannelTag = itemVariant.GetOrDefault<FStructFallback>("VariantChannelTag");

            if (variantChannelTag == null)
                return null;

            FName tagName = variantChannelTag.GetOrDefault<FName>("TagName");

            if (tagName == null)
                return null;

            return tagName.Text.Split('.').Last();
        }

        private static List<string> GetCustomizationVariantTags(UObject itemVariant)
        {
            List<string> result = [];

            if (itemVariant == null || itemVariant.Class == null)
                return result;

            FStructFallback[] options = [];

            switch (itemVariant.Class.Name)
            {
                case "FortCosmeticMeshVariant":
                    options = itemVariant.GetOrDefault<FStructFallback[]>("MeshOptions");
                    break;
                case "FortCosmeticPropertyVariant":
                    options = itemVariant.GetOrDefault<FStructFallback[]>("GenericPropertyOptions");
                    break;
                case "FortCosmeticMaterialVariant":
                    options = itemVariant.GetOrDefault<FStructFallback[]>("MaterialOptions");
                    break;
                case "FortCosmeticCharacterPartVariant":
                    options = itemVariant.GetOrDefault<FStructFallback[]>("PartOptions");
                    break;
                case "FortCosmeticGameplayTagVariant":
                    options = itemVariant.GetOrDefault<FStructFallback[]>("GenericTagOptions");
                    break;
                case "FortCosmeticParticleVariant":
                    options = itemVariant.GetOrDefault<FStructFallback[]>("ParticleOptions");
                    break;
                default:
                    break;
            }

            foreach (FStructFallback option in options)
            {
                FStructFallback customizationVariantTag = option.GetOrDefault<FStructFallback>("CustomizationVariantTag");

                if (customizationVariantTag == null)
                    continue;

                FName tagName = customizationVariantTag.GetOrDefault<FName>("TagName");

                if (tagName == null)
                    continue;

                result.Add(tagName.Text.Split('.').Last());
            }

            return result;
        }
    }
}
