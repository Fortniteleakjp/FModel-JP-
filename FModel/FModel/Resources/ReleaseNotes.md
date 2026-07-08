# FModelJP リリースノート

FModelJP をご利用いただきありがとうございます！
このページは、アップデート後に一度だけ表示されます。

## {version} 主な変更点

### 新機能

- **本家 FModel の最新差分を反映** — [4sval/FModel](https://github.com/4sval/FModel) の dev ブランチ最新状態に合わせて、依存関係・設定画面・解析まわりを更新しました。
- **Loose Files の表示と読み込みを強化** — パッケージ外のファイルを「Loose Files」として一覧に表示し、選択時の読み込み対象にも含められるようになりました。
- **Dead by Daylight の動的コンテンツに対応** — `PersistentDownloadDir\DynamicContent` を自動で追加検索し、追加配信コンテンツを読み込めるようになりました。
- **`.jmap` / `.jmap.gz` マッピングに対応** — 設定画面のマッピング選択で、従来の `.usmap` に加えて `.jmap` と `.jmap.gz` を選べるようになりました。
- **GameFeatureVersePaths の表示に対応** — Verse 関連の Game Feature パス情報を JSON として確認できるようになりました。

### 改善・修正

- **CUE4Parse コアを最新へ同期**（[FabianFG/CUE4Parse](https://github.com/FabianFG/CUE4Parse) HEAD まで）— Lord of Mysteries、Netmarble 系、LuaJIT、Wwise Bitcrush、Shader ハッシュ名など、多数のゲーム/フォーマット解析を更新しました。
- EpicManifestParser v3 系に合わせ、Fortnite Live / On-Demand manifest の取得・展開処理を更新しました。
- Oodle / Zlib / VgmStream の初期化処理を更新し、起動時のダウンロード・展開・ネイティブライブラリ読み込みを安定化しました。
- Valorant の zlib 展開処理を最新の CUE4Parse Compression API に合わせて更新しました。
- Skeletal Mesh の Morph 読み込みで、現在の LOD に対応する Morph データを使うよう修正しました。
- Lies of P / Code Vein 2 / High On Life 2 / Mortal Shell 2 向けのモデル回転補正を更新しました。
- CUE4Parse 更新に伴う型変更へ対応し、ビルドエラーを修正しました。

すべての変更履歴については、[GitHub のリリース](https://github.com/Fortniteleakjp/FModel-JP-/releases) をご確認ください。
