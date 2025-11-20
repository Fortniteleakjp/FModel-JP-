using System.Threading.Tasks;

namespace FModel.Features.Athena
{
    public class GenerateNewCosmeticsWithPaksFeature : AthenaFeatureBase
    {
        public static async Task ExecuteAsync()
        {
            await GenerateProfile("https://fortnite-api.com/v2/cosmetics/new", "athena_new_paks.json", "新しいコスメティックのプロファイルを生成中...", true);
        }
    }
}