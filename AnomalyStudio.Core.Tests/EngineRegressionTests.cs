using AnomalyStudio.Core.Analysis;

namespace AnomalyStudio.Core.Tests;

/// <summary>
/// 高速化したエンジン（時刻 × 日の配列、塊ごとの並列、配列の並べ替え、結果の使い回し、窓の切り出し）が、
/// 高速化前の参照実装（<see cref="ReferenceEngine"/>）と同じ値をビット単位で出すことを確かめる。
/// 価格は乱数（ランダムウォーク + 欠損 + 週末の休場）で、手計算のテストでは現れない端数の差も検出する。
/// </summary>
[TestClass]
public sealed class EngineRegressionTests
{
    private static readonly EngineParameters Parameters = new() { FallbackSpread = 0.02 };

    [TestMethod]
    public void FastEngine_MatchesReference_BitForBit_OnRandomMarket()
    {
        var quotes = RandomMarket.Quotes(SyntheticMarket.ReportDate.AddDays(-365), 366, seed: 1);
        var matrix = PriceMatrix.Build(SyntheticMarket.ReportDate, 365, quotes);

        var fast = AnomalyEngine.Compute(matrix, Parameters);
        var reference = ReferenceEngine.Compute(matrix, Parameters);

        AssertSame(reference, fast);
    }

    [TestMethod]
    public void Window_WithoutReportDate_MatchesMatrixBuiltFromEarlierQuotesOnly()
    {
        // 396 日の行列から 365 日 + 基準日の窓を切り出し、基準日当日を使わない設定にすると、
        // 基準日の 0:00 より前の足だけで作った行列と同じ結果になる（ウォークフォワードの先読み防止と同じ条件）
        var firstDay = SyntheticMarket.ReportDate.AddDays(-395);
        var quotes = RandomMarket.Quotes(firstDay, 397, seed: 2);
        var full = PriceMatrix.BuildRange(firstDay, 397, quotes);
        var reportDate = SyntheticMarket.ReportDate.AddDays(-10);

        var window = full.Window(reportDate, 365, excludeReportDate: true);
        var sliced = PriceMatrix.Build(reportDate, 365, quotes.Where(q => q.TimeUtc < Jst.StartOfDayUtc(reportDate)));

        Assert.AreEqual(365, window.ValidDays);
        Assert.AreEqual(sliced.BarCount, window.BarCount);
        AssertSame(ReferenceEngine.Compute(sliced, Parameters), AnomalyEngine.Compute(window, Parameters));
        AssertSame(ReferenceEngine.Compute(window, Parameters), AnomalyEngine.Compute(sliced, Parameters));
    }

    [TestMethod]
    public void Window_WithReportDate_MatchesBuild()
    {
        var firstDay = SyntheticMarket.ReportDate.AddDays(-400);
        var quotes = RandomMarket.Quotes(firstDay, 402, seed: 3);
        var full = PriceMatrix.BuildRange(firstDay, 402, quotes);

        var window = full.Window(SyntheticMarket.ReportDate, 365, excludeReportDate: false);
        var built = PriceMatrix.Build(SyntheticMarket.ReportDate, 365, quotes);

        Assert.AreEqual(built.BarCount, window.BarCount);
        AssertSame(ReferenceEngine.Compute(built, Parameters), AnomalyEngine.Compute(window, Parameters));
    }

    [TestMethod]
    public void Window_OutsideMatrix_Throws()
    {
        var full = PriceMatrix.BuildRange(SyntheticMarket.ReportDate.AddDays(-365), 366, []);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => full.Window(SyntheticMarket.ReportDate.AddDays(1), 365, false));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => full.Window(SyntheticMarket.ReportDate.AddDays(-1), 365, false));
    }

    [TestMethod]
    public void ReusedTable_MatchesFreshTable()
    {
        var a = PriceMatrix.Build(SyntheticMarket.ReportDate, 365, RandomMarket.Quotes(SyntheticMarket.ReportDate.AddDays(-365), 366, seed: 4));
        var b = PriceMatrix.Build(SyntheticMarket.ReportDate, 365, RandomMarket.Quotes(SyntheticMarket.ReportDate.AddDays(-365), 366, seed: 5));

        var reused = AnomalyEngine.Compute(a, Parameters);
        var again = AnomalyEngine.Compute(b, Parameters, reuse: reused);
        Assert.AreSame(reused, again);

        AssertSame(ReferenceEngine.Compute(b, Parameters), again);
    }

    [TestMethod]
    public void BuiltInCandidates_MatchLinqOrdering()
    {
        var matrix = PriceMatrix.Build(SyntheticMarket.ReportDate, 365, RandomMarket.Quotes(SyntheticMarket.ReportDate.AddDays(-365), 366, seed: 6));
        var table = AnomalyEngine.Compute(matrix, Parameters);

        foreach (var column in new[] { "score_win_rate", "score_profit_eff", "win_rate_lcb_avg" })
        {
            var fast = PointSelector.BuiltInCandidates(table, column);
            var expected = PointSelector.OrderByPriority(Enumerable.Range(0, CandidateGrid.Count)
                .Where(i => !table.QualityExcluded[i] && CandidateGrid.Hold(i) is >= 3 and <= 15 && double.IsFinite(table.Column(column)![i]))
                .Select(i => new PointCandidate(CandidateGrid.Direction(i), CandidateGrid.Entry(i), CandidateGrid.Hold(i), table.Column(column)![i])))
                .ToArray();
            CollectionAssert.AreEqual(expected, fast.ToArray(), column);

            // 複合ポイント用（並びは score 列、値は指標のもとの値）
            var composite = PointSelector.BuiltInCandidates(table, column, "win_rate_avg");
            var expectedComposite = Enumerable.Range(0, CandidateGrid.Count)
                .Where(i => !table.QualityExcluded[i] && CandidateGrid.Hold(i) is >= 3 and <= 15
                            && double.IsFinite(table.Column(column)![i]) && double.IsFinite(table.WinRateAvg[i]))
                .OrderByDescending(i => table.Column(column)![i]).ThenBy(CandidateGrid.Entry).ThenBy(CandidateGrid.Hold).ThenBy(CandidateGrid.Direction)
                .Select(i => new PointCandidate(CandidateGrid.Direction(i), CandidateGrid.Entry(i), CandidateGrid.Hold(i), table.WinRateAvg[i]))
                .ToArray();
            CollectionAssert.AreEqual(expectedComposite, composite.ToArray(), column + " (composite)");
        }
    }

    private static void AssertSame(ReferenceEngine.Result expected, CandidateTable actual)
    {
        CollectionAssert.AreEqual(expected.Periods.ToArray(), actual.Periods.ToArray());
        for (var p = 0; p < expected.Periods.Count; p++)
        {
            var e = expected.ByPeriod[p];
            var a = actual.ByPeriod[p];
            var name = $"{e.Days}日";
            CollectionAssert.AreEqual(e.N, a.N, name + " N");
            CollectionAssert.AreEqual(e.Wins, a.Wins, name + " Wins");
            AssertBits(e.WinRate, a.WinRate, name + " WinRate");
            AssertBits(e.WinRateLcb, a.WinRateLcb, name + " WinRateLcb");
            AssertBits(e.Total, a.Total, name + " Total");
            AssertBits(e.Mean, a.Mean, name + " Mean");
            AssertBits(e.Sigma, a.Sigma, name + " Sigma");
            AssertBits(e.ProfitEff, a.ProfitEff, name + " ProfitEff");
            AssertBits(e.Spread, a.Spread, name + " Spread");
        }

        AssertBits(expected.WinRateAvg, actual.WinRateAvg, "WinRateAvg");
        AssertBits(expected.TotalAvg, actual.TotalAvg, "TotalAvg");
        AssertBits(expected.SigmaAvg, actual.SigmaAvg, "SigmaAvg");
        AssertBits(expected.ProfitEffAvg, actual.ProfitEffAvg, "ProfitEffAvg");
        AssertBits(expected.SpreadAvg, actual.SpreadAvg, "SpreadAvg");
        AssertBits(expected.MaxProfitBase, actual.MaxProfitBase, "MaxProfitBase");
        AssertBits(expected.WinRateLcbAvg, actual.WinRateLcbAvg, "WinRateLcbAvg");
        CollectionAssert.AreEqual(expected.QualityExcluded, actual.QualityExcluded, "QualityExcluded");
        AssertBits(expected.ScoreWinRate, actual.ScoreWinRate, "ScoreWinRate");
        AssertBits(expected.ScoreProfitEff, actual.ScoreProfitEff, "ScoreProfitEff");
        AssertBits(expected.ScoreMaxProfit, actual.ScoreMaxProfit, "ScoreMaxProfit");
        CollectionAssert.AreEqual(expected.RankWinRate, actual.RankWinRate, "RankWinRate");
        CollectionAssert.AreEqual(expected.RankProfitEff, actual.RankProfitEff, "RankProfitEff");
        CollectionAssert.AreEqual(expected.RankMaxProfit, actual.RankMaxProfit, "RankMaxProfit");
        CollectionAssert.AreEquivalent(expected.QualityThresholds.ToArray(), actual.QualityThresholds.ToArray(), "QualityThresholds");
    }

    /// <summary>NaN 同士は等しいとみなし、それ以外はビット単位で比べる。</summary>
    private static void AssertBits(double[] expected, double[] actual, string name)
    {
        Assert.AreEqual(expected.Length, actual.Length, name);
        for (var i = 0; i < expected.Length; i++)
        {
            if (BitConverter.DoubleToInt64Bits(expected[i]) != BitConverter.DoubleToInt64Bits(actual[i]))
            {
                Assert.Fail($"{name}[{i}] ({CandidateGrid.DirectionName(CandidateGrid.Direction(i))} {CandidateGrid.Entry(i)} +{CandidateGrid.Hold(i)}): 期待 {expected[i]:R}、実際 {actual[i]:R}");
            }
        }
    }
}

/// <summary>乱数の価格（ランダムウォーク）。3% の足を欠損させ、土日（JST）は休場、spread は 0.01〜0.05 で時々 NaN（負の値）。</summary>
internal static class RandomMarket
{
    public static List<OpenQuote> Quotes(DateOnly firstDay, int days, int seed)
    {
        var rng = new Random(seed);
        var price = 150.0;
        var quotes = new List<OpenQuote>(days * CandidateGrid.MinutesPerDay);
        for (var d = 0; d < days; d++)
        {
            var day = firstDay.AddDays(d);
            if (day.DayOfWeek is DayOfWeek.Sunday)
            {
                continue;
            }

            for (var m = 0; m < CandidateGrid.MinutesPerDay; m++)
            {
                price += (rng.NextDouble() - 0.5) * 0.02;
                if (rng.NextDouble() < 0.03 || (day.DayOfWeek == DayOfWeek.Saturday && m >= 360))
                {
                    continue;
                }

                var spread = rng.NextDouble() < 0.01 ? -0.001 : 0.01 + (rng.NextDouble() * 0.04);
                quotes.Add(new OpenQuote(Jst.StartOfDayUtc(day).AddMinutes(m), Math.Round(price, 3), Math.Round(price + spread, 3)));
            }
        }

        return quotes;
    }
}
