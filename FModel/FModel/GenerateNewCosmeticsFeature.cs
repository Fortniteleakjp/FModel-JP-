using System.Threading.Tasks;

namespace FModel.Features.Athena
{
    public class GenerateNewCosmeticsFeature : AthenaFeatureBase
    {
        public static async Task ExecuteAsync()
        {
            await GenerateProfile("https://fortnite-api.com/v2/cosmetics/new", "athena_new.json", "新しいコスメティックのプロファイルを生成中...");
        }
    }
}