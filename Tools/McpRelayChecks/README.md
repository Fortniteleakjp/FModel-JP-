# MCP中継の再接続チェック

Windowsと.NET 10 SDKで、リポジトリのルートから実行します。

```powershell
dotnet run --project Tools/McpRelayChecks/McpRelayChecks.csproj -c Release
```

ランダムな名前のユーザー限定パイプでFModelの停止・再起動を再現します。
既存のFModel、AIクライアント、ユーザー設定、ゲームファイルには接続・書き込みしません。

- AI側のstdio接続を保持したまま、本体に再接続してinitializeとinitializedを復元
- 実行途中の要求には中断エラーを返し、要求を自動で再実行しない
- 再起動待機中の要求には待機中であることを返す
- 初回接続／再接続待機中のstdin終了で、中継が速やかに終了
- --no-launchと再接続タイムアウト
- 再初期化に失敗した場合の接続再試行

中継の埋め込み・配置の検証は `Tools/CompatibilityChecks` に含まれます。
