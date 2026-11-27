# Remo for Android
Windows 版 Remo (SSH クライアント) の Android 移植版。Kotlin + Jetpack Compose + JSch(mwiede)。

- 幅 720dp 以上の横画面: PC と同じ「接続先サイドバー + タブ + 端末」
- 縦画面/狭い画面: 接続先一覧 ⇄ 端末の切り替え (戻るキーで一覧へ)
- 操作: 縦スワイプ=スクロール(全画面アプリでは↑↓キー) / 長押し+ドラッグ=範囲選択→「コピー」 /
  ダブルタップ=画面クリア / 2本指タップ・「貼付」=ペースト / ピンチ=文字サイズ / 補助キー列(Esc, Tab, Ctrl, Alt, 矢印 ほか)
- 接続先は Android Keystore で暗号化保存。書き出し/読み込みは PC 版の .remo と互換。

ビルド: `gradle :app:assembleDebug` (JDK 17+, Android SDK 36)
