using AnomalyStudio.Core.Analysis;

namespace AnomalyStudio.Core.Backtesting;

/// <summary>
/// バックテスト・最適化確認の結果を、取引時間帯とポイント数で絞り込む（計算済みの取引に後から当てはめる）。
/// 時間帯は JST の時（<see cref="StartHour"/> 以上 <see cref="EndHour"/> 以下）で、
/// Entry が開始以降かつ Close が終了以前のポイントだけを使う（時間帯の境界や日付を跨ぐポイントは除く）。
/// そのうえで基準日ごとに、順位（モードの SQL の並び = 既定モードではスコアの高い順）の上位 <see cref="MaxPoints"/> 件を使う。
/// </summary>
public sealed record PointFilter(int StartHour, int EndHour, int MaxPoints)
{
    public static PointFilter Default { get; } = new(8, 19, 10);

    /// <summary>時間帯に収まるポイントか。Close がちょうど終了時刻のものは含む。</summary>
    public bool Contains(EntryPoint point) =>
        point.EntryMinute >= StartHour * 60 && point.EntryMinute + point.HoldMinutes <= EndHour * 60;

    /// <summary>1 つの分析結果のポイントから、時間帯に収まるものを順位の高い順に最大 <see cref="MaxPoints"/> 件選ぶ。</summary>
    public IReadOnlyList<EntryPoint> Select(IEnumerable<EntryPoint> points) =>
        [.. points.Distinct().Where(Contains).OrderBy(p => p.Rank).Take(MaxPoints)];

    /// <summary>基準日ごとに、時間帯に収まるポイントを順位の高い順に最大 <see cref="MaxPoints"/> 件選び、その取引だけを返す。</summary>
    public IReadOnlyList<BacktestTrade> Apply(IReadOnlyList<BacktestTrade> trades)
    {
        var allowed = trades
            .GroupBy(t => t.ReportDate)
            .ToDictionary(g => g.Key, g => Select(g.Select(t => t.Trade.Point)).ToHashSet());
        return [.. trades.Where(t => allowed[t.ReportDate].Contains(t.Trade.Point))];
    }
}
