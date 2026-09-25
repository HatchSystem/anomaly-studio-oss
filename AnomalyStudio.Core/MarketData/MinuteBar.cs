namespace AnomalyStudio.Core.MarketData;

/// <summary>片側（BID または ASK）の 1 分足。時刻は UTC の足の開始時刻。</summary>
public readonly record struct SideBar(DateTime TimeUtc, double Open, double High, double Low, double Close, double Volume);

/// <summary>BID / ASK を同じ時刻で結合した 1 分足。</summary>
public readonly record struct MinuteBar(
    DateTime TimeUtc,
    double BidOpen,
    double BidHigh,
    double BidLow,
    double BidClose,
    double BidVolume,
    double AskOpen,
    double AskHigh,
    double AskLow,
    double AskClose,
    double AskVolume)
{
    /// <summary>足の開始時点の spread（Ask − Bid）。負の値は異常値として NaN。</summary>
    public double OpenSpread => AskOpen - BidOpen is var s && s >= 0 ? s : double.NaN;

    public static MinuteBar Join(SideBar bid, SideBar ask) =>
        new(bid.TimeUtc, bid.Open, bid.High, bid.Low, bid.Close, bid.Volume, ask.Open, ask.High, ask.Low, ask.Close, ask.Volume);
}
