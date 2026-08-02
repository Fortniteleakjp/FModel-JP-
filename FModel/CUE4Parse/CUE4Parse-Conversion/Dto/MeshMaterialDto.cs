using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.UObject;

namespace CUE4Parse_Conversion.Dto;

// PR #358 back-port (JP 適合):
// 本家 head では Material を FPackageIndex? で保持するが、JP ベースの FStaticMaterial/FSkeletalMaterial は
// マテリアル参照を解決済みの ResolvedObject? で保持するため、ここでは ResolvedObject? に適合させている。
// 後続レイヤー(エクスポータ)では Material?.Object?.Value で UMaterialInterface を取得する。
public readonly struct MeshMaterialDto
{
    public readonly string SlotName;
    public readonly ResolvedObject? Material;

    public MeshMaterialDto(string? slotName, ResolvedObject? material = null)
    {
        Material = material;
        SlotName = material?.Name.Text ?? slotName ?? "None";
    }

    // head の MeshMaterialDto(string?, FPackageIndex?) 相当（ランドスケープ等が FPackageIndex を直接渡す経路用）。
    public MeshMaterialDto(string? slotName, FPackageIndex? material) : this(slotName, material?.ResolvedObject)
    {
    }

    public MeshMaterialDto(FStaticMaterial material)
        : this(material.ImportedMaterialSlotName.Text, material.MaterialInterface)
    {
    }

    public MeshMaterialDto(FSkeletalMaterial material)
        : this(material.ImportedMaterialSlotName?.Text ?? material.MaterialSlotName.Text, material.Material)
    {
    }
}
