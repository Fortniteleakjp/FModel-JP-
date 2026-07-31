# FModel-JP リリースノート

FModel-JP をご利用いただきありがとうございます。このページには、主な機能追加と処理変更を記録します。

## {version} 主な変更点

### 新機能

- Animation Blueprint グラフビューアを更新しました。
  - AnimGraph の出力層、Function 層、統合グラフを表示できます。
  - StateMachine の State / Conduit / 遷移と State サブグラフを確認できます。
  - ノードの接続、ピン、コメント、プロパティを表示できます。
- 新エクスプローラーを起動時の標準表示に変更しました。検索、クラス絞り込み、パス移動、戻る・進む、グリッド表示を利用できます。
- CUE4Parse を [`a098f0b6`](https://github.com/FabianFG/CUE4Parse/tree/a098f0b6f87372e95d42701216159961eb691948) に更新し、AnimBlueprint の新 API を使用するようにしました。
- Fortnite Cloud Archives と On-Demand アーカイブの読み込みに対応しました。
- チャンク、マニフェスト、マッピングを専用キャッシュディレクトリへ整理し、旧配置から自動移行します。

### 大規模データ向けの改善

- 大量のパッケージを含むマニフェストのフォルダツリー構築をバックグラウンド化しました。
- フォルダのパス検索と Go To をインデックス化し、深い階層への移動を高速化しました。
- パッケージ一覧とアーカイブ一覧の UI 更新をバッチ化しました。
- アセットプレビューとパッケージ解決の同時実行数を制限し、読み込み中の UI 応答性を改善しました。

### 参照 PR

- [PR #693](https://github.com/4sval/FModel/pull/693)
- [PR #689](https://github.com/4sval/FModel/pull/689)
- [PR #656](https://github.com/4sval/FModel/pull/656)

### その他

- 本家 FModel の dev ブランチおよび CUE4Parse の更新を取り込みました。
- 既存の日本語 UI、比較機能、詳細検索、履歴、AES 取得機能との互換性を維持しています。

詳細な変更履歴は [GitHub Releases](https://github.com/Fortniteleakjp/FModel-JP-/releases) をご確認ください。
