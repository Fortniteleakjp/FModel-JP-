using System.Threading.Tasks;

namespace FModel.Features.Athena
{
    public class GenerateAllCosmeticsFeature : AthenaFeatureBase
    {
        public static async Task ExecuteAsync()
        {
            await GenerateProfile("https://fortnite-api.com/v2/cosmetics", "athena.json", "すべてのコスメティックのプロファイルを生成中...");
        }
    }
}