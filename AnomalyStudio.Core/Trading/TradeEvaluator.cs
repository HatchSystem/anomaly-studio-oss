using AnomalyStudio.Core.Analysis;

namespace AnomalyStudio.Core.Trading;

public enum TradeStatus
{
    /// <summary>Close 時刻のデータがまだ取り込まれていない。</summary>
    Pending,

    /// <summary>結果確定。</summary>
    Settled,

    /// <summary>Entry または Close 時刻の足がない（休場など）。</summary>
    NoData,
}

/// <summary>1 回の取引の結果。値幅は価格単位。<paramref name="EntryPrice"/> は Entry 時点の BID（結果確定時のみ）。</summary>
public sealed record TradeResult(
    DateOnly TradeDate,
    EntryPoint Point,
    DateTime EntryJst,
    DateTime CloseJst,
    TradeStatus Status,
    double Raw,
    double Spread,
    double Net,
    double EntryPrice = double.NaN)
{
    public bool IsWin => Status == TradeStatus.Settled && Net > 0;

    /// <summary>Entry と Close がどちらも FX 市場の開いている時間か（土曜の日中や月曜の開場前は取引できない）。</summary>
    public bool IsInMarketHours => Jst.IsFxMarketOpen(EntryJst) && Jst.IsFxMarketOpen(CloseJst);
}

/// <summary>
/// 保存済みの 1 分足でエントリーポイントの結果を計算する。分析エンジンと同じく BID Open → BID Open で、
/// 控除する spread はその取引の Entry 時点の実測値（取れないときは銘柄の既定値）。
/// 足は時刻順の並びをそのまま持ち、二分探索で引く（辞書に写さないので、キャッシュした足をコピーせずに使える）。
/// </summary>
public sealed class TradeEvaluator
{
    private readonly IReadOnlyList<OpenQuote> _quotes;
    private readonly DateTime _dataUntilUtc;
    private readonly double _fallbackSpread;

    /// <param name="quotes">対象期間の足（時刻順でなければ並べ替えて写す）。</param>
    /// <param name="dataUntilUtc">取込済みの最終時刻。これより後の Close は「未確定」。</param>
    public TradeEvaluator(IEnumerable<OpenQuote> quotes, DateTime dataUntilUtc, double fallbackSpread)
    {
        var list = quotes as IReadOnlyList<OpenQuote> ?? [.. quotes];
        _quotes = IsSorted(list) ? list : [.. list.OrderBy(q => q.TimeUtc)];
        _dataUntilUtc = dataUntilUtc;
        _fallbackSpread = fallbackSpread;
    }

    private static bool IsSorted(IReadOnlyList<OpenQuote> quotes)
    {
        for (var i = 1; i < quotes.Count; i++)
        {
            if (quotes[i].TimeUtc < quotes[i - 1].TimeUtc)
            {
                return false;
            }
        }

        return true;
    }

    public TradeResult Evaluate(DateOnly tradeDate, EntryPoint point)
    {
        var entryJst = tradeDate.ToDateTime(TimeOnly.MinValue).AddMinutes(point.EntryMinute);
        var closeJst = entryJst.AddMinutes(point.HoldMinutes);
        var closeUtc = Jst.ToUtc(closeJst);

        if (closeUtc > _dataUntilUtc)
        {
            return new TradeResult(tradeDate, point, entryJst, closeJst, TradeStatus.Pending, double.NaN, double.NaN, double.NaN);
        }

        if (!TryFind(Jst.ToUtc(entryJst), out var entry) || !TryFind(closeUtc, out var close))
        {
            return new TradeResult(tradeDate, point, entryJst, closeJst, TradeStatus.NoData, double.NaN, double.NaN, double.NaN);
        }

        var sign = point.Direction == TradeDirection.Long ? 1.0 : -1.0;
        var raw = sign * (close.BidOpen - entry.BidOpen);
        var spread = entry.AskOpen - entry.BidOpen is var s && s >= 0 && double.IsFinite(s) ? s : _fallbackSpread;
        return new TradeResult(tradeDate, point, entryJst, closeJst, TradeStatus.Settled, raw, spread, raw - spread, entry.BidOpen);
    }

    /// <summary>時刻がちょうど一致する足を二分探索で引く。</summary>
    private bool TryFind(DateTime timeUtc, out OpenQuote quote)
    {
        int lo = 0, hi = _quotes.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var t = _quotes[mid].TimeUtc;
            if (t == timeUtc)
            {
                quote = _quotes[mid];
                return true;
            }

            if (t < timeUtc)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        quote = default;
        return false;
    }

    /// <summary>期間中の各日・各ポイントを評価する。</summary>
    public IEnumerable<TradeResult> EvaluateRange(DateOnly from, DateOnly to, IReadOnlyList<EntryPoint> points)
    {
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            foreach (var point in points)
            {
                yield return Evaluate(day, point);
            }
        }
    }

    /// <summary>評価に必要な足の範囲（UTC）。日跨ぎ Close のため終端は翌日まで含める。</summary>
    public static (DateTime FromUtc, DateTime ToUtc) RequiredRange(DateOnly from, DateOnly to) =>
        (Jst.StartOfDayUtc(from), Jst.StartOfDayUtc(to.AddDays(2)));
}
