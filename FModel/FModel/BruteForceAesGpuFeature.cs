using System.Threading.Tasks;
using FModel;
using FModel.Views.Resources.Controls;

namespace FModel.Features.Athena;

public static class BruteForceAesGpuFeature
{
    public static async Task ExecuteAsync()
    {
        FLogger.Append(ELog.Information, () => FLogger.Text("GPUバックエンドでBrute Force AESを実行します。", Constants.WHITE));
        await BruteForceAesFeature.ExecuteAsync(true);
    }
}
