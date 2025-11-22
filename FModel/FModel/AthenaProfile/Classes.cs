using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;

namespace FModel.AthenaProfile
{
    public class UFortCosmeticMeshVariant : UObject
    {
        public static string GetName()
        {
            return "FortCosmeticMeshVariant";
        }
    }

    public class UFortCosmeticPropertyVariant : UObject
    {
        public static string GetName()
        {
            return "FortCosmeticPropertyVariant";
        }
    }

    public class UFortCosmeticMaterialVariant : UObject
    {
        public static string GetName()
        {
            return "FortCosmeticMaterialVariant";
        }
    }

    public class UFortCosmeticCharacterPartVariant : UObject
    {
        public static string GetName()
        {
            return "FortCosmeticCharacterPartVariant";
        }
    }

    public class UFortCosmeticGameplayTagVariant : UObject
    {
        public static string GetName()
        {
            return "FortCosmeticGameplayTagVariant";
        }
    }

    public class UFortCosmeticParticleVariant : UObject
    {
        public static string GetName()
        {
            return "FortCosmeticParticleVariant";
        }
    }
}
