using AnomalyStudio.Core.Analysis;
using AnomalyStudio.Core.MarketData;
using AnomalyStudio.Core.Modes;
using AnomalyStudio.Core.Storage;
using AnomalyStudio.Core.Symbols;
using AnomalyStudio.Core.Trading;

namespace AnomalyStudio.Core.Tests;

[TestClass]
public sealed class StorageTests
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

    private static MinuteBar Bar(DateTime utc, double bid) =>
        new(utc, bid, bid, bid, bid, 1, bid + 0.1, bid + 0.1, bid + 0.1, bid + 0.1, 1);

    [TestMethod]
    public async Task Bars_AreSavedMonthly_AndMergedWithoutDuplicates()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        var t0 = new DateTime(2026, 8, 31, 23, 58, 0, DateTimeKind.Utc);
        await db.SaveBarsAsync("USDJPY", [Bar(t0, 1), Bar(t0.AddMinutes(1), 2), Bar(t0.AddMinutes(2), 3)]);
        await db.SaveBarsAsync("USDJPY", [Bar(t0.AddMinutes(2), 30), Bar(t0.AddMinutes(3), 4)]);

        var paths = new AppPaths(_root);
        Assert.IsTrue(File.Exists(paths.MarketFile("USDJPY", 2026, 8)));
        Assert.IsTrue(File.Exists(paths.MarketFile("USDJPY", 2026, 9)));

        var quotes = await db.LoadOpenQuotesAsync("USDJPY", t0, t0.AddHours(1));
        CollectionAssert.AreEqual(new[] { 1.0, 2.0, 30.0, 4.0 }, quotes.Select(q => q.BidOpen).ToArray());
        Assert.AreEqual(0.1, quotes[0].AskOpen - quotes[0].BidOpen, 1e-9);
        Assert.AreEqual(t0.AddMinutes(3), await db.GetLatestBarTimeAsync("USDJPY"));
    }

    [TestMethod]
    public async Task BacktestPointBatch_SavesEveryModeWithoutChangingOrderOrValues()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        var day = new DateOnly(2026, 9, 1);
        EntryPoint[] points =
        [
            new(1, TradeDirection.Short, 900, 3, 1.0 / 3),
            new(2, TradeDirection.Long, 540, 15, double.NaN),
        ];
        await db.SaveBacktestPointsBatchAsync("TEST", day, [("first", points), ("empty", []), ("third", points)]);

        foreach (var key in new[] { "first", "third" })
        {
            var saved = (await db.GetBacktestPointsAsync("TEST", key))[day];
            CollectionAssert.AreEqual(points, saved.ToArray());
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(points[0].Score), BitConverter.DoubleToInt64Bits(saved[0].Score));
        }

        Assert.AreEqual(0, (await db.GetBacktestPointsAsync("TEST", "empty"))[day].Count);
    }

    [TestMethod]
    public async Task BacktestPointBatch_ReplacesOnlyTheSpecifiedDayAndKeys()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        var day = new DateOnly(2026, 9, 1);
        EntryPoint[] before = [new(1, TradeDirection.Long, 540, 3, 1)];
        EntryPoint[] after = [new(1, TradeDirection.Short, 600, 5, 2)];
        await db.SaveBacktestPointsAsync("TEST", day.AddDays(-1), "first", before);
        await db.SaveBacktestPointsBatchAsync("TEST", day, [("first", before), ("untouched", before)]);

        await db.SaveBacktestPointsBatchAsync("TEST", day, [("first", after), ("new", after)]);

        var first = await db.GetBacktestPointsAsync("TEST", "first");
        CollectionAssert.AreEqual(before, first[day.AddDays(-1)].ToArray());
        CollectionAssert.AreEqual(after, first[day].ToArray());
        CollectionAssert.AreEqual(before, (await db.GetBacktestPointsAsync("TEST", "untouched"))[day].ToArray());
        CollectionAssert.AreEqual(after, (await db.GetBacktestPointsAsync("TEST", "new"))[day].ToArray());
    }

    [TestMethod]
    public async Task BacktestPointBatch_ConversionFailureLeavesExistingDataUnchanged()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        var day = new DateOnly(2026, 9, 1);
        EntryPoint[] before = [new(1, TradeDirection.Long, 540, 3, 1)];
        EntryPoint[] after = [new(1, TradeDirection.Short, 600, 5, 2)];
        await db.SaveBacktestPointsAsync("TEST", day.AddDays(-1), "first", before);
        await db.SaveBacktestPointsAsync("TEST", day, "first", before);

        // 置換と新規挿入を含む入力の3モード目の変換を失敗させる。
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => db.SaveBacktestPointsBatchAsync(
            "TEST", day, [("first", after), ("new", after), ("broken", null!)]));

        var first = await db.GetBacktestPointsAsync("TEST", "first");
        CollectionAssert.AreEqual(before, first[day].ToArray());
        CollectionAssert.AreEqual(before, first[day.AddDays(-1)].ToArray());
        Assert.AreEqual(0, (await db.GetBacktestPointsAsync("TEST", "new")).Count);
        Assert.AreEqual(0, (await db.GetBacktestPointsAsync("TEST", "broken")).Count);

        // ロールバック後も次の基準日を保存できる。
        await db.SaveBacktestPointsBatchAsync("TEST", day.AddDays(1), [("first", after)]);
        CollectionAssert.AreEqual(after, (await db.GetBacktestPointsAsync("TEST", "first"))[day.AddDays(1)].ToArray());
    }

    [TestMethod]
    public async Task BacktestPointBatch_DuplicateKeysKeepTheLastValue()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        var day = new DateOnly(2026, 9, 1);
        EntryPoint[] before = [new(1, TradeDirection.Long, 540, 3, 1)];
        EntryPoint[] after = [new(1, TradeDirection.Short, 600, 5, 2)];
        await db.SaveBacktestPointsBatchAsync("TEST", day, [("same", before), ("other", before), ("same", after)]);
        CollectionAssert.AreEqual(after, (await db.GetBacktestPointsAsync("TEST", "same"))[day].ToArray());
        CollectionAssert.AreEqual(before, (await db.GetBacktestPointsAsync("TEST", "other"))[day].ToArray());

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => db.SaveBacktestPointsBatchAsync(
            "TEST", day, [("same", null!), ("same", before)]));
        CollectionAssert.AreEqual(after, (await db.GetBacktestPointsAsync("TEST", "same"))[day].ToArray());
    }

    [TestMethod]
    public async Task BacktestPointBatch_DatabaseFailureRollsBackAndAllowsTheNextSave()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        // 一時 DB だけに制約を付け、複数行 INSERT の最後の行を DB 内で失敗させる。
        using (var fixture = new DuckDB.NET.Data.DuckDBConnection($"Data Source={paths.DatabaseFile}"))
        {
            fixture.Open();
            using var command = fixture.CreateCommand();
            command.CommandText = """
                CREATE TABLE backtest_points (
                    symbol VARCHAR NOT NULL, report_date DATE NOT NULL,
                    cache_key VARCHAR NOT NULL CHECK (cache_key <> 'reject'),
                    points VARCHAR NOT NULL, created_at TIMESTAMP NOT NULL,
                    PRIMARY KEY (symbol, report_date, cache_key)
                )
                """;
            command.ExecuteNonQuery();
        }

        await using var db = new AnalysisDatabase(paths);
        var day = new DateOnly(2026, 9, 1);
        EntryPoint[] before = [new(1, TradeDirection.Long, 540, 3, 1)];
        EntryPoint[] after = [new(1, TradeDirection.Short, 600, 5, 2)];
        await db.SaveBacktestPointsAsync("TEST", day, "existing", before);
        await db.SaveBacktestPointsAsync("TEST", day.AddDays(-1), "existing", before);

        await Assert.ThrowsExactlyAsync<DuckDB.NET.Data.DuckDBException>(() => db.SaveBacktestPointsBatchAsync(
            "TEST", day, [("existing", after), ("new", after), ("reject", after)]));

        var existing = await db.GetBacktestPointsAsync("TEST", "existing");
        CollectionAssert.AreEqual(before, existing[day].ToArray());
        CollectionAssert.AreEqual(before, existing[day.AddDays(-1)].ToArray());
        Assert.AreEqual(0, (await db.GetBacktestPointsAsync("TEST", "new")).Count);
        await db.SaveBacktestPointsBatchAsync("TEST", day, [("existing", after)]);
        CollectionAssert.AreEqual(after, (await db.GetBacktestPointsAsync("TEST", "existing"))[day].ToArray());
    }

    [TestMethod]
    public async Task Run_IsSaved_AndModeSqlSelectsReferencePoints()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        var table = AnomalyEngine.Compute(SyntheticMarket.Build(), new EngineParameters());
        var run = await db.SaveRunAsync("TEST", SyntheticMarket.ReportDate, table, 10, 100, "{}");

        var latest = await db.GetLatestRunAsync("TEST");
        Assert.AreEqual(run.RunId, latest?.RunId);

        var points = await new PointService(db).GetPointsAsync(run.RunId, ModeDefinition.Defaults[0]);
        Assert.AreEqual(50, points.Count);
        Assert.AreEqual((TradeDirection.Long, 530, 15), (points[0].Direction, points[0].EntryMinute, points[0].HoldMinutes));
        // SQL 抽出でも 09:05 の決済と同時刻の Short を除外し、次の非重複候補を選ぶ。
        Assert.AreEqual((TradeDirection.Long, 0, 3), (points[1].Direction, points[1].EntryMinute, points[1].HoldMinutes));
        Assert.IsFalse(points.Any(p => p.EntryMinute == SyntheticMarket.SpikeMinute));

        var summaries = await db.GetCandidateSummariesAsync(run.RunId, points.Take(1));
        Assert.AreEqual(1.0, summaries.Single().WinRateAvg, 1e-9);
    }

    [TestMethod]
    public async Task RetiredTimeEfficiencyColumns_AreDroppedFromOldDatabases()
    {
        // 以前の版の DB: 時間効率の列（time_eff_base / score_time_eff / rank_time_eff）を持つ
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        using (var old = new DuckDB.NET.Data.DuckDBConnection($"Data Source={paths.DatabaseFile}"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = AnalysisDatabase.CandidateStatsSchema
                .Replace("max_profit_base DOUBLE,", "max_profit_base DOUBLE, time_eff_base DOUBLE,")
                .Replace("score_max_profit DOUBLE,", "score_max_profit DOUBLE, score_time_eff DOUBLE,")
                .Replace("rank_profit_eff INTEGER,", "rank_profit_eff INTEGER, rank_time_eff INTEGER,");
            cmd.ExecuteNonQuery();
        }

        await using var db = new AnalysisDatabase(paths);
        var run = await db.SaveRunAsync("TEST", SyntheticMarket.ReportDate, AnomalyEngine.Compute(SyntheticMarket.Build(), new EngineParameters()), 1, 1, "{}");
        Assert.AreEqual(50, (await db.QueryPointCandidatesAsync(run.RunId, ModeDefinition.Defaults[0].Sql)).Take(50).Count());
        await Assert.ThrowsAsync<Exception>(() => db.QueryPointCandidatesAsync(run.RunId,
            "SELECT direction, entry_min, hold_min, score_time_eff AS score FROM candidate_stats WHERE run_id = $run_id"));
    }

    [TestMethod]
    public async Task Reset_RemovesBarsRunsAndBacktestPoints()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        var t0 = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        await db.SaveBarsAsync("USDJPY", [Bar(t0, 1), Bar(t0.AddMinutes(1), 2)]);
        await db.SaveRunAsync("USDJPY", SyntheticMarket.ReportDate, AnomalyEngine.Compute(SyntheticMarket.Build(), new EngineParameters()), 1, 1, "{}");
        await db.SaveBacktestPointsAsync("USDJPY", new DateOnly(2026, 9, 1), "k", [new EntryPoint(1, TradeDirection.Long, 540, 5, 1)]);

        await db.ResetAsync();

        Assert.AreEqual(0, (await db.GetMarketCoverageAsync("USDJPY")).Count);
        Assert.IsNull(await db.GetLatestRunAsync("USDJPY"));
        Assert.AreEqual(0, (await db.GetBacktestPointsAsync("USDJPY", "k")).Count);
        Assert.IsFalse(Directory.Exists(new AppPaths(_root).MarketDirectory("USDJPY")));

        // 初期化のあとも取り込み・分析を続けられる
        await db.SaveBarsAsync("USDJPY", [Bar(t0, 1)]);
        Assert.AreEqual(1, (await db.GetMarketCoverageAsync("USDJPY")).Count);
    }

    [TestMethod]
    public async Task OldRuns_ArePruned()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        var table = AnomalyEngine.Compute(SyntheticMarket.Build(), new EngineParameters());
        var first = await db.SaveRunAsync("TEST", SyntheticMarket.ReportDate, table, 1, 1, "{}");
        var second = await db.SaveRunAsync("TEST", SyntheticMarket.ReportDate, table, 1, 1, "{}");
        await db.PruneRunsAsync("TEST", keep: 1);

        Assert.AreEqual(second.RunId, (await db.GetLatestRunAsync("TEST"))?.RunId);
        await Assert.ThrowsExactlyAsync<ModeSqlException>(() =>
            db.QueryPointCandidatesAsync(first.RunId, "SELECT 1 AS x"));
        Assert.AreEqual(0, (await db.QueryPointCandidatesAsync(first.RunId, ModeDefinition.Defaults[0].Sql)).Count);
    }

    [TestMethod]
    public async Task Compact_ShrinksFile_KeepsData_AndContinuesRunIds()
    {
        var paths = new AppPaths(_root);
        var table = AnomalyEngine.Compute(SyntheticMarket.Build(), new EngineParameters());
        long before;
        long lastRunId;
        await using (var db = new AnalysisDatabase(paths, compactionMinFreeBytes: long.MaxValue))
        {
            await db.SaveRunAsync("TEST", SyntheticMarket.ReportDate, table, 1, 1, "{}");
            await db.SaveRunAsync("TEST", SyntheticMarket.ReportDate, table, 1, 1, "{}");
            lastRunId = (await db.SaveRunAsync("TEST", SyntheticMarket.ReportDate, table, 1, 1, "{}")).RunId;
            await db.SaveBacktestPointsAsync("TEST", new DateOnly(2026, 9, 1), "k", [new EntryPoint(1, TradeDirection.Long, 540, 5, 1)]);
            await db.PruneRunsAsync("TEST", keep: 1);
            before = (await db.GetDatabaseSizeAsync()).FileBytes;

            (long Before, long After)? compacted = null;
            db.Compacted += sizes => compacted = sizes;
            await db.CompactAsync();

            Assert.IsNotNull(compacted);
            Assert.IsLessThan(before, compacted.Value.After, "作り直したファイルは小さくなる");
            Assert.AreEqual(lastRunId, (await db.GetLatestRunAsync("TEST"))?.RunId);
            Assert.AreEqual(50, (await new PointService(db).GetPointsAsync(lastRunId, ModeDefinition.Defaults[0])).Count);
            Assert.AreEqual(1, (await db.GetBacktestPointsAsync("TEST", "k")).Count);

            // 順序番号は続きから
            Assert.AreEqual(lastRunId + 1, (await db.SaveRunAsync("TEST", SyntheticMarket.ReportDate, table, 1, 1, "{}")).RunId);
        }

        Assert.IsFalse(File.Exists(paths.DatabaseFile + ".compact"));
        Assert.IsFalse(File.Exists(paths.DatabaseFile + ".old"));

        // 開き直しても読める
        await using var reopened = new AnalysisDatabase(paths);
        Assert.AreEqual(lastRunId + 1, (await reopened.GetLatestRunAsync("TEST"))?.RunId);
    }

    [TestMethod]
    public async Task Prune_CompactsAutomatically_WhenFreeSpaceIsLarge()
    {
        var paths = new AppPaths(_root);
        var table = AnomalyEngine.Compute(SyntheticMarket.Build(), new EngineParameters());
        await using var db = new AnalysisDatabase(paths, compactionMinFreeBytes: 1);
        for (var i = 0; i < 4; i++)
        {
            await db.SaveRunAsync("TEST", SyntheticMarket.ReportDate, table, 1, 1, "{}");
        }

        var compacted = false;
        db.Compacted += _ => compacted = true;
        var before = (await db.GetDatabaseSizeAsync()).FileBytes;
        await db.PruneRunsAsync("TEST", keep: 1);

        Assert.IsTrue(compacted, "空きがしきい値を超えたので自動で縮める");
        Assert.IsLessThan(before, (await db.GetDatabaseSizeAsync()).FileBytes);
        Assert.IsNotNull(await db.GetLatestRunAsync("TEST"));
    }

    [TestMethod]
    public async Task DeleteMarketData_AlsoRemovesRunsOfThatSymbolOnly()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        var t0 = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var table = AnomalyEngine.Compute(SyntheticMarket.Build(), new EngineParameters());
        await db.SaveBarsAsync("USDJPY", [Bar(t0, 1)]);
        var removed = await db.SaveRunAsync("USDJPY", SyntheticMarket.ReportDate, table, 1, 1, "{}");
        var kept = await db.SaveRunAsync("EURUSD", SyntheticMarket.ReportDate, table, 1, 1, "{}");

        await db.DeleteMarketDataAsync("USDJPY");

        Assert.IsNull(await db.GetLatestRunAsync("USDJPY"), "銘柄を追加し直しても古い分析結果が出ない");
        Assert.AreEqual(0, (await db.QueryPointCandidatesAsync(removed.RunId, ModeDefinition.Defaults[0].Sql)).Count, "候補統計も消える");
        Assert.AreEqual(kept.RunId, (await db.GetLatestRunAsync("EURUSD"))?.RunId);
        Assert.IsGreaterThan(0, (await db.QueryPointCandidatesAsync(kept.RunId, ModeDefinition.Defaults[0].Sql)).Count);
        Assert.AreEqual(0, (await db.GetMarketCoverageAsync("USDJPY")).Count);
    }

    [TestMethod]
    public void AnalysisRun_IsCurrent_OnlyForTheCurrentLogicVersion()
    {
        var run = new AnalysisRun(1, "USDJPY", SyntheticMarket.ReportDate, EngineParameters.LogicVersion, DateTime.UtcNow, 1, 1);
        Assert.IsTrue(run.IsCurrent);
        Assert.IsFalse((run with { LogicVersion = EngineParameters.LogicVersion + "-old" }).IsCurrent, "計算方式の版が違う結果は再分析が必要");
    }

    [TestMethod]
    public void ShouldCompact_RequiresBothAbsoluteAndRelativeFreeSpace()
    {
        const long mb = 1024 * 1024;
        Assert.IsTrue(AnalysisDatabase.ShouldCompact(fileBytes: 500 * mb, freeBytes: 400 * mb, minFreeBytes: 64 * mb));
        Assert.IsFalse(AnalysisDatabase.ShouldCompact(fileBytes: 500 * mb, freeBytes: 60 * mb, minFreeBytes: 64 * mb), "絶対量が小さい");
        Assert.IsFalse(AnalysisDatabase.ShouldCompact(fileBytes: 5000 * mb, freeBytes: 100 * mb, minFreeBytes: 64 * mb), "割合が小さい");
    }

    [TestMethod]
    public void TradeEvaluator_AcceptsUnsortedQuotes()
    {
        var day = new DateOnly(2026, 9, 1);
        var entryUtc = Jst.ToUtc(day.ToDateTime(new TimeOnly(9, 0)));
        var quotes = new[]
        {
            new OpenQuote(entryUtc.AddMinutes(5), 101.0, 101.2),
            new OpenQuote(entryUtc.AddMinutes(3), 100.5, 100.7),
            new OpenQuote(entryUtc, 100.0, 100.2),
        };
        var evaluator = new TradeEvaluator(quotes, entryUtc.AddMinutes(10), fallbackSpread: 0.5);

        Assert.AreEqual(0.8, evaluator.Evaluate(day, new EntryPoint(1, TradeDirection.Long, 540, 5, 0)).Net, 1e-9);
        Assert.AreEqual(0.3, evaluator.Evaluate(day, new EntryPoint(1, TradeDirection.Long, 540, 3, 0)).Net, 1e-9);
        Assert.AreEqual(TradeStatus.NoData, evaluator.Evaluate(day, new EntryPoint(1, TradeDirection.Long, 540, 4, 0)).Status);
    }

    [TestMethod]
    [DataRow("'Unknown'", "540", "3")]
    [DataRow("'Long'", "-1", "3")]
    [DataRow("'Long'", "1440", "3")]
    [DataRow("'Long'", "540.5", "3")]
    [DataRow("'Long'", "CAST(540.000000000000000001 AS DECIMAL(38,18))", "3")]
    [DataRow("'Long'", "'540.000000000000000001'", "3")]
    [DataRow("'Long'", "'invalid'", "3")]
    [DataRow("'Long'", "540", "0")]
    [DataRow("'Long'", "540", "-1")]
    [DataRow("'Long'", "540", "61")]
    [DataRow("'Long'", "540", "3.5")]
    [DataRow("'Long'", "540", "CAST(3.000000000000000001 AS DECIMAL(38,18))")]
    [DataRow("'Long'", "540", "'3.000000000000000000000000000001'")]
    public async Task ModeSql_RejectsInvalidCandidateValues(string direction, string entry, string hold)
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        var sql = $"SELECT {direction} AS direction, {entry} AS entry_min, {hold} AS hold_min, 1 AS score";
        await Assert.ThrowsExactlyAsync<ModeSqlException>(() => db.QueryPointCandidatesAsync(1, sql));

        // 拒否した後もトランザクションが残らず、有効な候補を読み取れる。
        var candidates = await db.QueryPointCandidatesAsync(1,
            "SELECT 'short' AS direction, 1439 AS entry_min, 60 AS hold_min, 1 AS score");
        Assert.AreEqual(new PointCandidate(TradeDirection.Short, 1439, 60, 1), candidates.Single());
    }

    [TestMethod]
    public void ModeSql_RejectsNonSelectStatements()
    {
        Assert.ThrowsExactly<ModeSqlException>(() => ModeSql.Prepare("DELETE FROM candidate_stats", 1));
        Assert.ThrowsExactly<ModeSqlException>(() => ModeSql.Prepare("SELECT 1; DROP TABLE candidate_stats", 1));
        Assert.ThrowsExactly<ModeSqlException>(() => ModeSql.Prepare("   ", 1));
        Assert.AreEqual("SELECT * FROM t WHERE run_id = 7", ModeSql.Prepare("-- コメント\nSELECT * FROM t WHERE run_id = $run_id;", 7));
    }

    [TestMethod]
    public void TradeEvaluator_ComputesSettledPendingAndNoData()
    {
        var day = new DateOnly(2026, 9, 1);
        var entryUtc = Jst.ToUtc(day.ToDateTime(new TimeOnly(9, 0)));
        var quotes = new[]
        {
            new OpenQuote(entryUtc, 100.0, 100.2),
            new OpenQuote(entryUtc.AddMinutes(5), 101.0, 101.2),
        };
        var evaluator = new TradeEvaluator(quotes, entryUtc.AddMinutes(10), fallbackSpread: 0.5);

        var longTrade = evaluator.Evaluate(day, new EntryPoint(1, TradeDirection.Long, 540, 5, 0));
        Assert.AreEqual(TradeStatus.Settled, longTrade.Status);
        Assert.AreEqual(1.0, longTrade.Raw, 1e-9);
        Assert.AreEqual(0.2, longTrade.Spread, 1e-9);
        Assert.AreEqual(0.8, longTrade.Net, 1e-9);
        Assert.IsTrue(longTrade.IsWin);

        var shortTrade = evaluator.Evaluate(day, new EntryPoint(1, TradeDirection.Short, 540, 5, 0));
        Assert.AreEqual(-1.2, shortTrade.Net, 1e-9);

        Assert.AreEqual(TradeStatus.NoData, evaluator.Evaluate(day, new EntryPoint(1, TradeDirection.Long, 541, 3, 0)).Status);
        Assert.AreEqual(TradeStatus.Pending, evaluator.Evaluate(day, new EntryPoint(1, TradeDirection.Long, 540, 30, 0)).Status);
    }

    [TestMethod]
    public void DukascopyResponse_IsParsed()
    {
        var rows = DukascopyMarketDataSource.Parse(
            "_callbacks____abc([[1755475200000,147.229,147.279,147.223,147.274,111.81],[1755475260000,147.274,147.32,147.268,147.303,138.93]]);",
            "_callbacks____abc");
        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual(new DateTime(2025, 8, 18, 0, 0, 0, DateTimeKind.Utc), rows[0].Bar.TimeUtc);
        Assert.AreEqual(147.229, rows[0].Bar.Open, 1e-9);
        Assert.AreEqual(147.303, rows[1].Bar.Close, 1e-9);
    }

    [TestMethod]
    public void SymbolCatalog_AssignsUnitsByInstrument()
    {
        var jpy = SymbolCatalog.Create("usd/jpy");
        Assert.AreEqual(("USDJPY", 0.01, "pips"), (jpy.Id, jpy.UnitSize, jpy.UnitLabel));
        var gold = SymbolCatalog.Create("XAU/USD");
        Assert.AreEqual(("XAUUSD", 1.0, "USD", 0.5), (gold.Id, gold.UnitSize, gold.UnitLabel, gold.FallbackSpread));
        Assert.AreEqual(0.0001, SymbolCatalog.Create("EUR/USD").UnitSize);
    }

    [TestMethod]
    public void SymbolCatalog_Defaults_AreTheInitialPairs()
    {
        var ids = SymbolCatalog.Defaults.Select(s => s.Id).ToList();
        CollectionAssert.AreEqual(
            new[] { "USDJPY", "XAUUSD", "AUDJPY", "AUDUSD", "CADJPY", "CHFJPY", "EURAUD", "EURJPY", "EURUSD", "GBPAUD", "GBPJPY", "GBPUSD", "NZDJPY" },
            ids);
        Assert.IsTrue(SymbolCatalog.Defaults.All(s => s.Enabled));

        // 既定の銘柄はすべて銘柄追加の候補にもあり、単位は通貨ペアから決まる
        var catalog = SymbolCatalog.All.Select(s => s.Id).ToHashSet();
        Assert.IsTrue(ids.All(catalog.Contains));
        Assert.AreEqual(0.01, SymbolCatalog.Defaults.Single(s => s.Id == "GBPJPY").UnitSize);
        Assert.AreEqual(0.0001, SymbolCatalog.Defaults.Single(s => s.Id == "GBPAUD").UnitSize);
    }
}
