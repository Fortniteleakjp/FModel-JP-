# FModel-JP 開発者向けREADME

このドキュメントは、開発者向けに「どのファイル/フォルダが何を担当しているか」を把握しやすくするための案内です。

## 1. リポジトリ全体の構成

- `README.md`
  - ユーザー向け説明（機能紹介、使い方、ダウンロード案内）。
- `LICENSE`
  - ライセンス情報。
- `Update_DownloadUrl.py`
  - リリースURL更新用の補助スクリプト。
- `AES-Grabber/`
  - AESキー取得関連の小規模ツール（独立した C# プロジェクト）。
- `FModel/`
  - メインアプリ本体と依存ライブラリ群。
- `replay-analysis/`
  - リプレイ解析系の別ソリューション群（ライブラリ/テスト/ベンチマーク含む）。
- `images/`
  - README等で使う画像リソース。
- `obj/`
  - ビルド生成物（通常は編集不要）。

## 2. まず見るべきソリューション/プロジェクト

日常的な開発対象は主に以下です。

- `FModel/FModel/FModel.sln`
  - FModel-JP本体開発の起点。
- `FModel/FModel/FModel.csproj`
  - WPFアプリ本体プロジェクト。
- `replay-analysis/FortniteReplayDecompressor.sln`
  - リプレイ解析機能側を触る場合の起点。
- `AES-Grabber/AES-Grabber.csproj`
  - AES取得ツールを触る場合の起点。

補助/依存側として以下も存在します。

- `FModel/CUE4Parse/CUE4Parse.sln`
- `FModel/UAssetGUI-master/UAssetAPI/UAssetAPI.sln`

## 3. FModel本体（`FModel/FModel/`）の主要ファイル

### エントリとUI骨格

- `Program.cs`（`FModel/Program.cs` 側）
  - アプリ起動エントリ（上位階層に配置）。
- `App.xaml`
  - WPFアプリケーション定義、リソース参照の起点。
- `App.xaml.cs`
  - アプリ開始時の初期化コード。
- `MainWindow.xaml`
  - メイン画面のレイアウト。
- `MainWindow.xaml.cs`
  - メイン画面のイベント処理、画面ロジック。

### コア設定/共通定義

- `Constants.cs`
  - 定数定義。
- `Enums.cs`
  - 列挙型定義。
- `Helper.cs`
  - 共通ユーティリティ。
- `AssemblyInfo.cs`
  - アセンブリ情報。

### 機能系（Fortnite向け拡張含む）

- `AthenaFeatureBase.cs`
  - Athena系機能の基底クラス。
- `AthenaFeatures.cs`
  - Athena機能の定義/登録管理。
- `AthenaGenerator.cs`
  - Athenaデータ生成処理。
- `BruteForceAesFeature.cs`
  - AES総当たり（CPU）機能。
- `BruteForceAesGpuFeature.cs`
  - AES総当たり（GPU）機能。
- `GenerateAllCosmeticsFeature.cs`
  - コスメ情報生成（全件）。
- `GenerateCustomCosmeticsByIdFeature.cs`
  - ID指定コスメ生成。
- `GenerateNewCosmeticsFeature.cs`
  - 新規コスメ生成。
- `GenerateNewCosmeticsWithPaksFeature.cs`
  - Pakを考慮した新規コスメ生成。

### ディレクトリ別の役割

- `ViewModels/`
  - MVVMのビューモデル。
- `Views/`
  - 画面・ダイアログUI。
- `Services/`
  - API呼び出し、データ取得、外部連携。
- `Settings/`
  - 設定管理、設定モデル。
- `Resources/`
  - テーマ、文字列、画像などの静的リソース。
- `Features/`
  - 機能単位の実装。
- `Extensions/`
  - 拡張メソッド・補助拡張。
- `Framework/`
  - アプリ内で共有する基盤コード。
- `Creator/`
  - 作成系/生成系UIや処理。
- `AthenaProfile/`
  - Athena関連データ。
- `Properties/`
  - プロジェクトメタ情報。
- `bin/`, `obj/`
  - ビルド生成物（原則編集しない）。

## 4. 変更時の目安（どこを触るか）

- 画面デザイン変更
  - `MainWindow.xaml`、`Views/`、`Resources/`
- 画面動作変更
  - `MainWindow.xaml.cs`、`ViewModels/`
- API/外部サービス連携変更
  - `Services/`
- 新しい機能追加
  - `Features/` に実装し、必要なら `ViewModels/` と `Views/` を追加
- 設定項目の追加
  - `Settings/` と関連ViewModel/Viewを更新

## 5. ビルドの基本

1. 通常の本体開発は `FModel/FModel/FModel.sln` を開く。
2. `Debug` でビルドして動作確認。
3. リプレイ解析側を触る場合のみ `replay-analysis/FortniteReplayDecompressor.sln` を開く。

## 6. CUE4Parse の使用方法（開発者向け）

### どのソリューションを開くか

- `FModel/CUE4Parse/CUE4Parse.sln`
  - CUE4Parse本体・変換系・テストをまとめて確認する場合。
- `FModel/FModel/FModel.sln`
  - FModelアプリからの実利用（参照関係込み）を確認する場合。

### よく使うプロジェクト

- `FModel/CUE4Parse/CUE4Parse/CUE4Parse.csproj`
  - CUEアセット読み取りのコアライブラリ。
- `FModel/CUE4Parse/CUE4Parse-Conversion/CUE4Parse-Conversion.csproj`
  - 変換処理関連。
- `FModel/CUE4Parse/CUE4Parse.Tests/CUE4Parse.Tests.csproj`
  - CUE4Parseのテスト群。
- `FModel/CUE4Parse/CUE4Parse.Example/CUE4Parse.Example.csproj`
  - APIの最小利用例を確認するサンプル。

### 典型的な開発フロー

1. `FModel/CUE4Parse/CUE4Parse.sln` を開いて、対象箇所を修正する。
2. `CUE4Parse.Tests` を実行してライブラリ単体の回帰を確認する。
3. `FModel/FModel/FModel.sln` 側をビルドして、アプリ統合時に問題がないか確認する。

### FModel本体で使う時の注意

- CUE4Parse側の公開APIを変更した場合、`FModel/FModel/` 側の呼び出しコードも追従修正が必要になることがあります。
- 依存更新後は、少なくとも起動・パッケージ読み込み・主要ビュー表示までのスモークテストを実施してください。

## 7. 補足

- `bin/` と `obj/` 配下は自動生成ファイルが中心です。
- 依存ライブラリ側（`CUE4Parse`、`UAssetAPI`）を更新する場合は、影響範囲を事前に確認してから実施してください。
