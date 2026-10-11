# Uno Win32 ランタイムの変更告知

Uno Platform — Copyright (c) the Uno Platform contributors.
元のソースは Apache License 2.0（同梱 License.md）で提供されています。
出所: https://github.com/unoplatform/uno
固定 commit: da217df2312e9e39676880f9cb4b8d519cc297e0（Uno 6.7.135）。

AnomalyStudio は 2026-10-11 に次の変更を加えました。

- Win32UIAutomationInterop.cs: UiaRaiseStructureChangedEvent のマーシャリングを SAFEARRAY から int 配列と長さの 4 引数に修正。
- Win32Accessibility.cs: 構造変更イベントを送る際に runtimeId の長さを渡す。
- 上流 Hosting/Win32Host.cs のコメント「There's only one running instance...」行と THIRD-PARTY-NOTICES.md の AcrylicBrush 紹介行の末尾 1 space を、それぞれリポジトリの空白規約に合わせて削除。動作および告知内容は不変。
- Win32DragDropExtension.cs、Win32ClipboardExtension.cs、License.md、THIRD-PARTY-NOTICES.md の末尾の空行 1 行を削除。ソースの原本 SHA は検証時に空行を復元して照合する。

UI Automation の構造変更通知を Microsoft のネイティブ API 契約に合わせる修正です。
アクセシビリティ機能および通知の送信条件は維持しています。
元ソースは upstream/、変更した 2 ファイルは patched/、出所の Git blob SHA は source-manifest.json にあります。
本ビルドは AnomalyStudio による派生物であり、Uno の公式配布バイナリではありません。
上流の第三者告知は THIRD-PARTY-NOTICES.md に保持しています。
