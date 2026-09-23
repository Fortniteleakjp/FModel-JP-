using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Verse;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Objects.UObject;
using Newtonsoft.Json;
using static CUE4Parse.UE4.Versions.EGame;

namespace FModel.Services.Verse;

/// <summary>
/// Fixes for Verse exports of current Fortnite UE6 packages (42.x+) that CUE4Parse does not read
/// correctly yet. Kept in FModel instead of patching the CUE4Parse submodule: the classes below replace
/// CUE4Parse's own under the same serialized class names, so the submodule stays pristine.
/// Drop an override once CUE4Parse handles that export itself.
/// </summary>
public static class VerseExportOverrides
{
    /// <summary>Must run before any package is loaded.</summary>
    public static void Register()
    {
        ObjectTypeRegistry.RegisterClass("VerseStruct", typeof(FixedVerseStruct));
        ObjectTypeRegistry.RegisterClass("VerseDebugData", typeof(FixedVerseDebugData));
        ObjectTypeRegistry.RegisterClass("VerseFunction", typeof(FixedVerseFunction));
    }
}

/// <summary>Replaces <see cref="UVerseStruct"/>.</summary>
public class FixedVerseStruct : UScriptStruct
{
    public override void Deserialize(FAssetArchive Ar, long validPos)
    {
        // UE5.6 introduced a leading bIsNativeCooked flag for Verse structs. That field is no
        // longer present in the UE6 serialization used by current Fortnite builds (42.x+).
        // Reading it on GAME_UE6_0 consumes the first four bytes of the normal UScriptStruct
        // payload and produces values such as 0x0B010400 as an invalid bool.
        var bIsNativeCooked = Ar.Game is >= GAME_UE5_6 and < GAME_UE6_0 && Ar.ReadBoolean();
        if (!bIsNativeCooked) base.Deserialize(Ar, validPos);
    }
}

/// <summary>Replaces <see cref="UVerseDebugData"/>.</summary>
public class FixedVerseDebugData : UObject
{
    public UVerseDebugData.FSolarisPackageDebugData? DebugData;

    public override void Deserialize(FAssetArchive Ar, long validPos)
    {
        base.Deserialize(Ar, validPos);

        // UE5 cooked VerseDebugData used an extra trailing presence bool followed by
        // FSolarisPackageDebugData. Current Fortnite UE6 packages no longer use that trailing
        // layout; reading four bytes here consumes unrelated data (observed value: 2) and throws
        // ParserException. FModel's Verse recovery reads the UE6 Solaris debug payload directly
        // from the cooked package bytes instead.
        if (Ar.Game >= GAME_UE6_0) return;

        if (Ar.ReadBoolean())
            DebugData = new UVerseDebugData.FSolarisPackageDebugData(Ar);
    }

    protected override void WriteJson(JsonWriter writer, JsonSerializer serializer)
    {
        base.WriteJson(writer, serializer);
        writer.WritePropertyName(nameof(DebugData));
        serializer.Serialize(writer, DebugData);
    }
}

/// <summary>CUE4Parse has no VerseFunction class, it falls back to a plain UObject.</summary>
public class FixedVerseFunction : UFunction
{
    public FName AlternateName;

    public override void Deserialize(FAssetArchive Ar, long validPos)
    {
        base.Deserialize(Ar, validPos);

        // Current Fortnite UE6 packages (42.x+) append one natively serialized FName after the
        // UFunction payload. Every observed value is None; without reading it each VerseFunction
        // export logs "Did not read VerseFunction correctly, 8 bytes remaining".
        if (Ar.Game >= GAME_UE6_0 && validPos - Ar.Position >= 8)
            AlternateName = Ar.ReadFName();
    }

    protected override void WriteJson(JsonWriter writer, JsonSerializer serializer)
    {
        base.WriteJson(writer, serializer);

        if (AlternateName.IsNone) return;
        writer.WritePropertyName(nameof(AlternateName));
        serializer.Serialize(writer, AlternateName);
    }
}
