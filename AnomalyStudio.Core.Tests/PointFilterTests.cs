using AnomalyStudio.Core.Analysis;
using AnomalyStudio.Core.Backtesting;
using AnomalyStudio.Core.Trading;

namespace AnomalyStudio.Core.Tests;

[TestClass]
public sealed class PointFilterTests
{
    private static EntryPoint Point(int rank, int entryMinute, int hold) =>
        new(rank, TradeDirection.Long, entryMinute, hold, 100 - rank);

    private static BacktestTrade Trade(DateOnly reportDate, DateOnly tradeDate, EntryPoint point)
    {
        var entry = tradeDate.ToDateTime(TimeOnly.MinValue).AddMinutes(point.EntryMinute);
        return new BacktestTrade(reportDate, new TradeResult(tradeDate, point, entry, entry.AddMinutes(point.HoldMinutes), TradeStatus.Settled, 0.01, 0.002, 0.008), "TEST");
    }

    [TestMethod]
    [DataRow(8 * 60, 10, true)] // 8:00 開始は含む
    [DataRow(18 * 60 + 50, 10, true)] // Close がちょうど 19:00 は含む
    [DataRow(18 * 60 + 55, 10, false)] // 19:05 まで跨ぐ
    [DataRow(7 * 60 + 55, 10, false)] // 開始前から
    [DataRow(12 * 60, 15, true)]
    public void Contains_RequiresTheWholeHoldInsideTheWindow(int entryMinute, int hold, bool expected) =>
        Assert.AreEqual(expected, PointFilter.Default.Contains(Point(1, entryMinute, hold)));

    [TestMethod]
    public void Contains_ExcludesPointsCrossingMidnight()
    {
        var allDay = new PointFilter(0, 24, 50);
        Assert.IsTrue(allDay.Contains(Point(1, 23 * 60 + 50, 10))); // Close 24:00 ちょうど
        Assert.IsFalse(allDay.Contains(Point(1, 23 * 60 + 55, 10))); // 0:05 へ跨ぐ
    }

    [TestMethod]
    public void Apply_TakesTopRanksAmongPointsInsideTheWindow()
    {
        var report = new DateOnly(2026, 9, 20);
        var day = new DateOnly(2026, 9, 21);
        var points = new[]
        {
            Point(1, 3 * 60, 10), // 時間帯外
            Point(2, 9 * 60, 10),
            Point(3, 18 * 60 + 55, 10), // 境界を跨ぐ
            Point(4, 10 * 60, 10),
            Point(5, 11 * 60, 10),
        };
        var trades = points.Select(p => Trade(report, day, p)).ToList();

        var result = new PointFilter(8, 19, 2).Apply(trades);

        CollectionAssert.AreEqual(new[] { 2, 4 }, result.Select(t => t.Trade.Point.Rank).ToArray());
    }

    [TestMethod]
    public void Select_TakesTopRanksInsideTheWindowRegardlessOfInputOrder()
    {
        // エントリー画面の複合モードは、分析結果のポイントに最適化確認と同じ絞り込みを当てはめる
        var points = new[]
        {
            Point(5, 11 * 60, 10),
            Point(3, 18 * 60 + 55, 10), // 境界を跨ぐ
            Point(1, 3 * 60, 10), // 時間帯外
            Point(4, 10 * 60, 10),
            Point(2, 9 * 60, 10),
        };

        var result = new PointFilter(8, 19, 2).Select(points);

        CollectionAssert.AreEqual(new[] { 2, 4 }, result.Select(p => p.Rank).ToArray());
    }

    [TestMethod]
    public void Apply_SelectsPerReportDate()
    {
        // 基準日が違えば、同じ順位でも別のポイント
        var first = new DateOnly(2026, 9, 13);
        var second = new DateOnly(2026, 9, 20);
        var trades = new List<BacktestTrade>
        {
            Trade(first, new DateOnly(2026, 9, 14), Point(1, 9 * 60, 5)),
            Trade(first, new DateOnly(2026, 9, 15), Point(1, 9 * 60, 5)),
            Trade(first, new DateOnly(2026, 9, 14), Point(2, 10 * 60, 5)),
            Trade(second, new DateOnly(2026, 9, 21), Point(1, 2 * 60, 5)),
            Trade(second, new DateOnly(2026, 9, 21), Point(2, 14 * 60, 5)),
            Trade(second, new DateOnly(2026, 9, 21), Point(3, 15 * 60, 5)),
        };

        var result = new PointFilter(8, 19, 1).Apply(trades);

        CollectionAssert.AreEqual(
            new[] { (first, 1), (first, 1), (second, 2) },
            result.Select(t => (t.ReportDate, t.Trade.Point.Rank)).ToArray());
    }
}
