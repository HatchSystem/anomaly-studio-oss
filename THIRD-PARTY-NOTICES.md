# Third-party notices / 同梱ソフトウェアの許諾表示

AnomalyStudio itself is licensed under the PolyForm Strict License 1.0.0 (see `LICENSE.md`).
The distributed application includes the following third-party components, each under its own license.
AnomalyStudio 本体は PolyForm Strict License 1.0.0 です。配布物には次のソフトウェアが含まれ、それぞれ各自のライセンスに従います。

| Component | License | Source |
|---|---|---|
| .NET runtime and libraries (Microsoft.Extensions.*, System.*) | MIT | https://github.com/dotnet/runtime |
| Uno Platform (Uno.WinUI and related packages) | Apache-2.0 | https://github.com/unoplatform/uno |
| Uno Win32 runtime（UI Automation ABI 修正版、6.7.135 ベース） | Apache-2.0 | `third_party/Uno.Win32/NOTICE.md` |
| SkiaSharp / HarfBuzzSharp | MIT | https://github.com/mono/SkiaSharp |
| Skia | BSD-3-Clause | https://skia.org |
| HarfBuzz | MIT (Old MIT) | https://github.com/harfbuzz/harfbuzz |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| DuckDB | MIT | https://github.com/duckdb/duckdb |
| DuckDB.NET | MIT | https://github.com/Giorgi/DuckDB.NET |
| Velopack | MIT | https://github.com/velopack/velopack |
| Phosphor Icons (font) | MIT | https://github.com/phosphor-icons/core |

The full license texts are available at the sources above and in each NuGet package. The Phosphor Icons license is also included in
`AnomalyStudio/Assets/Fonts/Phosphor-LICENSE.txt`.
各ライセンスの全文は上の配布元と各 NuGet パッケージにあります。Phosphor Icons の許諾表示は `AnomalyStudio/Assets/Fonts/Phosphor-LICENSE.txt` にも含めています。

Win32 runtime は Uno の commit `da217df2312e9e39676880f9cb4b8d519cc297e0` を元に、
UI Automation の構造変更通知を Windows API の引数形式へ合わせた修正版です。
出所、変更内容、Apache-2.0 全文と元の第三者告知は `third_party/Uno.Win32/` に保存し、
配布物にも `licenses/Uno.Win32/` として同梱します。

## External data / 外部のデータ

AnomalyStudio does not include market data or economic calendar data. It downloads them on your computer when you use it:

- 1-minute bars from Dukascopy's chart service (https://www.dukascopy.com/)
- Economic calendar from GMO CLICK Securities' web page (https://www.click-sec.com/corp/guide/fxneo/cal/)

These are not official APIs. Their availability and terms are controlled by each provider, and you are responsible for following them.
市場データと経済指標は同梱しておらず、利用者の PC で各社のサービスから取得します。いずれも公式の API ではなく、提供の有無と利用条件は各社が決めます。利用条件を守るのは利用者の責任です。
