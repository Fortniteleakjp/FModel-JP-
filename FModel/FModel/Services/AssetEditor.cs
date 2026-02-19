using System;
using System.IO;
using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace FModel.Services
{
    public static class AssetEditor
    {
        /// <summary>
        /// 指定されたUAssetファイルを読み込み、編集アクションを適用して、新しいパスに保存します。
        /// </summary>
        /// <param name="sourcePath">元の.uassetファイルのパス</param>
        /// <param name="destinationPath">編集後の.uassetファイルを保存するパス</param>
        /// <param name="engineVersion">アセットのUnreal Engineバージョン</param>
        /// <param name="editAction">UAssetを編集するためのデリゲート</param>
        public static void EditAndSave(string sourcePath, string destinationPath, EngineVersion engineVersion, Action<UAsset> editAction)
        {
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"Source asset not found: {sourcePath}");
            }

            // UAssetAPIを使用してアセットを読み込む
            // .uexpファイルが存在する場合は自動的に処理されます
            var asset = new UAsset(sourcePath, engineVersion);

            // 呼び出し元で定義された編集ロジックを実行
            editAction?.Invoke(asset);

            // 変更されたアセットを指定されたパスに書き込む
            asset.Write(destinationPath);
        }
    }
}