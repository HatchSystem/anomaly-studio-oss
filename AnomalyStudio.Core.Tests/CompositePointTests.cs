using AnomalyStudio.Core.Analysis;
using AnomalyStudio.Core.Backtesting;
using AnomalyStudio.Core.MarketData;
using AnomalyStudio.Core.Modes;
using AnomalyStudio.Core.Storage;
using AnomalyStudio.Core.Symbols;

namespace AnomalyStudio.Core.Tests;

[TestClass]
public sealed class CompositePointTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup() => _root = Path.Combine(Path.GetTempPath(), "anomaly-tests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static PointCandidate Candidate(int entry, int hold, double score, TradeDirection direction = TradeDirection.Long) =>
        new(direction, entry, hold, score);

    [TestMethod]
    public void Select_RanksAcrossSymbols_WithoutOverlapAcrossSymbols()
    {
        var candidates = new Dictionary<string, IReadOnlyList<PointCandidate>>
        {
            ["AAA"] = [Candidate(600, 10, 0.9), Candidate(700, 5, 0.6), Candidate(800, 5, double.NaN)],
            ["BBB"] = [Candidate(605, 10, 0.8), Candidate(900, 5, 0.7)],
        };

        var points = CompositePointSelector.Select(candidates);

        // BBB 10:05 は AAA 10:00–10:10 と時間帯が重なるので、銘柄が違っても選ばない。NaN のスコアは使わない
        CollectionAssert.AreEqual(
            new[] { ("AAA", 1, 600), ("BBB", 2, 900), ("AAA", 3, 700) },
            points.Select(p => (p.SymbolId, p.Point.Rank, p.Point.EntryMinute)).ToArray());
    }

    [TestMethod]
    public void Select_BreaksTiesBySymbolId_AndStopsAtLimit()
    {
        var candidates = new Dictionary<string, IReadOnlyList<PointCandidate>>
        {
            ["BBB"] = [Candidate(100, 3, 0.5), Candidate(200, 3, 0.4)],
            ["AAA"] = [Candidate(100, 3, 0.5), Candidate(300, 3, 0.3)],
        };

        var points = CompositePointSelector.Select(candidates, limit: 2);

        CollectionAssert.AreEqual(new[] { ("AAA", 100), ("BBB", 200) }, points.Select(p => (p.SymbolId, p.Point.EntryMinute)).ToArray());
    }

    [TestMethod]
    public void DefaultModes_CompareRawValues_AndTimeEfficiencyIsRetired()
    {
        var modes = ModeDefinition.Defaults.ToDictionary(m => m.Id);
        Assert.AreEqual(CompositeMetric.WinRate, modes["win-rate"].EffectiveCompositeMetric);
        Assert.AreEqual(CompositeMetric.ProfitEfficiency, modes["profit-efficiency"].EffectiveCompositeMetric);
        Assert.IsFalse(modes.ContainsKey("time-efficiency"));
        CollectionAssert.Contains(ModeDefinition.RetiredIds.ToArray(), "time-efficiency");

        // 保存済みの modes.json に項目がない既定モードも、同じ ID の既定値で補う
        Assert.AreEqual(CompositeMetric.WinRate, (modes["win-rate"] with { CompositeMetric = null }).EffectiveCompositeMetric);
        Assert.IsNull((modes["win-rate"] with { Id = "custom", CompositeMetric = null }).EffectiveCompositeMetric);
    }

    [TestMethod]
    public async Task CompositeCandidates_UseRawValues_AndSelectWithoutSavingMatchesSavedRuns()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        var table = AnomalyEngine.Compute(SyntheticMarket.Build(), new EngineParameters());
        var mode = ModeDefinition.Defaults[0];

        var selected = await db.SelectCompositePointsWithoutSavingAsync([("AAA", table), ("BBB", table)], mode.Sql, CompositeMetric.WinRate);
        var leftover = await db.QueryPointCandidatesAsync(0, "SELECT direction, entry_min, hold_min, 0 AS score FROM candidate_stats");
        Assert.AreEqual(0, leftover.Count);

        var runA = await db.SaveRunAsync("AAA", SyntheticMarket.ReportDate, table, 1, 1, "{}");
        var runB = await db.SaveRunAsync("BBB", SyntheticMarket.ReportDate, table, 1, 1, "{}");
        var candidatesA = await db.QueryCompositeCandidatesAsync(runA.RunId, mode.Sql, CompositeMetric.WinRate);
        var candidatesB = await db.QueryCompositeCandidatesAsync(runB.RunId, mode.Sql, CompositeMetric.WinRate);

        // score は正規化スコアではなく平均勝率（0〜1）
        Assert.IsTrue(candidatesA.Count > 0 && candidatesA.All(c => c.Score is >= 0 and <= 1));
        var expected = CompositePointSelector.Select(new Dictionary<string, IReadOnlyList<PointCandidate>> { ["AAA"] = candidatesA, ["BBB"] = candidatesB });
        CollectionAssert.AreEqual(expected.ToArray(), selected.ToArray());

        // 同じ分析なのでスコアが同じになり、銘柄 ID 順で AAA が先に選ばれる
        Assert.AreEqual("AAA", selected[0].SymbolId);
    }

    [TestMethod]
    public async Task CompositeBacktestPoints_RoundTrip_AndAreClearedWithSymbolData()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        SymbolPoint[] points =
        [
            new("USDJPY", new EntryPoint(1, TradeDirection.Short, 545, 3, 0.75)),
            new("XAUUSD", new EntryPoint(2, TradeDirection.Long, 600, 10, 0.5)),
        ];
        await db.SaveCompositeBacktestPointsAsync(new DateOnly(2026, 9, 1), "k", points);

        var map = await db.GetCompositeBacktestPointsAsync("k");
        CollectionAssert.AreEqual(points, map[new DateOnly(2026, 9, 1)].ToArray());

        await db.DeleteMarketDataAsync("USDJPY");
        Assert.AreEqual(0, (await db.GetCompositeBacktestPointsAsync("k")).Count);
    }

    [TestMethod]
    public async Task RunComposite_TradesEachSymbolAtItsOwnPoints_AndCaches()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        var usd = SymbolCatalog.Create("USD/JPY");
        var xau = SymbolCatalog.Create("XAU/USD");
        var reportDate = new DateOnly(2026, 8, 16);

        // USDJPY は毎日 09:05、XAUUSD は毎日 14:00 だけ上がる。勝率が最も高い時間帯は銘柄ごとに違う
        await db.SaveBarsAsync(usd.Id, Bars(reportDate, 9 * 60 + 5, 100));
        await db.SaveBarsAsync(xau.Id, Bars(reportDate, 14 * 60, 2000));
        var mode = ModeDefinition.Defaults[0];
        var until = Jst.StartOfDayUtc(reportDate.AddDays(2));
        var dataUntil = new Dictionary<string, DateTime> { [usd.Id] = until, [xau.Id] = until };
        var backtester = new WalkForwardBacktester(db);

        var result = await backtester.RunCompositeAsync([usd, xau], mode, reportDate, reportDate, BacktestCycle.Daily, dataUntil);

        Assert.AreEqual(1, result.ComputedAnalyses);
        Assert.AreEqual(50, result.Trades.Count);
        CollectionAssert.AreEquivalent(new[] { usd.Id, xau.Id }, result.Trades.Select(t => t.SymbolId).Distinct().ToArray());
        Assert.AreEqual(50, result.Trades.Select(t => t.Trade.Point.Rank).Distinct().Count());

        // 銘柄ごとの上昇の直前に Entry する Long が、それぞれの銘柄で勝つ
        var usdWin = result.Trades.Where(t => t.SymbolId == usd.Id && t.Trade.IsWin).ToList();
        var xauWin = result.Trades.Where(t => t.SymbolId == xau.Id && t.Trade.IsWin).ToList();
        Assert.IsTrue(usdWin.Count > 0 && xauWin.Count > 0);
        Assert.IsTrue(xauWin.All(t => t.Trade.EntryPrice > 1000));

        var again = await backtester.RunCompositeAsync([usd, xau], mode, reportDate, reportDate, BacktestCycle.Daily, dataUntil);
        Assert.AreEqual(0, again.ComputedAnalyses);
        CollectionAssert.AreEqual(result.Trades.ToArray(), again.Trades.ToArray());

        var custom = mode with { Id = "custom", CompositeMetric = null };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            backtester.RunCompositeAsync([usd, xau], custom, reportDate, reportDate, BacktestCycle.Daily, dataUntil));
    }

    private static List<MinuteBar> Bars(DateOnly reportDate, int spikeMinute, double price)
    {
        var bars = new List<MinuteBar>();
        for (var day = reportDate.AddDays(-366); day <= reportDate.AddDays(1); day = day.AddDays(1))
        {
            for (var m = 0; m < CandidateGrid.MinutesPerDay; m++)
            {
                var bid = m == spikeMinute ? price * 1.01 : price;
                bars.Add(new MinuteBar(Jst.StartOfDayUtc(day).AddMinutes(m), bid, bid, bid, bid, 1, bid + 0.01, bid + 0.01, bid + 0.01, bid + 0.01, 1));
            }
        }

        return bars;
    }
}
