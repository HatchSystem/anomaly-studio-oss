using AnomalyStudio.Core.Analysis;
using AnomalyStudio.Core.Backtesting;
using AnomalyStudio.Core.MarketData;
using AnomalyStudio.Core.Modes;
using AnomalyStudio.Core.Storage;
using AnomalyStudio.Core.Symbols;

namespace AnomalyStudio.Core.Tests;

[TestClass]
public sealed class WalkForwardTests
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

    [TestMethod]
    public void ReportDate_Daily_IsTradeDate()
    {
        var day = new DateOnly(2026, 1, 1);
        Assert.AreEqual(day, WalkForwardSchedule.ReportDateFor(day, BacktestCycle.Daily));
    }

    [TestMethod]
    public void ReportDate_Weekly_IsSundayBeforeTheWeek()
    {
        // 2026/09/21 は月曜、2026/09/27 は日曜。どちらも週（月〜日）の直前の日曜 2026/09/20
        Assert.AreEqual(new DateOnly(2026, 9, 20), WalkForwardSchedule.ReportDateFor(new DateOnly(2026, 9, 21), BacktestCycle.Weekly));
        Assert.AreEqual(new DateOnly(2026, 9, 20), WalkForwardSchedule.ReportDateFor(new DateOnly(2026, 9, 27), BacktestCycle.Weekly));
        Assert.AreEqual(new DateOnly(2026, 9, 27), WalkForwardSchedule.ReportDateFor(new DateOnly(2026, 9, 28), BacktestCycle.Weekly));

        // 年を跨ぐ週: 2026/01/01（木）の週は 2025/12/29（月）から
        Assert.AreEqual(new DateOnly(2025, 12, 28), WalkForwardSchedule.ReportDateFor(new DateOnly(2026, 1, 1), BacktestCycle.Weekly));
    }

    [TestMethod]
    public void ReportDate_Monthly_IsFirstDayOfMonth()
    {
        Assert.AreEqual(new DateOnly(2026, 3, 1), WalkForwardSchedule.ReportDateFor(new DateOnly(2026, 3, 1), BacktestCycle.Monthly));
        Assert.AreEqual(new DateOnly(2026, 3, 1), WalkForwardSchedule.ReportDateFor(new DateOnly(2026, 3, 31), BacktestCycle.Monthly));
    }

    [TestMethod]
    public void ReportDates_AreDistinctAndSorted()
    {
        var from = new DateOnly(2026, 8, 30);
        var to = new DateOnly(2026, 9, 14);
        CollectionAssert.AreEqual(
            new[] { new DateOnly(2026, 8, 23), new DateOnly(2026, 8, 30), new DateOnly(2026, 9, 6), new DateOnly(2026, 9, 13) },
            WalkForwardSchedule.ReportDates(from, to, BacktestCycle.Weekly).ToArray());
        CollectionAssert.AreEqual(
            new[] { new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 1) },
            WalkForwardSchedule.ReportDates(from, to, BacktestCycle.Monthly).ToArray());
        Assert.AreEqual(16, WalkForwardSchedule.ReportDates(from, to, BacktestCycle.Daily).Count);
    }

    [TestMethod]
    public void RequiredRange_StartsMaxPeriodBeforeFirstReportDate()
    {
        var from = new DateOnly(2026, 9, 2);
        var to = new DateOnly(2026, 9, 30);
        var (fromUtc, toUtc) = WalkForwardBacktester.RequiredRange(from, to, BacktestCycle.Monthly, 365);
        Assert.AreEqual(Jst.StartOfDayUtc(new DateOnly(2026, 9, 1).AddDays(-365)), fromUtc);
        Assert.AreEqual(Jst.StartOfDayUtc(new DateOnly(2026, 10, 2)), toUtc);
    }

    [TestMethod]
    public async Task SelectPointsWithoutSaving_MatchesSavedRun_AndLeavesNothing()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        var table = AnomalyEngine.Compute(SyntheticMarket.Build(), new EngineParameters());
        var sqls = ModeDefinition.Defaults.Select(m => m.Sql).ToList();

        var selected = await db.SelectPointsWithoutSavingAsync("TEST", table, sqls);

        Assert.IsNull(await db.GetLatestRunAsync("TEST"));
        var leftover = await db.QueryPointCandidatesAsync(0, "SELECT direction, entry_min, hold_min, 0 AS score FROM candidate_stats");
        Assert.AreEqual(0, leftover.Count);

        var run = await db.SaveRunAsync("TEST", SyntheticMarket.ReportDate, table, 1, 1, "{}");
        for (var i = 0; i < sqls.Count; i++)
        {
            var expected = await db.QueryPointCandidatesAsync(run.RunId, sqls[i]);
            CollectionAssert.AreEqual(PointSelector.SelectNonOverlapping(expected).ToArray(), selected[i]!.ToArray());
        }
    }

    [TestMethod]
    public async Task SelectPointsWithoutSaving_StopsAtFailingOtherMode()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        var table = AnomalyEngine.Compute(SyntheticMarket.Build(), new EngineParameters());
        var good = ModeDefinition.Defaults[0].Sql;

        var selected = await db.SelectPointsWithoutSavingAsync("TEST", table, [good, "SELECT no_such_column FROM candidate_stats", good]);
        Assert.AreEqual(50, selected[0]!.Count);
        Assert.IsNull(selected[1]);
        Assert.IsNull(selected[2]);

        await Assert.ThrowsAsync<Exception>(() => db.SelectPointsWithoutSavingAsync("TEST", table, ["SELECT no_such_column FROM candidate_stats"]));
    }

    [TestMethod]
    public async Task BacktestPoints_RoundTrip()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        EntryPoint[] points = [new(1, TradeDirection.Short, 545, 3, 0.75), new(2, TradeDirection.Long, 1435, 10, double.NaN)];
        await db.SaveBacktestPointsAsync("TEST", new DateOnly(2026, 9, 1), "k", points);
        await db.SaveBacktestPointsAsync("TEST", new DateOnly(2026, 9, 2), "k", []);

        var map = await db.GetBacktestPointsAsync("TEST", "k");
        CollectionAssert.AreEqual(points, map[new DateOnly(2026, 9, 1)].ToArray());
        Assert.AreEqual(0, map[new DateOnly(2026, 9, 2)].Count);
        Assert.AreEqual(0, (await db.GetBacktestPointsAsync("TEST", "other")).Count);

        await db.DeleteMarketDataAsync("TEST");
        Assert.AreEqual(0, (await db.GetBacktestPointsAsync("TEST", "k")).Count);
    }

    [TestMethod]
    public async Task Backfill_FetchesOnlyOlderMissingRange()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        var symbol = SymbolCatalog.Create("USD/JPY");
        var t0 = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        await db.SaveBarsAsync(symbol.Id, [Bar(t0, 100), Bar(t0.AddMinutes(1), 100)]);

        var source = new HourlySource();
        var ingestor = new MarketDataIngestor(source, db);
        var added = await ingestor.BackfillAsync(symbol, t0.AddDays(-5));

        Assert.AreEqual(5 * 24, added);
        Assert.IsTrue(source.Requests.All(r => r.From == t0.AddDays(-5) && r.To == t0));
        Assert.AreEqual(t0.AddDays(-5), (await db.GetMarketCoverageAsync(symbol.Id)).FirstUtc);

        // 取得済みの範囲（許容幅内）なら取得しない
        source.Requests.Clear();
        Assert.AreEqual(0, await ingestor.BackfillAsync(symbol, t0.AddDays(-7)));
        Assert.AreEqual(0, source.Requests.Count);
    }

    [TestMethod]
    public async Task WalkForward_UsesReportDatePoints_WithoutLookAhead_AndCaches()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        var symbol = SymbolCatalog.Create("USD/JPY");
        var reportDate = new DateOnly(2026, 8, 16);

        // BID は 100、毎日 09:05 だけ 101。基準日当日の 00:10 だけ 1000 に跳ねる（前日 23:5x Entry の日跨ぎ Close にだけ効く）
        var bars = new List<MinuteBar>();
        for (var day = reportDate.AddDays(-366); day <= reportDate.AddDays(1); day = day.AddDays(1))
        {
            for (var m = 0; m < CandidateGrid.MinutesPerDay; m++)
            {
                var bid = m == SyntheticMarket.SpikeMinute ? 101.0 : 100.0;
                if (day == reportDate && m == 10)
                {
                    bid = 1000;
                }

                bars.Add(Bar(Jst.StartOfDayUtc(day).AddMinutes(m), bid));
            }
        }

        await db.SaveBarsAsync(symbol.Id, bars);
        var mode = new ModeDefinition
        {
            Id = "total",
            Name = "合計",
            Description = "30 日合計の大きい順",
            Sql = """
                SELECT direction, entry_min, hold_min, total_30 AS score FROM candidate_stats
                WHERE run_id = $run_id AND total_30 IS NOT NULL ORDER BY score DESC, entry_min, hold_min, direction
                """,
        };
        var backtester = new WalkForwardBacktester(db);
        var until = bars[^1].TimeUtc;

        var result = await backtester.RunAsync(symbol, mode, [], reportDate, reportDate, BacktestCycle.Daily, until);

        Assert.AreEqual(1, result.ComputedAnalyses);
        Assert.AreEqual(0, result.UnavailableReportDates.Count);
        Assert.AreEqual(50, result.Trades.Count);
        Assert.IsTrue(result.Trades.All(t => t.ReportDate == reportDate && t.Trade.TradeDate == reportDate));

        // 基準日当日の価格を使っていれば、23:5x Entry の日跨ぎ候補が合計 +900 で 1 位になる
        var top = result.Trades[0].Trade.Point;
        Assert.IsTrue(top.EntryMinute + top.HoldMinutes < CandidateGrid.MinutesPerDay, $"日跨ぎ候補が選ばれた: {top}");
        Assert.AreEqual(TradeDirection.Long, top.Direction);

        // 2 回目は保存済みのポイントを使う
        var again = await backtester.RunAsync(symbol, mode, [], reportDate, reportDate, BacktestCycle.Daily, until);
        Assert.AreEqual(0, again.ComputedAnalyses);
        CollectionAssert.AreEqual(result.Trades.ToArray(), again.Trades.ToArray());

        // 365 日分のデータがない基準日は分析できない（取引なし）
        var early = reportDate.AddDays(-30);
        var missing = await backtester.RunAsync(symbol, mode, [], early, early, BacktestCycle.Daily, until);
        CollectionAssert.AreEqual(new[] { early }, missing.UnavailableReportDates.ToArray());
        Assert.AreEqual(0, missing.Trades.Count);
    }

    private static MinuteBar Bar(DateTime utc, double bid) =>
        new(utc, bid, bid, bid, bid, 1, bid + 0.01, bid + 0.01, bid + 0.01, bid + 0.01, 1);

    /// <summary>要求された範囲の毎時 0 分に足を返す取得元。</summary>
    private sealed class HourlySource : IMarketDataSource
    {
        public List<(DateTime From, DateTime To)> Requests { get; } = [];

        public Task<IReadOnlyList<SideBar>> FetchAsync(
            string instrument, OfferSide side, DateTime fromUtc, DateTime toUtc, IProgress<DateTime>? progress = null, CancellationToken cancellationToken = default)
        {
            lock (Requests)
            {
                Requests.Add((fromUtc, toUtc));
            }

            var price = side == OfferSide.Bid ? 100.0 : 100.01;
            var bars = new List<SideBar>();
            for (var t = fromUtc; t < toUtc; t = t.AddHours(1))
            {
                bars.Add(new SideBar(t, price, price, price, price, 1));
            }

            return Task.FromResult<IReadOnlyList<SideBar>>(bars);
        }
    }
}
