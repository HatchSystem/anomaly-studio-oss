# Third-party notices / 同梱ソフトウェアの許諾表示

AnomalyStudio itself is licensed under the PolyForm Strict License 1.0.0 (see `LICENSE.md`).
The distributed application includes the following third-party components, each under its own license.
AnomalyStudio 本体は PolyForm Strict License 1.0.0 です。配布物には次のソフトウェアが含まれ、それぞれ各自のライセンスに従います。

| Component | License | Source |
|---|---|---|
| .NET runtime and libraries (Microsoft.Extensions.*, System.*) | MIT | https://github.com/dotnet/runtime |
| Uno Platform (Uno.WinUI and related packages) | Apache-2.0 | https://github.com/unoplatform/uno |
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

## External data / 外部のデータ

AnomalyStudio does not include market data or economic calendar data. It downloads them on your computer when you use it:

- 1-minute bars from Dukascopy's chart service (https://www.dukascopy.com/)
- Economic calendar from GMO CLICK Securities' web page (https://www.click-sec.com/corp/guide/fxneo/cal/)

These are not official APIs. Their availability and terms are controlled by each provider, and you are responsible for following them.
市場データと経済指標は同梱しておらず、利用者の PC で各社のサービスから取得します。いずれも公式の API ではなく、提供の有無と利用条件は各社が決めます。利用条件を守るのは利用者の責任です。
