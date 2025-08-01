using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Readers;

namespace CUE4Parse.UE4.Versions;

// Custom serialization version for changes made in Dev-Anim stream
public static class FRigVMObjectVersion
{
    public enum Type
    {
        // Before any version changes were made
        BeforeCustomVersionWasAdded,

        // ControlRig & RigVMHost compute and checks VM Hash
        AddedVMHashChecks,

        // Predicates added to execute operations
        PredicatesAddedToExecuteOps,

        // Storing paths to user defined structs map
        VMStoringUserDefinedStructMap,

        // Storing paths to user defined enums map
        VMStoringUserDefinedEnumMap,

        // Storing paths to user defined enums map
        HostStoringUserDefinedData,

        // VM Memory Storage Struct serialized
        VMMemoryStorageStructSerialized,

        // VM Memory Storage Defaults generated at VM
        VMMemoryStorageDefaultsGeneratedAtVM,

        // VM Bytecode Stores the Public Context Path
        VMBytecodeStorePublicContextPath,

        // Removing unused tooltip property from frunction header
        VMRemoveTooltipFromFunctionHeader,

        // Removing library node FSoftObjectPath from FRigVMGraphFunctionIdentifier
        RemoveLibraryNodeReferenceFromFunctionIdentifier,

        // Adding variant struct to function identifier
        AddVariantToFunctionIdentifier,

        // Adding variant to every RigVM asset
        AddVariantToRigVMAssets,

        // Storing user interface layout within function header
        FunctionHeaderStoresLayout,

        // Storing user interface relevant pin index in category
        FunctionHeaderLayoutStoresPinIndexInCategory,

        // Storing user interface relevant category expansion
        FunctionHeaderLayoutStoresCategoryExpansion,

        // Storing function graph collapse node content as part of the header
        RigVMSaveSerializedGraphInGraphFunctionDataAsByteArray,

        // VM Bytecode Stores the Public Context Path as a FTopLevelAssetPath
        VMBytecodeStorePublicContextPathAsTopLevelAssetPath,

        // Serialized instruction offsets are now int32 rather than uint16, NumBytes has been removed
        // from RigVMCopyOp
        ByteCodeCleanup,

        // The VM stores a local snapshot registry to use in cooked environments instead of the shared global registry
        LocalizedRegistry,

        // -----<new versions can be added above this line>-------------------------------------------------
        VersionPlusOne,
        LatestVersion = VersionPlusOne - 1,
    }

    public static readonly FGuid GUID = new(0xDC49959B, 0x53C04DE7, 0x9156EA88, 0x5E7C5D39);

    public static Type Get(FArchive Ar)
    {
        var ver = Ar.CustomVer(GUID);
        if (ver != -1)
            return (Type) ver;

        return Ar.Game switch
        {
            < EGame.GAME_UE5_3 => (Type) (-1),
            < EGame.GAME_UE5_4 => Type.PredicatesAddedToExecuteOps,
            < EGame.GAME_UE5_5 => Type.VMRemoveTooltipFromFunctionHeader,
            < EGame.GAME_UE5_6 => Type.FunctionHeaderLayoutStoresCategoryExpansion,
            < EGame.GAME_UE5_7 => Type.ByteCodeCleanup,
            _ => Type.LatestVersion
        };
    }
}
