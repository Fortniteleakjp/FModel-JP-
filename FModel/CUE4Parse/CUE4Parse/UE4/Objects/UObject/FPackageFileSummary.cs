using System;
using System.Runtime.InteropServices;
using System.Text;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;
using Serilog;
using static CUE4Parse.UE4.Objects.Core.Misc.ECompressionFlags;

namespace CUE4Parse.UE4.Objects.UObject
{
    /// <summary>
    /// Revision data for an Unreal package file.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct FGenerationInfo
    {
        /**
         * Number of exports in the linker's ExportMap for this generation.
         */
        public readonly int ExportCount;

        /**
         * Number of names in the linker's NameMap for this generation.
         */
        public readonly int NameCount;
    }

    [JsonConverter(typeof(FPackageFileSummaryConverter))]
    public class FPackageFileSummary
    {
        public const uint PACKAGE_FILE_TAG = 0x9E2A83C1U;
        public const uint PACKAGE_FILE_TAG_SWAPPED = 0xC1832A9EU;
        public const uint PACKAGE_FILE_TAG_ACE7 = 0x37454341U; // ACE7
        private const uint PACKAGE_FILE_TAG_ONE = 0x00656E6FU; // SOD2
        private const uint PACKAGE_FILE_TAG_NTE = 0xD5A8D56E;
        private const uint PACKAGE_FILE_TAG_AE = 0x56DE5ECA; // AshEchoes

        public readonly uint Tag;
        public FPackageFileVersion FileVersionUE;
        public EUnrealEngineObjectLicenseeUEVersion FileVersionLicenseeUE;
        public FCustomVersionContainer CustomVersionContainer;
        public EPackageFlags PackageFlags;
        public int TotalHeaderSize;
        public readonly string PackageName;
        public int NameCount;
        public int NameOffset;
        public readonly int SoftObjectPathsCount;
        public readonly int SoftObjectPathsOffset;
        public readonly string? LocalizationId;
        public readonly int GatherableTextDataCount;
        public readonly int GatherableTextDataOffset;
        public int MetaDataOffset;
        public int ExportCount;
        public int ExportOffset;
        public int ImportCount;
        public int ImportOffset;
        public int CellExportCount;
        public int CellExportOffset;
        public int CellImportCount;
        public int CellImportOffset;
        public readonly int DependsOffset;
        public readonly int SoftPackageReferencesCount;
        public readonly int SoftPackageReferencesOffset;
        public readonly int SearchableNamesOffset;
        public readonly int ThumbnailTableOffset;
        public FSHAHash SavedHash;
        public readonly FGuid Guid;
        public readonly FGuid PersistentGuid;
        public readonly FGenerationInfo[] Generations;
        public readonly FEngineVersion? SavedByEngineVersion;
        public readonly FEngineVersion? CompatibleWithEngineVersion;
        public readonly ECompressionFlags CompressionFlags;
        public readonly int PackageSource;
        public bool bUnversioned;
        public readonly int AssetRegistryDataOffset;
        public int BulkDataStartOffset; // serialized as long
        public readonly int WorldTileInfoDataOffset;
        public readonly int[] ChunkIds;
        public readonly int PreloadDependencyCount;
        public readonly int PreloadDependencyOffset;
        public readonly int NamesReferencedFromExportDataCount;
        public readonly long PayloadTocOffset;
        public readonly int DataResourceOffset;

        public FPackageFileSummary()
        {
            CustomVersionContainer = new FCustomVersionContainer();
            PackageName = string.Empty;
            Generations = Array.Empty<FGenerationInfo>();
            ChunkIds = Array.Empty<int>();
        }

        internal FPackageFileSummary(FArchive Ar)
        {
            Tag = Ar.Read<uint>();

            /*
             * The package file version number when this package was saved.
             *
             * Lower 16 bits stores the UE3 engine version
             * Upper 16 bits stores the UE4/licensee version
             * For newer packages this is -7
             *		-2 indicates presence of enum-based custom versions
             *		-3 indicates guid-based custom versions
             *		-4 indicates removal of the UE3 version. Packages saved with this ID cannot be loaded in older engine versions
             *		-5 indicates the replacement of writing out the "UE3 version" so older versions of engine can gracefully fail to open newer packages
             *		-6 indicates optimizations to how custom versions are being serialized
             *		-7 indicates the texture allocation info has been removed from the summary
             *		-8 indicates that the UE5 version has been added to the summary
             *      -9 indicates a contractual change in when early exits are required based on FileVersionTooNew. At or
             *		   after this LegacyFileVersion, we support changing the PackageFileSummary serialization format for
             *		   all bytes serialized after FileVersionLicensee, and that format change can be conditional on any
             *		   of the versions parsed before that point. All packageloaders that understand the -9
             *		   legacyfileformat are required to early exit without further serialization at that point if
             *		   FileVersionTooNew is true.
             */
            const int CurrentLegacyFileVersion = -9;
            var legacyFileVersion = CurrentLegacyFileVersion;

            if (Tag == PACKAGE_FILE_TAG_ONE) // SOD2, "one"
            {
                Ar.Game = EGame.GAME_StateOfDecay2;
                Ar.Ver = Ar.Game.GetVersion();
                legacyFileVersion = Ar.Read<int>(); // seems to be always int.MinValue
                bUnversioned = true;
                FileVersionUE = Ar.Ver;
                CustomVersionContainer = new FCustomVersionContainer();
                PackageName = "None";
                PackageFlags = EPackageFlags.PKG_FilterEditorOnly;
                goto afterPackageFlags;
            }

            if (Tag == PACKAGE_FILE_TAG_NTE && Ar.Game == EGame.GAME_NevernessToEverness_CBT1)
            {
                var keyData = Ar.Read<FGuid>();
                var decryptedDataLength = Ar.Read<int>();
                _ = Ar.Read<int>(); // paddedEncryptedDataLength
                var encryptedData = Ar.ReadArray<byte>();
                var isKeyObfuscated = Ar.ReadBoolean();

                if (isKeyObfuscated)
                {
                    keyData = new FGuid(
                        keyData.A ^ keyData.D,
                        keyData.B ^ keyData.C,
                        keyData.B,
                        keyData.A);
                }

                var key = new FAesKey(Encoding.UTF8.GetBytes(keyData.ToString()));
                var paddedDecryptedData = encryptedData.Decrypt(key);
                var decryptedData = paddedDecryptedData[..decryptedDataLength];

                Ar = new FByteArchive("NTE - Decrypted FPackageFileSummary", decryptedData, Ar.Versions);
                Tag = Ar.Read<uint>();
            }

            if (Tag == PACKAGE_FILE_TAG_AE) Tag = PACKAGE_FILE_TAG;

            if (Tag != PACKAGE_FILE_TAG && Tag != PACKAGE_FILE_TAG_SWAPPED)
            {
                throw new ParserException($"Invalid uasset magic: 0x{Tag:X8} != 0x{PACKAGE_FILE_TAG:X8}");
            }

            // The package has been stored in a separate endianness than the linker expected so we need to force
            // endian conversion. Latent handling allows the PC version to retrieve information about cooked packages.
            if (Tag == PACKAGE_FILE_TAG_SWAPPED)
            {
                // Set proper tag.
                //Tag = PACKAGE_FILE_TAG;
                // Toggle forced byte swapping.
                throw new ParserException("Byte swapping for packages not supported");
            }

            legacyFileVersion = Ar.Read<int>();
            if (Ar.Game == EGame.GAME_DeltaForceHawkOps) legacyFileVersion /= 659;

            if (legacyFileVersion < 0) // means we have modern version numbers
            {
                if (legacyFileVersion < CurrentLegacyFileVersion)
                {
                    // we can't safely load more than this because the legacy version code differs in ways we can not predict.
                    // Make sure that the linker will fail to load with it.
                    FileVersionUE.Reset();
                    FileVersionLicenseeUE = 0;
                    throw new ParserException("Can't load legacy UE3 file");
                }

                if (legacyFileVersion != -4)
                {
                    var legacyUE3Version = Ar.Read<int>();
                }

                FileVersionUE.FileVersionUE4 = Ar.Read<int>();
                if (Ar.Game == EGame.GAME_DaysGone) FileVersionUE.FileVersionUE4 = 498;

                if (legacyFileVersion <= -8)
                {
                    FileVersionUE.FileVersionUE5 = Ar.Read<int>();
                }

                FileVersionLicenseeUE = Ar.Read<EUnrealEngineObjectLicenseeUEVersion>();

                if (FileVersionUE != EUnrealEngineObjectUE4Version.DETERMINE_BY_GAME &&
                    FileVersionUE < EUnrealEngineObjectUE4Version.OLDEST_LOADABLE_PACKAGE ||
                    FileVersionUE > EUnrealEngineObjectUE4Version.AUTOMATIC_VERSION ||
                    FileVersionUE > EUnrealEngineObjectUE5Version.AUTOMATIC_VERSION)
                {
                    Log.Warning("File version is too new or too old");
                }

                if (FileVersionUE >= EUnrealEngineObjectUE5Version.PACKAGE_SAVED_HASH)
                {
                    SavedHash = new FSHAHash(Ar);
                    TotalHeaderSize = Ar.Read<int>();
                }

                CustomVersionContainer = new FCustomVersionContainer(Ar, FCustomVersionContainer.DetermineSerializationFormat(legacyFileVersion));

                if (Ar.Versions.CustomVersions == null && CustomVersionContainer.Versions.Length > 0)
                {
                    Ar.Versions.CustomVersions = CustomVersionContainer;
                }

                if (FileVersionUE.FileVersionUE4 == 0 && FileVersionUE.FileVersionUE5 == 0 && FileVersionLicenseeUE == 0)
                {
                    // this file is unversioned, remember that, then use current versions
                    bUnversioned = true;
                    FileVersionUE = Ar.Ver;
                    FileVersionLicenseeUE = EUnrealEngineObjectLicenseeUEVersion.VER_LIC_AUTOMATIC_VERSION;
                }
                else
                {
                    bUnversioned = false;
                    // Only apply the version if an explicit version is not set
                    if (!Ar.Versions.bExplicitVer)
                    {
                        Ar.Ver = FileVersionUE;
                    }
                }
            }
            else
            {
                // This is probably an old UE3 file, make sure that the linker will fail to load with it.
                throw new ParserException("Can't load legacy UE3 file");
            }

            if (FileVersionUE < EUnrealEngineObjectUE5Version.PACKAGE_SAVED_HASH)
            {
                TotalHeaderSize = Ar.Read<int>();
            }

            PackageName = Ar.ReadFString(); // PackageGroup
            PackageFlags = Ar.Read<EPackageFlags>();

            /*if (PackageFlags.HasFlag(EPackageFlags.PKG_FilterEditorOnly))
            {
                TODO Ar.SetFilterEditorOnly(true);
            }*/

            afterPackageFlags:
            NameCount = Ar.Read<int>();
            NameOffset = Ar.Read<int>();

            if (FileVersionUE >= EUnrealEngineObjectUE5Version.ADD_SOFTOBJECTPATH_LIST)
            {
                SoftObjectPathsCount = Ar.Read<int>();
                SoftObjectPathsOffset = Ar.Read<int>();
            }

            if (!PackageFlags.HasFlag(EPackageFlags.PKG_FilterEditorOnly))
            {
                if (FileVersionUE >= EUnrealEngineObjectUE4Version.ADDED_PACKAGE_SUMMARY_LOCALIZATION_ID)
                {
                    LocalizationId = Ar.ReadFString();
                }
            }

            if (FileVersionUE >= EUnrealEngineObjectUE4Version.SERIALIZE_TEXT_IN_PACKAGES)
            {
                GatherableTextDataCount = Ar.Read<int>();
                GatherableTextDataOffset = Ar.Read<int>();
            }

            ExportCount = Ar.Read<int>();
            ExportOffset = Ar.Read<int>();
            ImportCount = Ar.Read<int>();
            ImportOffset = Ar.Read<int>();

            if (FileVersionUE >= EUnrealEngineObjectUE5Version.VERSE_CELLS)
            {
                CellExportCount = Ar.Read<int>();
                CellExportOffset = Ar.Read<int>();
                CellImportCount = Ar.Read<int>();
                CellImportOffset = Ar.Read<int>();
            }

            if (FileVersionUE >= EUnrealEngineObjectUE5Version.METADATA_SERIALIZATION_OFFSET)
            {
                MetaDataOffset = Ar.Read<int>();
            }

            DependsOffset = Ar.Read<int>();

            if (FileVersionUE < EUnrealEngineObjectUE4Version.OLDEST_LOADABLE_PACKAGE || FileVersionUE > EUnrealEngineObjectUE4Version.AUTOMATIC_VERSION)
            {
                Generations = [];
                ChunkIds = [];
                return; // we can't safely load more than this because the below was different in older files.
            }

            if (FileVersionUE >= EUnrealEngineObjectUE4Version.ADD_STRING_ASSET_REFERENCES_MAP)
            {
                SoftPackageReferencesCount = Ar.Read<int>();
                SoftPackageReferencesOffset = Ar.Read<int>();
            }

            if (FileVersionUE >= EUnrealEngineObjectUE4Version.ADDED_SEARCHABLE_NAMES)
            {
                SearchableNamesOffset = Ar.Read<int>();
            }

            ThumbnailTableOffset = Ar.Read<int>();

            if (FileVersionUE < EUnrealEngineObjectUE5Version.PACKAGE_SAVED_HASH)
            {
                Guid = Ar.Read<FGuid>();
            }

            if (Ar.Game is EGame.GAME_Valorant_PRE_11_2 or EGame.GAME_HYENAS) Ar.Position += 8;

            if (!PackageFlags.HasFlag(EPackageFlags.PKG_FilterEditorOnly))
            {
                if (FileVersionUE >= EUnrealEngineObjectUE4Version.ADDED_PACKAGE_OWNER)
                {
                    PersistentGuid = Ar.Read<FGuid>();
                }
                else
                {
                    // By assigning the current package guid, we maintain a stable persistent guid, so we can reference this package even if it wasn't resaved.
                    PersistentGuid = Guid;
                }

                // The owner persistent guid was added in VER_UE4_ADDED_PACKAGE_OWNER but removed in the next version VER_UE4_NON_OUTER_PACKAGE_IMPORT
                if (FileVersionUE >= EUnrealEngineObjectUE4Version.ADDED_PACKAGE_OWNER && FileVersionUE < EUnrealEngineObjectUE4Version.NON_OUTER_PACKAGE_IMPORT)
                {
                    var ownerPersistentGuid = Ar.Read<FGuid>();
                }
            }

            Generations = Ar.ReadArray<FGenerationInfo>();

            if (Ar.Game == EGame.GAME_DeltaForceHawkOps) Ar.Position += 16;

            if (FileVersionUE >= EUnrealEngineObjectUE4Version.ENGINE_VERSION_OBJECT)
            {
                SavedByEngineVersion = new FEngineVersion(Ar);
                FixCorruptEngineVersion(FileVersionUE, SavedByEngineVersion);
            }
            else
            {
                var engineChangelist = Ar.Read<int>();

                if (engineChangelist != 0)
                {
                    SavedByEngineVersion = new FEngineVersion(4, 0, 0, (uint) engineChangelist, string.Empty);
                }
            }

            if (FileVersionUE >= EUnrealEngineObjectUE4Version.PACKAGE_SUMMARY_HAS_COMPATIBLE_ENGINE_VERSION)
            {
                CompatibleWithEngineVersion = new FEngineVersion(Ar);
                FixCorruptEngineVersion(FileVersionUE, CompatibleWithEngineVersion);
            }
            else
            {
                CompatibleWithEngineVersion = SavedByEngineVersion;
            }

            static bool VerifyCompressionFlagsValid(int compressionFlags)
            {
                const int CompressionFlagsMask = (int) (COMPRESS_DeprecatedFormatFlagsMask | COMPRESS_OptionsFlagsMask | COMPRESS_ForPurposeMask);
                return (compressionFlags & ~CompressionFlagsMask) == 0;
            }

            CompressionFlags = Ar.Read<ECompressionFlags>();

            if (!VerifyCompressionFlagsValid((int) CompressionFlags))
            {
                throw new ParserException($"Invalid compression flags ({(uint) CompressionFlags})");
            }

            var compressedChunks = Ar.ReadArray<FCompressedChunk>();

            if (compressedChunks.Length > 0)
            {
                throw new ParserException("Package level compression is enabled");
            }

            PackageSource = Ar.Read<int>();

            if (Ar.Game == EGame.GAME_ArkSurvivalEvolved && (int) FileVersionLicenseeUE >= 10)
            {
                Ar.Position += 8;
            }

            // No longer used: List of additional packages that are needed to be cooked for this package (ie streaming levels)
            // Keeping the serialization code for backwards compatibility without bumping the package version
            var additionalPackagesToCook = Ar.ReadArray(Ar.ReadFString);

            if (legacyFileVersion > -7)
            {
                var numTextureAllocations = Ar.Read<int>();
                if (numTextureAllocations != 0)
                {
                    // We haven't used texture allocation info for ages and it's no longer supported anyway
                    throw new ParserException("NumTextureAllocations != 0");
                }
            }

            if (FileVersionUE >= EUnrealEngineObjectUE4Version.ASSET_REGISTRY_TAGS)
            {
                AssetRegistryDataOffset = Ar.Read<int>();
            }

            if (Ar.Game == EGame.GAME_TowerOfFantasy)
            {
                TotalHeaderSize = (int) (TotalHeaderSize ^ 0xEEB2CEC7);
                NameCount = (int) (NameCount ^ 0xEEB2CEC7);
                NameOffset = (int) (NameOffset ^ 0xEEB2CEC7);
                ExportCount = (int) (ExportCount ^ 0xEEB2CEC7);
                ExportOffset = (int) (ExportOffset ^ 0xEEB2CEC7);
                ImportCount = (int) (ImportCount ^ 0xEEB2CEC7);
                ImportOffset = (int) (ImportOffset ^ 0xEEB2CEC7);
                DependsOffset = (int) (DependsOffset ^ 0xEEB2CEC7);
                PackageSource = (int) (PackageSource ^ 0xEEB2CEC7);
                AssetRegistryDataOffset = (int) (AssetRegistryDataOffset ^ 0xEEB2CEC7);
            }

            if (Ar.Game is EGame.GAME_SeaOfThieves or EGame.GAME_GearsOfWar4)
            {
                Ar.Position += 6; // no idea what's going on here.
            }

            if (FileVersionUE >= EUnrealEngineObjectUE4Version.SUMMARY_HAS_BULKDATA_OFFSET)
            {
                BulkDataStartOffset = (int) Ar.Read<long>();
            }

            if (FileVersionUE >= EUnrealEngineObjectUE4Version.WORLD_LEVEL_INFO)
            {
                WorldTileInfoDataOffset = Ar.Read<int>();
            }

            if (FileVersionUE >= EUnrealEngineObjectUE4Version.CHANGED_CHUNKID_TO_BE_AN_ARRAY_OF_CHUNKIDS)
            {
                ChunkIds = Ar.ReadArray<int>();
            }
            else if (FileVersionUE >= EUnrealEngineObjectUE4Version.ADDED_CHUNKID_TO_ASSETDATA_AND_UPACKAGE)
            {
                var chunkId = Ar.Read<int>();
                ChunkIds = chunkId < 0 ? [] : [chunkId];
            }
            else
            {
                ChunkIds = [];
            }

            if (FileVersionUE >= EUnrealEngineObjectUE4Version.PRELOAD_DEPENDENCIES_IN_COOKED_EXPORTS)
            {
                PreloadDependencyCount = Ar.Read<int>();
                PreloadDependencyOffset = Ar.Read<int>();
            }
            else
            {
                PreloadDependencyCount = -1;
                PreloadDependencyOffset = 0;
            }

            NamesReferencedFromExportDataCount = FileVersionUE >= EUnrealEngineObjectUE5Version.NAMES_REFERENCED_FROM_EXPORT_DATA ? Ar.Read<int>() : NameCount;
            PayloadTocOffset = FileVersionUE >= EUnrealEngineObjectUE5Version.PAYLOAD_TOC ? Ar.Read<long>() : -1;
            DataResourceOffset = FileVersionUE >= EUnrealEngineObjectUE5Version.DATA_RESOURCES || Ar.Game == EGame.GAME_TheFirstDescendant ? Ar.Read<int>() : -1;

            if (Tag == PACKAGE_FILE_TAG_ONE && Ar is FAssetArchive assetAr)
            {
                assetAr.AbsoluteOffset = NameOffset - (int) Ar.Position;
            }
        }

        private static void FixCorruptEngineVersion(FPackageFileVersion objectVersion, FEngineVersion version)
        {
            if (objectVersion < EUnrealEngineObjectUE4Version.CORRECT_LICENSEE_FLAG
                && version is { Major: 4, Minor: 26, Patch: 0, Changelist: >= 12740027 }
                && version.IsLicenseeVersion())
            {
                version.Set(4, 26, 0, version.Changelist, version.Branch);
            }
        }
    }
}