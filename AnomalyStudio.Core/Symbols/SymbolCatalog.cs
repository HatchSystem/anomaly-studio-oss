namespace AnomalyStudio.Core.Symbols;

/// <summary>Dukascopy で取得できる主要 FX・貴金属の一覧。銘柄追加の候補として使う。</summary>
public static class SymbolCatalog
{
    private static readonly string[] FxPairs =
    [
        "AUD/CAD", "AUD/CHF", "AUD/JPY", "AUD/NZD", "AUD/SGD", "AUD/USD", "CAD/CHF", "CAD/HKD", "CAD/JPY", "CHF/JPY",
        "CHF/PLN", "CHF/SGD", "EUR/AUD", "EUR/CAD", "EUR/CHF", "EUR/CZK", "EUR/DKK", "EUR/GBP", "EUR/HKD", "EUR/HUF",
        "EUR/JPY", "EUR/MXN", "EUR/NOK", "EUR/NZD", "EUR/PLN", "EUR/SEK", "EUR/SGD", "EUR/TRY", "EUR/USD", "EUR/ZAR",
        "GBP/AUD", "GBP/CAD", "GBP/CHF", "GBP/JPY", "GBP/NZD", "GBP/USD", "HKD/JPY", "MXN/JPY", "NZD/CAD", "NZD/CHF",
        "NZD/JPY", "NZD/SGD", "NZD/USD", "SGD/JPY", "TRY/JPY", "USD/CAD", "USD/CHF", "USD/CNH", "USD/CZK", "USD/DKK",
        "USD/HKD", "USD/HUF", "USD/ILS", "USD/JPY", "USD/MXN", "USD/NOK", "USD/PLN", "USD/SEK", "USD/SGD", "USD/THB",
        "USD/TRY", "USD/ZAR", "ZAR/JPY",
    ];

    private static readonly string[] Metals = ["XAU/USD", "XAG/USD"];

    /// <summary>初回起動時（symbols.json がないとき）に登録する銘柄。</summary>
    public static IReadOnlyList<SymbolProfile> Defaults { get; } =
    [
        .. new[]
        {
            "USD/JPY", "XAU/USD",
            "AUD/JPY", "AUD/USD", "CAD/JPY", "CHF/JPY", "EUR/AUD", "EUR/JPY",
            "EUR/USD", "GBP/AUD", "GBP/JPY", "GBP/USD", "NZD/JPY",
        }.Select(i => Create(i)),
    ];

    public static IReadOnlyList<SymbolProfile> All { get; } =
        [.. FxPairs.Concat(Metals).Order().Select(i => Create(i))];

    /// <summary>
    /// Dukascopy の銘柄コードから既定のプロファイルを作る。
    /// 貴金属は USD 建て、JPY クロスは 0.01、その他の FX は 0.0001 を 1 pips とする。
    /// </summary>
    public static SymbolProfile Create(string dukascopyInstrument, bool enabled = true)
    {
        var instrument = dukascopyInstrument.Trim().ToUpperInvariant();
        var id = instrument.Replace("/", string.Empty, StringComparison.Ordinal);
        var isMetal = instrument.StartsWith("XAU/", StringComparison.Ordinal) || instrument.StartsWith("XAG/", StringComparison.Ordinal);
        var isJpy = instrument.EndsWith("/JPY", StringComparison.Ordinal);

        var (unitSize, unitLabel, fallback) = (isMetal, isJpy) switch
        {
            (true, _) => (1.0, "USD", instrument.StartsWith("XAU/", StringComparison.Ordinal) ? 0.50 : 0.03),
            (_, true) => (0.01, "pips", 0.003),
            _ => (0.0001, "pips", 0.00003),
        };

        return new SymbolProfile
        {
            Id = id,
            DukascopyInstrument = instrument,
            UnitSize = unitSize,
            UnitLabel = unitLabel,
            FallbackSpread = fallback,
            Enabled = enabled,
        };
    }
}
