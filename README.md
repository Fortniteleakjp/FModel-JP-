# FModel-JP

[![Build](https://github.com/Fortniteleakjp/FModel-JP-/actions/workflows/run.yml/badge.svg)](https://github.com/Fortniteleakjp/FModel-JP-/actions/workflows/run.yml)
[![Latest Release](https://img.shields.io/github/v/release/Fortniteleakjp/FModel-JP-?label=release)](https://github.com/Fortniteleakjp/FModel-JP-/releases/latest)
[![License: GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue.svg)](LICENSE)

**FModel-JP** は、[FModel](https://github.com/4sval/FModel) をベースに、日本語環境での利用や Fortnite 向けのワークフローを強化した Windows 向け Unreal Engine アーカイブエクスプローラーです。

コアの Unreal Engine 解析には [CUE4Parse](https://github.com/FabianFG/CUE4Parse) を使用し、FModel が持つ UE4 / UE5 アーカイブの閲覧・検索・解析・エクスポート機能をベースに、FModel-JP 独自の機能追加とパフォーマンス改善を行っています。

> [!NOTE]
> FModel-JP は FModel の派生プロジェクトです。FModel および CUE4Parse の開発者・コントリビューターに感謝します。

## 主な機能

### FModel ベースの機能

- Unreal Engine 4 / 5 のゲームアーカイブを閲覧
- AES キーを使用した暗号化アーカイブの読み込み
- アセット・パッケージの検索、フィルター、参照
- テクスチャ、モデル、アニメーション、音声などのプレビュー・エクスポート
- Blueprint / UClass などの解析・表示
- ゲームごとのカスタムディレクトリ
- IoStore / Pak を含む CUE4Parse 対応フォーマットの読み込み

### FModel-JP の追加・改善

- **日本語 / English UI**
  - 設定画面からインターフェース言語を切り替え可能
  - リリースノートも表示言語に追従
- **Reference Viewer**
  - アセット間の参照関係をノードグラフで表示
  - 右クリックメニューまたはショートカットから利用可能
- **Diff Tool**
  - アセットを以前のバージョンや別ビルドと比較
- **ブループリントグラフ**
  - ブループリントの関数と EventGraph を UE エディタのようなノードグラフで表示
  - ノード名・ピン名はエディタと同じ表記（[ノード名データ](#ブループリントグラフのノード名データ)を参照）
  - イベント / Branch / Cast To / Timeline / Delay / SpawnActor / Make Struct などのエディタ専用ノードを再現
  - ダブルクリックで呼び出し先の関数へ移動、ノード検索、ズーム
  - 利用には設定の「スクリプトバイトコードをシリアル化」を ON にする必要があります
- **Verse 宣言の復元**
  - クックされた Verse のクラス・構造体・列挙型を Verse コードとして復元
  - 復元した `.verse` ファイルをエクスプローラーから直接開ける
- **export のページ送り**
  - export が 5000 個を超えるパッケージをページ単位で表示
  - 1 ページあたりの export 数を設定で変更可能（1〜1000）
- **Athena Profile**
  - 選択した Fortnite コスメティックから `profile_athena.json` を生成
  - フォルダ単位でコスメティックを収集
  - プロファイル名、バトルパスレベルなどを設定可能
- **Athena キュー**
  - コスメティックを一時キューへ追加
  - キュー管理画面から個別削除・全削除・プロファイル生成
  - キュー件数をリアルタイム表示
  - 重複追加時の警告
  - プロファイル生成中の進捗表示
- **パフォーマンス改善**
  - UE パッケージの LRU キャッシュ
  - AES 更新時の差分キー適用
  - `ThreadWorker` の async ジョブ対応
  - 検索結果のサイズソートキャッシュ
- **アプリ内リリース情報**
  - リリースノート表示
  - 最新ビルドのダウンロード

## ダウンロード

ビルド済みの最新版は [GitHub Releases](https://github.com/Fortniteleakjp/FModel-JP-/releases/latest) から入手できます。

初回利用時は、Release にある ZIP を展開するか `FModel.exe` を直接ダウンロードしてください。

GitHub Actions で作成される Release ビルドは **Windows x64 / self-contained** で発行されるため、通常は .NET Runtime を別途インストールする必要はありません。

## Athena Profile の出力

Athena Profile を生成すると、設定されている出力ディレクトリの次の場所へ保存されます。

```text
<OutputDirectory>\Profiles\profile_athena.json
```

右クリックメニューから直接生成するほか、複数のコスメティックを Athena キューへ追加してまとめて生成できます。

## ソースコードからビルド

### 必要な環境

- Windows x64
- Git
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [CMake](https://cmake.org/)
- CMake から利用できる Windows C++ ツールチェーン
  - Visual Studio / Visual Studio Build Tools の MSVC 環境を推奨

### リポジトリを取得

CUE4Parse は Git submodule として含まれているため、`--recurse-submodules` を付けて clone してください。

```powershell
git clone --recurse-submodules https://github.com/Fortniteleakjp/FModel-JP-.git
cd FModel-JP-
```

すでに clone 済みの場合は、次のコマンドで submodule を初期化できます。

```powershell
git submodule update --init --recursive
```

### Restore

```powershell
dotnet restore .\FModel\FModel.slnx -r win-x64
```

### 通常の Release ビルド

```powershell
dotnet build .\FModel\FModel.csproj -c Release --no-restore
```

通常のビルド出力は次のディレクトリ以下に生成されます。

```text
FModel\bin\Release\net10.0-windows\win-x64\
```

## 配布用 Single-file Publish

CI では、先に `CUE4Parse-Natives.dll` を CMake でビルドし、その DLL を `FModel.exe` に埋め込んだ self-contained single-file を作成しています。

ローカルで CI と同等の publish を行う場合の例:

```powershell
cmake -S ".\CUE4Parse\CUE4Parse-Natives" -B ".\CUE4Parse\CUE4Parse-Natives\build-local"
cmake --build ".\CUE4Parse\CUE4Parse-Natives\build-local" --config Release

$native = (Resolve-Path ".\CUE4Parse\CUE4Parse-Natives\build-local\Release\CUE4Parse-Natives.dll").Path
$env:CUE4PARSE_SKIP_NATIVE = "true"

dotnet restore ".\FModel\FModel.slnx" -r win-x64
dotnet publish ".\FModel\FModel.csproj" `
  -c Release `
  --no-restore `
  --self-contained true `
  -r win-x64 `
  -f net10.0-windows `
  -o ".\FModel\bin\Publish\" `
  -p:PublishReadyToRun=false `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -p:CUE4ParseNativeDll="$native"
```

publish 後の実行ファイル:

```text
FModel\bin\Publish\FModel.exe
```

## ブループリントグラフのノード名データ

クックされたゲームにはエディタ用のメタデータ（関数の表示名、ピン名、純粋関数かどうかなど）が残っていません。
ブループリントグラフでエディタと同じ名前を表示するため、次の 2 つのソースから作成したデータを
`FModel/Resources/BlueprintNodeDatabase.json.gz` としてアプリに同梱しています。

| ソース | 用途 |
| --- | --- |
| [Unreal Engine のソースコード](https://github.com/EpicGames/UnrealEngine/tree/ue6-main)（`ue6-main` ブランチ） | エンジン関数の `UFUNCTION` 宣言から、表示名（`DisplayName` / `CompactNodeTitle`）、引数名、`UPARAM` の表示名、非表示ピン、`static` / 純粋関数の判定を取得 |
| [Dumpspace](https://dumpspace.spuckwaffel.com/)（[Spuckwaffel/dumpspace](https://github.com/Spuckwaffel/dumpspace) の Fortnite SDK ダンプ） | エンジンのソースにない Fortnite 独自のクラス・関数の引数とフラグ、クラスの継承関係、メンバー変数の型 |

ノード名の決め方（`FName::NameToDisplayString`、`Target is ...` の表示、Cast To / Timeline / SpawnActor などの K2Node のタイトル）も、
エンジンのソースコードの実装に合わせています。

> [!NOTE]
> - Unreal Engine のソースコードの閲覧には、Epic Games アカウントと GitHub アカウントの連携が必要です。
> - Dumpspace のダンプはデータ取得時点のゲームビルドのものです。手元のゲームと引数がずれる関数は `Arg 1` のように表示されます。
> - Fortnite 独自の関数はメタデータがないため、表示名は関数名から生成しています。

### データの再生成

ゲームやエンジンの更新に合わせてデータを作り直す場合は、`Tools/GenerateBlueprintNodeDatabase.py` を使用します。
エンジンのソースはヘッダー（`Engine/Source/Runtime/**/*.h`、`Engine/Plugins/**/*.h`）だけあれば十分です。

```powershell
# Unreal Engine のヘッダーだけを取得（sparse checkout）
git clone --filter=blob:none --no-checkout --depth 1 --branch ue6-main https://github.com/EpicGames/UnrealEngine.git ue
git -C ue sparse-checkout init --no-cone
Set-Content ue\.git\info\sparse-checkout "/Engine/Source/Runtime/**/*.h`n/Engine/Plugins/**/Classes/**/*.h`n/Engine/Plugins/**/Public/**/*.h"
git -C ue checkout

# Dumpspace の Fortnite ダンプを取得
$base = "https://raw.githubusercontent.com/Spuckwaffel/dumpspace/refs/heads/main/Games/Unreal-Engine-5/Fortnite"
Invoke-WebRequest "$base/FunctionsInfo.json.gz" -OutFile FunctionsInfo.json.gz
Invoke-WebRequest "$base/ClassesInfo.json.gz" -OutFile ClassesInfo.json.gz

# FModel/Resources/BlueprintNodeDatabase.json.gz を再生成
python .\Tools\GenerateBlueprintNodeDatabase.py .\ue --dumpspace FunctionsInfo.json.gz --dumpspace-classes ClassesInfo.json.gz
```

生成元（エンジンのコミットと Dumpspace の更新日時）はデータ内の `source` に記録され、ブループリントグラフの画面下部にも表示されます。

## CUE4Parse

`CUE4Parse/` は [FabianFG/CUE4Parse](https://github.com/FabianFG/CUE4Parse) を参照する Git submodule です。

通常は親リポジトリに記録されたコミットを使用してください。

```powershell
git submodule update --init --recursive
```

CUE4Parse を upstream の最新 `master` へ更新する場合は、互換性を確認した上で更新し、親リポジトリ側でも submodule のコミット変更を記録してください。

```powershell
git -C CUE4Parse fetch origin
git -C CUE4Parse switch master
git -C CUE4Parse merge --ff-only origin/master

git add CUE4Parse
```

## プロジェクト構成

```text
FModel-JP-/
├─ FModel/                  FModel-JP 本体
├─ CUE4Parse/               Unreal Engine 解析ライブラリ (submodule)
├─ Tools/                   ブループリントのノード名データ生成スクリプト
├─ .github/workflows/       GitHub Actions / Release ビルド
├─ LICENSE                  GPL-3.0
├─ NOTICE                   サードパーティーライセンス・表記
└─ README.md
```

## 開発について

現在のアプリケーションターゲットは次の通りです。

- Target Framework: `net10.0-windows`
- Runtime Identifier: `win-x64`
- Platform Target: `x64`
- UI: WPF
- FModel-JP Version: `5.9.0`

変更を加えた場合は、少なくとも Release ビルドが通ることを確認してください。

```powershell
dotnet build .\FModel\FModel.csproj -c Release --no-restore
```

## Upstream / Credits

- [4sval/FModel](https://github.com/4sval/FModel) — FModel upstream
- [FabianFG/CUE4Parse](https://github.com/FabianFG/CUE4Parse) — Unreal Engine archive / asset parsing
- [EpicGames/UnrealEngine](https://github.com/EpicGames/UnrealEngine) — ブループリントグラフのノード名・ピン名（エンジン関数）と命名規則
- [Spuckwaffel/dumpspace](https://github.com/Spuckwaffel/dumpspace) — ブループリントグラフの Fortnite 独自関数の引数・クラス継承情報
- FModel / CUE4Parse および各依存ライブラリのすべてのコントリビューター

FModel-JP は upstream の成果を尊重しつつ、日本語対応と独自機能を追加している派生版です。

## License

FModel-JP は **GNU General Public License v3.0 (GPL-3.0)** の下で提供されています。

詳細は [LICENSE](LICENSE) を確認してください。サードパーティーライブラリのライセンス・表記については [NOTICE](NOTICE) を確認してください。
