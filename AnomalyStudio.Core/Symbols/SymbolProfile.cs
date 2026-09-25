namespace AnomalyStudio.Core.Symbols;

/// <summary>
/// 分析対象の銘柄。ユーザーが追加でき、取込・分析・表示はすべてこの定義を参照する（コードに銘柄名の分岐を置かない）。
/// </summary>
public sealed record SymbolProfile
{
    /// <summary>銘柄 ID（例 USDJPY）。保存フォルダ名・DB のキーに使う。</summary>
    public required string Id { get; init; }

    /// <summary>Dukascopy の銘柄コード（例 USD/JPY、XAU/USD）。</summary>
    public required string DukascopyInstrument { get; init; }

    /// <summary>表示単位 1 あたりの価格幅（USDJPY の pips なら 0.01、XAUUSD の USD なら 1）。</summary>
    public required double UnitSize { get; init; }

    /// <summary>表示単位の名前（pips / USD）。</summary>
    public required string UnitLabel { get; init; }

    /// <summary>実測 spread が取れないときに使う控除値（価格単位）。通常は使われない。</summary>
    public required double FallbackSpread { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>価格差を表示単位へ換算する。</summary>
    public double ToUnits(double priceDiff) => priceDiff / UnitSize;
}
