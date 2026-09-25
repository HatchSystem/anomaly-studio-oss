using AnomalyStudio.Core.Analysis;
using AnomalyStudio.Core.Backtesting;

namespace AnomalyStudio.Core.Tests;

[TestClass]
public sealed class StatisticsTests
{
    private const double Tolerance = 1e-9;

    [TestMethod]
    public void WilsonLowerBound_MatchesReferenceValues()
    {
        // 期待値は同じ式を Python で計算したもの（z = 1.959963984540054）
        Assert.AreEqual(0.8864866068260312, Statistics.WilsonLowerBound(30, 30), Tolerance);
        Assert.AreEqual(0.9590643744356733, Statistics.WilsonLowerBound(90, 90), Tolerance);
        Assert.AreEqual(0.33154125640533766, Statistics.WilsonLowerBound(15, 30), Tolerance);
        Assert.AreEqual(0.0, Statistics.WilsonLowerBound(0, 365), Tolerance);
        Assert.IsTrue(double.IsNaN(Statistics.WilsonLowerBound(0, 0)));

        // サンプルが増えるほど点推定に近づき、同じ勝率なら少ないサンプルほど低い
        Assert.IsTrue(Statistics.WilsonLowerBound(30, 30) < Statistics.WilsonLowerBound(365, 365));
        Assert.IsTrue(Statistics.WilsonLowerBound(365, 365) < 1.0);
    }

    [TestMethod]
    public void Engine_ComputesWinRateLowerBound_AndItsAverage()
    {
        var t = AnomalyEngine.Compute(SyntheticMarket.Build(), new EngineParameters { FallbackSpread = 0.5 });
        var winner = CandidateGrid.Index(5, TradeDirection.Long, 540);
        Assert.AreEqual(0.8864866068260312, t.Period(30).WinRateLcb[winner], Tolerance);
        Assert.AreEqual(0.9895850677063891, t.Period(365).WinRateLcb[winner], Tolerance);
        Assert.AreEqual(0.9450453496560313, t.WinRateLcbAvg[winner], Tolerance);

        var loser = CandidateGrid.Index(3, TradeDirection.Long, 0);
        Assert.AreEqual(0.0, t.WinRateLcbAvg[loser], Tolerance);
        Assert.AreSame(t.WinRateLcbAvg, t.Column("win_rate_lcb_avg"));
        Assert.IsNull(t.Column("no_such_column"));
    }

    [TestMethod]
    public void BacktestStatistics_MatchesHandCalculation()
    {
        var s = BacktestStatistics.Compute([2, -1, -1, 3, -2]);
        Assert.AreEqual(5, s.Trades);
        Assert.AreEqual(2, s.Wins);
        Assert.AreEqual(1.0, s.Total, Tolerance);
        Assert.AreEqual(0.2, s.Mean, Tolerance);
        Assert.AreEqual(Math.Sqrt(18.8 / 4), s.StdDev, Tolerance);
        var se = Math.Sqrt(18.8 / 4) / Math.Sqrt(5);
        Assert.AreEqual(0.2 / se, s.TStat, Tolerance);
        Assert.AreEqual(Statistics.Z95 * se, s.MeanCiHalfWidth, Tolerance);
        Assert.AreEqual(5.0 / 4.0, s.ProfitFactor, Tolerance);

        // 累積 2, 1, 0, 3, 1 → 最大の落ち込みは 2（2→0 と 3→1）。連敗は −1, −1 の 2
        Assert.AreEqual(2.0, s.MaxDrawdown, Tolerance);
        Assert.AreEqual(2, s.MaxConsecutiveLosses);
    }

    [TestMethod]
    public void BacktestStatistics_HandlesEdgeCases()
    {
        Assert.AreSame(BacktestStatistics.Empty, BacktestStatistics.Compute([]));

        var single = BacktestStatistics.Compute([1.5]);
        Assert.AreEqual(1, single.Trades);
        Assert.IsTrue(double.IsNaN(single.StdDev));
        Assert.IsTrue(double.IsNaN(single.TStat));
        Assert.IsTrue(double.IsPositiveInfinity(single.ProfitFactor), "損失がなければ PF は無限大");

        var losses = BacktestStatistics.Compute([-1, -1, -1]);
        Assert.AreEqual(0.0, losses.ProfitFactor, Tolerance);
        Assert.AreEqual(3, losses.MaxConsecutiveLosses);
        Assert.AreEqual(3.0, losses.MaxDrawdown, Tolerance);
        Assert.AreEqual(0.0, losses.MaxDrawdown - 3.0, Tolerance);

        var flat = BacktestStatistics.Compute([0, 0]);
        Assert.AreEqual(0.0, flat.TStat, Tolerance);
        Assert.IsTrue(double.IsNaN(flat.ProfitFactor));
    }

    [TestMethod]
    public void RandomBaseline_SamplesNonOverlappingPointsInsideTheWindow()
    {
        var filter = new PointFilter(8, 19, 10);
        var rng = new Random(1);
        for (var trial = 0; trial < 50; trial++)
        {
            var points = RandomBaseline.SamplePoints(rng, filter, 10);
            Assert.AreEqual(10, points.Count);
            Assert.IsTrue(points.All(filter.Contains), "時間帯に収まる");
            Assert.IsTrue(points.All(p => p.HoldMinutes is >= 3 and <= 15));

            var occupied = new bool[CandidateGrid.MinutesPerDay];
            Assert.IsTrue(points.All(p => PointSelector.TryOccupy(occupied, p.EntryMinute, p.HoldMinutes)), "互いに重ならない");
        }

        // 狭い時間帯には入る分だけ（8:00〜9:00 に保有 3 分以上のポイントは最大 20 件）。幅のない時間帯や 0 件の要求は空
        var narrow = RandomBaseline.SamplePoints(new Random(2), new PointFilter(8, 9, 10), 100);
        Assert.IsTrue(narrow.Count is > 0 and <= 20);
        Assert.AreEqual(0, RandomBaseline.SamplePoints(new Random(1), new PointFilter(8, 8, 10), 10).Count);
        Assert.AreEqual(0, RandomBaseline.SamplePoints(new Random(3), new PointFilter(8, 9, 10), 0).Count);
    }

    [TestMethod]
    public void RandomBaseline_IsDeterministic_AndComparesAgainstActual()
    {
        var filter = new PointFilter(8, 19, 10);
        var days = new List<(DateOnly, int)> { (new DateOnly(2026, 9, 1), 3), (new DateOnly(2026, 9, 2), 2) };

        // 無作為ポイントの損益はすべて −1（1 日あたり件数分）。実績 0 はすべての試行を上回る
        double? Net(string symbol, DateOnly day, EntryPoint point) => symbol == "AAA" ? -1 : null;
        var a = RandomBaseline.Run(days, filter, ["AAA"], Net, actualTotal: 0, trials: 20);
        var b = RandomBaseline.Run(days, filter, ["AAA"], Net, actualTotal: 0, trials: 20);

        Assert.AreEqual(20, a.Trials);
        Assert.AreEqual(-5.0, a.MeanTotal, Tolerance);
        Assert.AreEqual(1.0, a.BeatRatio, Tolerance);
        CollectionAssert.AreEqual(a.Totals.ToArray(), b.Totals.ToArray());

        // 複数銘柄は無作為に割り当てる（BBB は null = 取引なし）。実績 −10 はどの試行にも負ける
        var c = RandomBaseline.Run(days, filter, ["AAA", "BBB"], Net, actualTotal: -10, trials: 20);
        Assert.IsTrue(c.MeanTotal > -5 && c.MeanTotal < 0);
        Assert.AreEqual(0.0, c.BeatRatio, Tolerance);
    }
}
