using AnomalyStudio.Core.Analysis;

namespace AnomalyStudio.Core.Tests;

[TestClass]
public sealed class AnomalyEngineTests
{
    private const double Tolerance = 1e-9;
    private static readonly EngineParameters Parameters = new() { FallbackSpread = 0.5 };

    private static CandidateTable Compute(Func<int, int, bool>? missing = null) =>
        AnomalyEngine.Compute(SyntheticMarket.Build(missing), Parameters);

    [TestMethod]
    public void Grid_Has172800UniqueCandidates_AndIndexRoundTrips()
    {
        var indices = (
            from hold in Enumerable.Range(1, 60)
            from direction in new[] { TradeDirection.Long, TradeDirection.Short }
            from entry in Enumerable.Range(0, 1440)
            select CandidateGrid.Index(hold, direction, entry)).ToList();
        Assert.HasCount(172_800, indices.Distinct());
        Assert.AreEqual(0, indices.Min());
        Assert.AreEqual(CandidateGrid.Count - 1, indices.Max());

        var i = CandidateGrid.Index(17, TradeDirection.Short, 1439);
        Assert.AreEqual(17, CandidateGrid.Hold(i));
        Assert.AreEqual(TradeDirection.Short, CandidateGrid.Direction(i));
        Assert.AreEqual(1439, CandidateGrid.Entry(i));
        Assert.AreEqual(16, CandidateGrid.Close(i));
        Assert.IsTrue(CandidateGrid.CrossesDay(i));
    }

    [TestMethod]
    public void LongIntoSpike_MatchesHandCalculation()
    {
        // Long 09:00 → 09:05: raw は偶数日 +1、奇数日 +2
        var t = Compute();
        var i = CandidateGrid.Index(5, TradeDirection.Long, 540);
        var p30 = t.Period(30);

        // 30 日 = 行 335〜364（奇数から始まるので +2 と +1 が 15 回ずつ）
        Assert.AreEqual(30, p30.N[i]);
        Assert.AreEqual(30, p30.Wins[i]);
        Assert.AreEqual(1.0, p30.WinRate[i], Tolerance);
        Assert.AreEqual(0.1, p30.Spread[i], Tolerance);
        Assert.AreEqual((15 * 1 + 15 * 2) - (30 * 0.1), p30.Total[i], Tolerance);
        Assert.AreEqual(1.5 - 0.1, p30.Mean[i], Tolerance);

        // σ は raw の標本標準偏差（ddof=1）
        var sigma = Math.Sqrt(30 * 0.25 / 29);
        Assert.AreEqual(sigma, p30.Sigma[i], Tolerance);
        Assert.AreEqual(p30.Total[i] / (sigma * Math.Sqrt(30)), p30.ProfitEff[i], Tolerance);
    }

    [TestMethod]
    public void BinaryOptionHolds_CloseExactlyHoldMinutesAfterEntry()
    {
        // 決済は Entry の足から保有分数後の足の始値（10 分なら 08:55 → 09:05）。1 分後の足（09:06）ではない
        var t = Compute();
        var p30 = t.Period(30);
        foreach (var (hold, entry) in new[] { (1, 544), (10, 535), (60, 485) })
        {
            var i = CandidateGrid.Index(hold, TradeDirection.Long, entry);
            Assert.AreEqual(30, p30.Wins[i], $"{hold} 分");
            Assert.AreEqual((15 * 1 + 15 * 2) - (30 * 0.1), p30.Total[i], Tolerance, $"{hold} 分");
        }

        // 08:56 から 10 分後は 09:06 で、跳ねた 09:05 を過ぎているので raw = 0（spread の分だけ負け）
        var late = CandidateGrid.Index(10, TradeDirection.Long, 536);
        Assert.AreEqual(0, p30.Wins[late]);
        Assert.AreEqual(-30 * 0.1, p30.Total[late], Tolerance);
    }

    [TestMethod]
    public void ShortIntoSpike_LosesEveryTime()
    {
        var t = Compute();
        var i = CandidateGrid.Index(5, TradeDirection.Short, 540);
        var p30 = t.Period(30);
        Assert.AreEqual(0, p30.Wins[i]);
        Assert.AreEqual(0.0, p30.WinRate[i], Tolerance);
        Assert.AreEqual(-45 - 3.0, p30.Total[i], Tolerance);

        // σ は方向によらず同じ
        Assert.AreEqual(t.Period(30).Sigma[CandidateGrid.Index(5, TradeDirection.Long, 540)], p30.Sigma[i], Tolerance);
    }

    [TestMethod]
    public void FlatPrice_HasNoSigma_AndLosesSpread()
    {
        var t = Compute();
        var i = CandidateGrid.Index(3, TradeDirection.Long, 0);
        var p365 = t.Period(365);
        Assert.AreEqual(365, p365.N[i]);
        Assert.AreEqual(0.0, p365.WinRate[i], Tolerance);
        Assert.AreEqual(-365 * 0.1, p365.Total[i], Tolerance);
        Assert.AreEqual(0.0, p365.Sigma[i], Tolerance);
        Assert.IsTrue(double.IsNaN(p365.ProfitEff[i]), "σ = 0 のとき利益効率σは値なし");
    }

    [TestMethod]
    public void CrossingDayCandidate_UsesNextDayPrice()
    {
        // 23:58 から 5 分保有 → 翌日 00:03 に Close。行 364 の Close は基準日当日（行 365）を参照する
        var t = Compute();
        var i = CandidateGrid.Index(5, TradeDirection.Long, 1438);
        Assert.IsTrue(CandidateGrid.CrossesDay(i));
        Assert.AreEqual(365, t.Period(365).N[i]);
    }

    [TestMethod]
    public void ScoreAverages_UseScorePeriodsOnly()
    {
        var t = Compute();
        var i = CandidateGrid.Index(5, TradeDirection.Long, 540);
        var expected = new[] { 30, 90, 365 }.Average(p => t.Period(p).Mean[i]);
        Assert.AreEqual(expected, t.MaxProfitBase[i], Tolerance);
    }

    [TestMethod]
    public void WinRateScore_IsNormalizedToUniverseMax()
    {
        var t = Compute();
        var winner = CandidateGrid.Index(5, TradeDirection.Long, 540);
        var loser = CandidateGrid.Index(3, TradeDirection.Long, 0);
        Assert.AreEqual(100.0, t.ScoreWinRate[winner], Tolerance);
        Assert.AreEqual(0.0, t.ScoreWinRate[loser], Tolerance);
        Assert.AreEqual(1, t.RankWinRate[winner]);
    }

    [TestMethod]
    public void QualityExclusion_RemovesLowCoverageCandidates()
    {
        // 10:00〜10:10 のデータを 180 日分（全体の半分以上）欠損させる
        var t = Compute((d, m) => d >= 180 && m is >= 600 and <= 610);
        var affected = CandidateGrid.Index(3, TradeDirection.Long, 605);
        var unaffected = CandidateGrid.Index(3, TradeDirection.Long, 0);

        Assert.IsTrue(t.QualityExcluded[affected]);
        Assert.IsFalse(t.QualityExcluded[unaffected]);
        Assert.AreEqual(0, t.RankWinRate[affected], "除外された候補には順位を付けない");
        Assert.AreEqual((365, 292), t.QualityThresholds[365]);
    }

    [TestMethod]
    public void QualityExclusion_KeepsCandidatesAtExactlyThreshold()
    {
        // 30 日で最大 30 サンプル、しきい値は ceil(30 * 0.8) = 24。6 日欠損 → 24 サンプルで採用
        var t = Compute((d, m) => d >= 359 && m == 700);
        var i = CandidateGrid.Index(3, TradeDirection.Long, 700);
        Assert.AreEqual(24, t.Period(30).N[i]);
        Assert.IsFalse(t.QualityExcluded[i]);
    }

    [TestMethod]
    public void RankDescending_UsesMinRankForTies()
    {
        var scores = new[] { 10.0, 30.0, 30.0, double.NaN, 20.0 };
        var ranks = new int[scores.Length];
        AnomalyEngine.RankDescending(scores, new bool[scores.Length], ranks);
        CollectionAssert.AreEqual(new[] { 4, 1, 1, 0, 3 }, ranks);
    }

    [TestMethod]
    public void Normalize_ReturnsNaNWhenMaxIsZero()
    {
        var output = new double[2];
        AnomalyEngine.Normalize([0.0, 0.0], [false, false], output);
        Assert.IsTrue(output.All(double.IsNaN));
    }
}
