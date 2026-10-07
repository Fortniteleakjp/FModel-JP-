# 日本語版の統合互換性チェック

上流更新後に、日本語UIリソース、検索強調、MCP引数、ACL DLL、Verseダイジェスト、
Verseデバッグ情報の通常形式・コンパクト形式、アセット編集、pak作成を実行確認します。
GUIウィンドウは開かず、ゲームやユーザー設定のファイルは変更しません。

Windows、.NET 10 SDK、Visual StudioのC++ビルドツール、CMakeが必要です。
リポジトリのルートで実行してください。

```powershell
git submodule update --init --recursive
cmake -S CUE4Parse/CUE4Parse-Natives -B artifacts/cue4parse-natives-build
cmake --build artifacts/cue4parse-natives-build --config Release --target install
dotnet run --project Tools/CompatibilityChecks/CompatibilityChecks.csproj -c Release -p:CUE4PARSE_SKIP_NATIVE=true -p:GeneratePackageOnBuild=false -- .
```

8項目がすべて成功すると終了コード0を返します。編集済みのfixtureとpak、診断用JSONは
`artifacts/compatibility-checks/<実行ID>/` に出力します。元のfixtureには書き込みません。
アセット編集はUAssetAPIに同梱されたUE5.5のテクスチャのLightingGuidを変更し、
FModelの保存処理からUAssetAPIで書き戻して、CUE4Parseでの再読み込み結果を検証します。

公式ライブラリのテストも併せて実行できます。

```powershell
$env:CUE4PARSE_FIXTURE_ENGINE = 'UE5_8'
dotnet test CUE4Parse/CUE4Parse.Tests/CUE4Parse.Tests.csproj -c Release -p:CUE4PARSE_SKIP_NATIVE=true
$env:CUE4PARSE_FIXTURE_ENGINE = 'UE6_0'
dotnet test CUE4Parse/CUE4Parse.Tests/CUE4Parse.Tests.csproj -c Release --no-build --no-restore
Remove-Item Env:CUE4PARSE_FIXTURE_ENGINE
dotnet test UAssetAPI/UAssetAPI.Tests/UAssetAPI.Tests.csproj -c Release -p:GeneratePackageOnBuild=false
```
