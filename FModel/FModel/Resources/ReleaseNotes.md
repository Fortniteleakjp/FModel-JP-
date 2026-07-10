# FModelJP リリースノート

FModelJP をご利用いただきありがとうございます！
このページは、アップデート後に一度だけ表示されます。

## {version} 主な変更点

### 新機能

- **本家 FModel の最新差分を反映** — [4sval/FModel](https://github.com/4sval/FModel) の dev ブランチ最新状態に合わせて、依存関係・設定画面・解析まわりを更新しました。
- **GameFeatureVersePaths の表示に対応** — Verse 関連の Game Feature パス情報を JSON として確認できるようになりました。
- **Blender へのアニメーション移植に対応** — ファイルを右クリックして「Blenderに移植」を選ぶと、アニメーションを [UEFormat](https://github.com/h4lfheart/UEFormat) の `.ueanim` として出力し、Blender の選択中リグへ読み込めるようになりました。
- **Blender 連携のセットアップ案内を追加** — 起動済み Blender の受信ブリッジ `FModelBlenderBridge.py`、または UEFormat アドオンが見つからない・有効でない場合に、必要な配置先や導入手順を表示するようになりました。

### 改善・修正

すべての変更履歴については、[GitHub のリリース](https://github.com/Fortniteleakjp/FModel-JP-/releases) をご確認ください。
