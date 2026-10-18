using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using FModel;
using FModel.Creator;
using FModel.Creator.Bases.FN;
using FModel.Settings;
using SkiaSharp;

internal static class OfferPreviewChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Run(string root, string output)
    {
        // No text is drawn in these previews; use Skia's default fonts without a game/UI service.
        Utils.Typefaces = (Typefaces) RuntimeHelpers.GetUninitializedObject(typeof(Typefaces));
        UserSettings.Default.CurrentDir = new DirectorySettings { TexturePlatform = ETexturePlatform.DesktopMobile };
        var fixture = Path.Combine(root, "UAssetAPI/UAssetAPI.Tests/TestAssets/TestUE5_5/BlankGame");
        using var provider = new DefaultFileProvider(fixture, SearchOption.TopDirectoryOnly,
            new VersionContainer(EGame.GAME_UE5_5), StringComparer.OrdinalIgnoreCase);
        provider.Initialize();
        provider.MappingsContainer = new FileUsmapTypeMappingsProvider(Path.Combine(fixture, "BlankUE5_5.usmap"));
        var file = provider.Files.Values.Single(f => f.Name == "T_Test.uasset");
        var texture = provider.LoadPackage(file).GetExports().OfType<UTexture2D>().Single();
        var mock = DispatchProxy.Create<IFileProvider, OfferProviderProxy>();
        var state = (OfferProviderProxy) mock;
        state.File = file;
        var package = PackageProxy.Create();
        ((PackageProxy) package).Provider = mock;
        const string texturePath = "/Example/T_Test.T_Test";
        const string missing = "/OfferCatalog/Art/A_Shop_Tiles_Textures/T_UI_PlaceholderCube.T_UI_PlaceholderCube";
        state.Assets[texturePath] = texture;
        FPropertyTag Soft(string name, string path) => new()
        {
            Name = new FName(name), Tag = new SoftObjectProperty(new FSoftObjectPath(path, "", package))
        };
        FStructFallback Context(params FPropertyTag[] tags) => new(tags.ToList());
        BaseOfferDisplayData Offer(string name, params FStructFallback[] contexts)
        {
            var obj = new UObject { Name = name, Outer = new ResolvedPackageObject(package) };
            obj.Properties.Add(new FPropertyTag
            {
                Name = new FName("ContextualPresentations"),
                Tag = new ArrayProperty(new UScriptArray(contexts.Select(c => (FPropertyTagType)
                    new StructProperty(new FScriptStruct(c))).ToList(), "StructProperty"))
            });
            using var creator = new CreatorPackage(name, "AthenaItemShopOfferDisplayData", new Lazy<UObject>(() => obj), EIconStyle.NoBackground);
            Require(creator.TryConstructCreator(out var result) && result is BaseOfferDisplayData, "表示アセットのCreatorが選択されません");
            return (BaseOfferDisplayData) result!;
        }
        void Check(BaseOfferDisplayData offer, int count, string name)
        {
            offer.ParseForInfo();
            var images = offer.Draw();
            Require(images.Length == count && images.All(i => i.Width == 512 && i.Height == 512), name);
            for (var i = 0; i < images.Length; i++)
            {
                using var image = images[i];
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = File.Create(Path.Combine(output, $"offer-{name}-{i}.png"));
                data.SaveTo(stream);
            }
        }

        Check(Offer("DAv2_Test", Context(Soft("RenderImage", texturePath))), 1, "render-image");
        Require(state.Loaded.Last() == texturePath, "RenderImageが読み込まれません");
        Check(Offer("DAv2_Test", Context(Soft("FullTileOverrideRenderImage", texturePath), Soft("RenderImage", "/Missing/T.T"))), 1, "override-priority");
        Check(Offer("DAv2_Test", Context(Soft("FullTileOverrideRenderImage", "/Missing/T.T"), Soft("RenderImage", texturePath))), 1, "missing-override");

        var itemData = Context(Soft("LargeIcon", texturePath));
        var instanced = (FInstancedStruct) RuntimeHelpers.GetUninitializedObject(typeof(FInstancedStruct));
        typeof(FInstancedStruct).GetField(nameof(FInstancedStruct.ScriptStruct))!.SetValue(instanced, new FScriptStruct(itemData));
        var item = new UObject();
        item.Properties.Add(new FPropertyTag
        {
            Name = new FName("DataList"), Tag = new ArrayProperty(new UScriptArray(
                [new StructProperty(new FScriptStruct(instanced))], "StructProperty"))
        });
        state.Assets["/CosmeticCompanions/Assets/Items/Companion_PinkySight.Companion_PinkySight"] = item;
        var companion = Offer("DAv2_Companion_PinkySight", Context(Soft("RenderImage", missing)), Context(Soft("RenderImage", missing)));
        Check(companion, 2, "companion-icon");
        Require(state.Loaded.Count(p => p.Contains("/Companion_PinkySight.")) == 1, "同じアイテムを繰り返し読み込んでいます");
        Check(companion, 2, "repeat-parse");
        state.Assets[missing.Replace("/OfferCatalog/", "/BRCosmetics/")] = texture;
        Check(Offer("DAv2_Other", Context(Soft("RenderImage", missing))), 1, "relocated-placeholder");
        Check(Offer("DAv2_Other", Context(Soft("RenderImage", "/Missing/T.T"))), 1, "missing-image");

        var material = new UMaterialInstanceConstant();
        var materialPackage = PackageProxy.Create(texture, material);
        var parameter = (FTextureParameterValue) RuntimeHelpers.GetUninitializedObject(typeof(FTextureParameterValue));
        typeof(FTextureParameterValue).GetField(nameof(FTextureParameterValue.ParameterInfo))!.SetValue(parameter, new FMaterialParameterInfo { Name = new FName("OfferImage") });
        typeof(FTextureParameterValue).GetField(nameof(FTextureParameterValue.ParameterValue))!.SetValue(parameter, new FPackageIndex(materialPackage, 1));
        material.TextureParameterValues = [parameter];
        state.Assets["/Example/MI.MI"] = material;
        Check(Offer("DAv2_Legacy", Context(Soft("Material", "/Example/MI.MI"))), 1, "legacy-material");
        Check(Offer("DAv2_New", Context(Soft("OverrideImageMaterial", "/Example/MI.MI"))), 1, "override-material");

        // This color-bearing material previously looped forever when it had no image/parent.
        var empty = new UMaterialInstanceConstant();
        var vector = (FVectorParameterValue) RuntimeHelpers.GetUninitializedObject(typeof(FVectorParameterValue));
        typeof(FVectorParameterValue).GetField(nameof(FVectorParameterValue.ParameterInfo))!.SetValue(vector, new FMaterialParameterInfo { Name = new FName("Background_Color_A") });
        empty.VectorParameterValues = [vector];
        var emptyCreator = new BaseMaterialInstance(empty, EIconStyle.NoBackground);
        emptyCreator.ParseForInfo();
        using (emptyCreator.Draw()[0]) { }
        material.Properties.Add(new FPropertyTag { Name = new FName("Parent"), Tag = new ObjectProperty(new FPackageIndex(materialPackage, 2)) });
        Check(Offer("DAv2_Legacy", Context(Soft("Material", "/Example/MI.MI"))), 1, "cyclic-parent");
        Require(state.MissingLoads == 0, "存在しない画像をロードしようとしました");
    }
}

public class OfferProviderProxy : DispatchProxy
{
    internal readonly Dictionary<string, UObject> Assets = new(StringComparer.OrdinalIgnoreCase);
    internal readonly List<string> Loaded = [];
    internal GameFile File = null!;
    internal int MissingLoads;

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var path = (string) args![0]!;
        if (method!.Name == "TryGetGameFile")
        {
            var exists = Assets.Keys.Any(key => key[..key.LastIndexOf('.')].Equals(path, StringComparison.OrdinalIgnoreCase));
            args[1] = exists ? File : null;
            return exists;
        }
        if (method.Name == "TryLoadPackageObject")
        {
            Loaded.Add(path);
            var exists = Assets.TryGetValue(path, out var export);
            if (!exists) MissingLoads++;
            args[1] = export;
            return exists;
        }
        throw new NotSupportedException(method.Name);
    }
}
