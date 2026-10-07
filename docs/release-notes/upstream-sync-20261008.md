# FModel-JP 次回リリース

最新の上流FModel・CUE4Parseを取り込み、Fortniteの表示アセットのTexturePreviewを改善しました。日本語UI、検索結果の強調表示、MCPサーバー、アセット編集、pak作成などの既存機能も引き続き利用できます。

## 更新内容

- 取得時点の最新FModel・CUE4Parseを統合しました。UAssetAPIも取得時点の最新版であることを確認しています。
- 最新CUE4ParseのVerse読み取り処理に対応しました。UE6のネイティブダイジェストと、通常形式・コンパクト形式のデバッグ情報を使ったソース復元に対応しています。
- ショップの表示アセットで、新しい `RenderImage`・`FullTileOverrideRenderImage`・`OverrideImageMaterial` を扱えるようにしました。従来の `Material` 形式も引き続き対応します。
- `DAv2_Companion_PinkySight.uasset` などのコンパニオン表示アセットは、本来のショップ画像を取得できない場合、対応するアイテムのローカルアイコンをTexturePreviewに表示します。

## 修正内容

- 存在しない画像参照を読み込む際の `KeyNotFoundException` を修正しました。任意の画像参照はファイルの存在を確認してから読み込み、取得できない場合は代替画像を表示します。
- マテリアルに画像がない場合や、親マテリアルの参照が循環している場合に、プレビュー処理が終了しなくなる問題を修正しました。

## Fortnite 42.30以降のNewDisplayAssetsについて

Fortnite 42.30以降、ショップのNewDisplayAssetsで使用する画像はAPI配信に切り替わっています。そのため、ローカルのNewDisplayAssetsファイルだけでは本来のショップ画像を閲覧できません。画像を取得できない場合にプレースホルダー画像が表示されることがあります。

コンパニオンなど、対応するアイコンがローカルに存在する場合は、そのアイコンを代わりに表示します。利用できるアイコンもない場合は、プレースホルダー画像を表示します。今回の更新には、API配信されるショップ画像の直接取得機能は含まれていません。

従来は、存在しない旧プレースホルダーへの参照を読み込もうとすると、次のエラーが発生することがありました。

```text
[ERR] System.Collections.Generic.KeyNotFoundException: There is no game file with the path "FortniteGame/Plugins/GameFeatures/OfferCatalog/Content/Art/A_Shop_Tiles_Textures/T_UI_PlaceholderCube.uasset"
   at CUE4Parse.FileProvider.AbstractFileProvider.get_Item(String path)
```

今回の更新では、この欠落参照を安全に扱い、関連アイコンやプレースホルダーへ切り替えます。

## 動作確認

- 日本語版の互換性チェック9項目に合格しました。
- 実際のPinkySight表示アセットからプレビュー画像を生成し、上記の欠落参照例外が発生しないことを確認しました。
- Releaseビルド、自己完結型Windows x64 EXEの発行、MCPヘルプモードでの起動を確認しました。

関連PR: [#63 最新FModel・CUE4Parse統合とOfferCatalogのTexturePreview修正](https://github.com/Fortniteleakjp/FModel-JP-/pull/63)
