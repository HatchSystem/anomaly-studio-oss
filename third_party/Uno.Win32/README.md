# Uno Win32 ランタイムの ABI 修正

Uno 6.7.135 の Win32 UI Automation 構造変更イベントの引数を、
[Microsoft の API 契約](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationcoreapi/nf-uiautomationcoreapi-uiaraisestructurechangedevent)
で定義された `provider, type, int* runtimeId, int runtimeIdLength` に合わせます。
元の 3 引数 SAFEARRAY 宣言で発生した、UIA ツリー取得後の画面切替時のネイティブクラッシュへの局所修正です。

## 出所と変更範囲

元ソースは [unoplatform/uno の固定 commit](https://github.com/unoplatform/uno/tree/da217df2312e9e39676880f9cb4b8d519cc297e0/src/Uno.UI.Runtime.Skia.Win32)
です。`upstream/` の 67 ファイルの Git blob SHA を `source-manifest.json` に保持します。
リポジトリの空白規約に合わせ、Hosting/Win32Host.cs のコメント 1 行の末尾 1 space のみ除いています。
Win32DragDropExtension.cs と Win32ClipboardExtension.cs は末尾の空行 1 行だけ除いています。
検証スクリプトはこの 3 箇所を復元して原本 SHA と照合します。それ以外は原本のままです。
`patched/` は変更告知コメントと ABI 宣言・呼出引数の 2 箇所だけを変更しています。
ライセンス、第三者告知、変更告知を同梱しています。

```powershell
node third_party/Uno.Win32/verify-sources.mjs
dotnet build AnomalyStudio/AnomalyStudio.csproj -f net10.0-desktop
```

アプリの ProjectReference がこのソースから同名のランタイム DLL を構築します。
公式 NuGet パッケージの compile/runtime 資産を除き、build 設定とネイティブ依存は保持します。
変更していない Win32.Support.dll は公式パッケージからコピーします。
パッケージキャッシュ内の DLL を書き換える工程や、ビルド済み DLL のソース管理はありません。
build/publish は `licenses/Uno.Win32/` にライセンスと告知をコピーします。

## ビルド設定と更新

依存版はルートの Directory.Packages.props の Uno.Win32 用 ItemGroup に固定しています。
Uno.Sdk の更新時は、固定ソース・Uno 実装 DLL・Support DLL の互換性と ABI 修正の要否を再確認してください。
内部 API へのアクセスには公式実装 DLL と元と同じアセンブリ名が必要です。
SkiaSharp は通常の上流ビルドと同じ ref DLL をコンパイル参照に使います。

上流の `src/Directory.Build.props` と同じく、Windows 専用 host の CA1416 警告を除外しています。
NuGet 消費側から入るアプリ用 Uno.UI.SourceGenerators だけを除きます。
元の host は Uno.UI の ProjectReference を使い、XAML アプリ用生成器を必要としません。
通常の Microsoft SDK、CsWin32、BannedApiAnalyzers および Uno の検査器は維持します。
上流ソースに対する Uno0001 警告は隠していません。

アセンブリ ID は公式と同じ `Uno.UI.Runtime.Skia.Win32, Version=255.255.255.255, Culture=neutral, PublicKeyToken=null` です。
派生物の識別には FileVersion `6.7.135.1` と InformationalVersion の `anomalystudio-uia1` を使います。
公式 DLL の Authenticode 署名はソースビルドには引き継がれません。署名検証や OS 保護設定は変更していません。
製品の署名方針はアプリ配布工程で扱います。

採用版の更新ではソース検証、アプリ build/publish、出力 DLL の一致確認に加えて、
UIA ツリー取得後の画面切替を実アプリで確認してください。
