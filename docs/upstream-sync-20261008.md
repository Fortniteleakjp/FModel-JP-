# 2026-10-08 上流統合記録

## ブランチ

| 用途 | ローカルブランチ | 起点 |
| --- | --- | --- |
| 統合・今後の作業 | `feature/upstream-sync-20261008` | 日本語版の `origin/main`、`eb53f5c787c2f913795bda94502d118c639ac290` |
| 作業開始時のバックアップ | `backup/pre-upstream-sync-20261008` | `c318a4b9109a46ccee7663c08579528e5ab6a48d` |
| 上流FModel原本 | `upstream/fmodel-20261008` | `858461d19e29803479555059acb59a03a0986de8` |
| 日本語版の既存main | `main` | `origin/main` に早送り |

作業開始時の `feature/search-path-highlighting` は日本語版mainに取り込み済みで、
mainの追加コミットはそのマージコミットでした。統合ブランチはこの最新mainから作成しました。
上流原本ブランチには日本語版の変更を加えていません。

既存のローカルブランチ15本は、コミットを削除せず、次の名前に整理しました。
以前からある `backup/pre-upstream-sync-20260915` も維持しています。
リモート側のブランチ削除・push・リリースは行っていません。

| 旧名 | 保全先 |
| --- | --- |
| `5.0` | `archive/20261008/5.0` |
| `FModelJP-Renewal` | `archive/20261008/FModelJP-Renewal` |
| `backup_messed_up` | `archive/20261008/backup_messed_up` |
| `beta` | `archive/20261008/beta` |
| `feature/cue4parse-latest` | `archive/20261008/feature/cue4parse-latest` |
| `feature/mcp-server` | `archive/20261008/feature/mcp-server` |
| `feature/search-path-highlighting` | `archive/20261008/feature/search-path-highlighting` |
| `feature/upstream-dev-sync` | `archive/20261008/feature/upstream-dev-sync` |
| `main` の更新前の先端 | `archive/20261008/main` |
| `my-new-branch` | `archive/20261008/my-new-branch` |
| `pr-2` | `archive/20261008/pr-2` |
| `pr-22` | `archive/20261008/pr-22` |
| `pr-619` | `archive/20261008/pr-619` |
| `pr-656` | `archive/20261008/pr-656` |
| `pr-689` | `archive/20261008/pr-689` |

## 取り込み元

取得時点の公式開発ブランチ先端を使用しています。FModelのデフォルトブランチは `dev`、
CUE4ParseとUAssetAPIは `master` です。

| プロジェクト | 取り込みコミット | 変更 |
| --- | --- | --- |
| [FModel](https://github.com/4sval/FModel/commit/858461d19e29803479555059acb59a03a0986de8) | `858461d19e29803479555059acb59a03a0986de8` | `upstream/dev` をマージ |
| [CUE4Parse](https://github.com/FabianFG/CUE4Parse/commit/2c4dca1d466b59aa36dd7ba235d10478a78074cc) | `2c4dca1d466b59aa36dd7ba235d10478a78074cc` | `cb72c6e1` から更新 |
| [UAssetAPI](https://github.com/atenfyr/UAssetAPI/commit/3228c1e86261aa08131f7ec0ff1a395f5d0b2a84) | `3228c1e86261aa08131f7ec0ff1a395f5d0b2a84` | すでに最新版であることをfetch後に確認 |

両ライブラリのソースには独自パッチを加えず、サブモジュールの固定コミットで管理しています。
作業前から存在したACL配下の未追跡キャッシュは保全しました。

## 互換性対応

- エクスポートページ移動機能を維持し、上流の3Dビューア表示タイミングの変更に合わせて
  `ExtractExportPage` からも表示要求を引き継ぐようにしました。
- IRMesh・Houdiniメッシュの表示／エクスポート、Ace Combat 8などの上流対応、
  キーボード操作・アイコン生成・アニメーション処理の修正を取り込みました。
- Verseの旧独自overrideを廃止し、CUE4Parseで修正されたStruct・Function・DebugDataの読み取りを使用します。
- Verseダイジェストは、新しいUE6ネイティブペイロードと旧プロパティ形式の両方を扱う
  `ReadableCode` に統一しました。コードが削除されている場合のフォールバックも維持しています。
- Verseソース復元は既存の生データ解析を維持し、上流の通常／コンパクトDebugDataからも復元できます。
  コンパクト形式の大小トレースポイント、完全パスによるハッシュ衝突解決、範囲外参照を扱います。
- 日本語UI、検索強調、MCP、アセット編集、pak作成などの独自機能を残しています。

## 検証結果

| 検証 | 結果 |
| --- | --- |
| 更新前Releaseビルド | エラー0 |
| 統合後Releaseビルド | エラー0 |
| CUE4Parse公式テスト・UE5.8 | 合格211、失敗0、スキップ1 |
| CUE4Parse公式テスト・UE6.0 | 合格211、失敗0、スキップ1 |
| UAssetAPI公式テスト | 合格27、失敗0 |
| 日本語版互換性チェック | 8項目合格 |
| 実際のローカルFortniteデータによるVerse検証 | 生データ／上流DebugDataからのソース一覧が88件で一致、関数25件読込、復元本文42,326文字も完全一致 |
| 自己完結型win-x64単一EXE発行 | 成功 |
| 発行EXEのMCPヘルプモード起動 | 終了コード0 |
| 単一EXEから展開されたACL DLL | ビルドしたDLLとSHA256一致 |

CUE4Parseのスキップは公式側の
`IoStorePatchPackageIdPrecedenceIsIndependentOfMountOrder` です。
新しいゲーム固有のIRMesh/Houdiniの対話的3D表示や、全ゲームの全アセットの動作までは確認していません。
既存のnullable・非推奨API警告に加え、更新前からある推移依存
`Microsoft.Bcl.Memory 9.0.0` のNU1903警告が残っています。

DLL同梱検証では、`DOTNET_BUNDLE_EXTRACT_BASE_DIR` にバックスラッシュ区切りの
Windows絶対パスを指定しました。初回のスラッシュ区切りの検証用指定ではホストが異常終了しましたが、
正規のWindowsパスを指定した新規展開先で起動・DLL検証が成功しています。

互換性チェックの手順は [Tools/CompatibilityChecks/README.md](../Tools/CompatibilityChecks/README.md) にあります。
発行物は `artifacts/upstream-sync-20261008/FModel.exe`、検証ログはリポジトリ直下の
`.tmp_*` に保存しています。いずれもGit管理外です。

## 発行手順

```powershell
git submodule update --init --recursive
cmake -S CUE4Parse/CUE4Parse-Natives -B artifacts/cue4parse-natives-build
cmake --build artifacts/cue4parse-natives-build --config Release --target install
dotnet publish FModel/FModel.csproj -c Release --self-contained true -r win-x64 -p:CUE4PARSE_SKIP_NATIVE=true -p:GeneratePackageOnBuild=false "-p:CUE4ParseNativeDll=$((Resolve-Path CUE4Parse/CUE4Parse-Natives/bin/Release/CUE4Parse-Natives.dll).Path)" -o artifacts/upstream-sync-20261008
```

バックアップへ戻す場合は、まず `git switch --no-recurse-submodules backup/pre-upstream-sync-20261008`、
続けて `git submodule update --init --recursive` を実行します。
通常の作業は `feature/upstream-sync-20261008` で続けてください。
