using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FModel.AthenaProfile
{
    internal class Filters
    {
        public static IEnumerable<string> ItemDefinition =
        [
            // バトルロイヤル
            "AthenaCharacterItemDefinition", // Character
            "AthenaBackpackItemDefinition", "AthenaPetItemDefinition", "AthenaPetCarrierItemDefinition", // Backpack
            "AthenaPickaxeItemDefinition", // Pickaxe
            "AthenaGliderItemDefinition", // Glider
            "AthenaSkyDiveContrailItemDefinition", // SkyDiveContrail
            "AthenaDanceItemDefinition", "AthenaEmojiItemDefinition", "AthenaSprayItemDefinition", "AthenaToyItemDefinition", // Dance
            "AthenaItemWrapDefinition", // ItemWrap
            "AthenaMusicPackItemDefinition", // MusicPack
            "AthenaLoadingScreenItemDefinition", // LoadingScreen

            //フェスティバル
            "SparksGuitarItemDefinition",
            "SparksBassItemDefinition",
            "SparksDrumItemDefinition",
            "SparksMicItemDefinition"
        ];
    }
}
