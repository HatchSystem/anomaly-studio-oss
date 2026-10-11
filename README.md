# AnomalyStudio

FX のアノマリー（時間帯ごとの値動きの癖）を分析するデスクトップアプリです（Windows / macOS）。
Dukascopy の 1 分足を取り込み、全 172,800 候補（1440 時刻 × 保有 1〜60 分 × Long/Short）を 30/90/180/365 日で検証して、モードごとにエントリーポイントを抽出します。通常の既定モードでは保有 3〜15 分を対象にします。
ウォークフォワード・バックテストで、その時点の分析だけを使った場合の成績も確かめられます。

A desktop app (Windows / macOS) that analyzes intraday time-of-day anomalies in FX using 1-minute bars. The UI is in Japanese.

> **ソース公開（source-available）です。オープンソースではありません。**
> 非商用の利用に限り、改変・再配布・二次利用はできません（[PolyForm Strict License 1.0.0](LICENSE.md)）。
> プルリクエストは受け付けていません。

## インストール

[v0.2.0 のリリース](https://github.com/HatchSystem/anomaly-studio-oss/releases/tag/v0.2.0)から、ご使用の PC に合うインストーラーを取得し、[導入マニュアル](docs/INSTALL.md) の手順に従ってください。既存版からの更新手順も掲載しています。

| 種類 | ファイル | 動作確認 |
|---|---|---|
| Windows 10 / 11（x64） | `HatchSystem.AnomalyStudio-win-x64-Setup.exe` | 確認済み |
| Windows 11（ARM） | `HatchSystem.AnomalyStudio-win-arm64-Setup.exe` | ビルドのみ・未検証 |
| Mac（Apple シリコン M1 以降） | `HatchSystem.AnomalyStudio-osx-arm64-Setup.pkg` | ビルドのみ・未検証 |
| Mac（Intel） | `HatchSystem.AnomalyStudio-osx-x64-Setup.pkg` | ビルドのみ・未検証 |

Windows は未署名、macOS は簡易署名のみのため、インストール時に OS の警告が出る場合があります。許可の手順はマニュアルにあります。
インストール後は、起動時に新しい版を確認して自動でダウンロードし、再起動（または次の起動）で更新します。

## v0.2.0 の変更

- 利益効率を従来の 30・90・365 日の平均に加え、短期（30 日）・中期（90 日）・長期（365 日）で評価するモードを追加しました。追加モードは初期状態では無効です。
- 最適化確認とウォークフォワードの銘柄・モード・検証期間・評価サイクルを、それぞれ保存します。保存済みの日付は翌日も自動で移動しません。
- 起動時はダッシュボードを表示します。最後に開いた画面を復元する機能は廃止しました。
- 設定に Windows 自体の時刻を同期する操作を追加しました。実行時に管理者承認が必要な場合があります。
- 初期状態の実画面 11 枚を使った操作マニュアルを追加しました。

詳しくは[変更履歴](docs/CHANGELOG.md)を参照してください。

## 操作マニュアル

[画像付き操作マニュアル](https://github.com/HatchSystem/anomaly-studio-oss/blob/main/docs/manual/README.md)で、取込・分析、エントリー、検証、モード、設定を説明しています。[HTML 版](https://github.com/HatchSystem/anomaly-studio-oss/blob/main/docs/manual/index.html)はダウンロードしてブラウザーで開くと、画像を拡大し、印刷できます。
画像は v0.1.0 の開発実行で撮影した初期状態の実画面です。v0.2.0 のインストール画面や版表示として撮影し直したものではありません。

## できること

| 画面 | 内容 |
|---|---|
| ダッシュボード | 次のエントリー、次の経済指標、米国夏時間の表示 |
| エントリー | 選んだ日のエントリーポイントと結果（取り込んだ 1 分足で確定）。銘柄・方向・モード・順位・取引時間帯・曜日で絞り込み、複数銘柄をまとめた複合モード |
| 経済指標カレンダー | GMO クリック証券の経済指標カレンダーを週次／月次で表示。重要度・国で絞り込み |
| ウォークフォワード・バックテスト | 取引日ごとに、その時点の分析結果で選んだポイントで評価（毎日・毎週・毎月）。安定性の指標とランダム基準、CSV 保存 |
| 最適化確認 | 最新の分析結果のポイントを過去の各日に当てはめた成績（選定に使った期間での上振れを確かめる） |
| データ抽出設定 | 取込と分析の実行、平均・期間別の利益効率モード、抽出モード（SQL）の編集、AI アシスト（設定した OpenAI 互換の LLM で SQL を生成） |
| 設定 | テーマ、銘柄、データの保存場所、週次の自動分析、Windows 時刻同期、経済指標の表示、AI アシスト、アプリの更新 |

分析の仕様は [docs/design/anomaly-engine.md](https://github.com/HatchSystem/anomaly-studio-oss/blob/main/docs/design/anomaly-engine.md) にあります。
毎週のウォークフォワードは週の直前の日曜日を基準に、その前日までのデータで選び直します。最適化確認は最新のポイントを固定して過去の日に適用する機能で、評価サイクルは集計単位です。どちらも、条件を保存することと分析結果を更新することは別です。

## 注意

- 市場データ（Dukascopy）と経済指標（GMO クリック証券）は、公式の API ではない方法で利用者の PC から取得します。提供側の変更で取得できなくなることがあります。利用条件は各社のものに従ってください。
- 分析結果は過去のデータの統計で、将来の成績を保証しません。投資の判断はご自身の責任で行ってください。
- ソフトウェアは現状のまま提供し、いかなる保証もしません（[LICENSE.md](LICENSE.md)）。

## ビルド（参考）

```sh
dotnet build AnomalyStudio/AnomalyStudio.csproj -f net10.0-desktop
dotnet test --project AnomalyStudio.Core.Tests/AnomalyStudio.Core.Tests.csproj
dotnet run --project AnomalyStudio/AnomalyStudio.csproj -f net10.0-desktop
```

.NET 10 SDK が必要です。ライセンス上、ビルドしたものの再配布や改変はできません。

- 構成: .NET 10 / C# / Uno Platform（Skia Desktop）/ MVVM（CommunityToolkit.Mvvm）/ DuckDB / Velopack
- 同梱ソフトウェアの許諾表示: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
