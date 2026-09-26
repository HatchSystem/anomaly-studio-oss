using AnomalyStudio.Core.Analysis;
using AnomalyStudio.Core.Modes;
using AnomalyStudio.Core.Storage;

namespace AnomalyStudio.Core.Tests;

/// <summary>既定モードの SQL を DuckDB なしで選ぶ経路が、SQL の結果と一致することを確かめる。</summary>
[TestClass]
public sealed class BuiltInSelectionTests
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
    public void ParseBuiltIn_RecognizesDefaultSqlOnly()
    {
        foreach (var mode in ModeDefinition.Defaults)
        {
            Assert.IsNotNull(ModeDefinition.ParseBuiltIn(mode.Sql), mode.Name);
        }

        Assert.AreEqual(new BuiltInSelection("score_win_rate"), ModeDefinition.ParseBuiltIn(ModeDefinition.BuiltInSql("score_win_rate")));
        Assert.AreEqual("win_rate_lcb_avg", ModeDefinition.ParseBuiltIn("  " + ModeDefinition.BuiltInSql("win_rate_lcb_avg").Replace("\n", "\r\n  ") + ";\n")?.ScoreColumn);
        Assert.IsNull(ModeDefinition.ParseBuiltIn(ModeDefinition.BuiltInSql("score_win_rate").Replace("BETWEEN 3 AND 15", "BETWEEN 3 AND 10")));
        Assert.IsNull(ModeDefinition.ParseBuiltIn(ModeDefinition.BuiltInSql("n_30")));
        Assert.IsNull(ModeDefinition.ParseBuiltIn("SELECT 1"));

        // 勝率重視BO: 保有時間を 1・3・5・10・15・30・60 分に限り、短期・中期・長期の勝率がすべて 55% 以上
        var bo = ModeDefinition.Defaults.Single(m => m.Id == "win-rate-bo");
        StringAssert.Contains(bo.Sql, "hold_min IN (1, 3, 5, 10, 15, 30, 60)");
        StringAssert.Contains(bo.Sql, "win_rate_30 >= 0.55");
        StringAssert.Contains(bo.Sql, "win_rate_90 >= 0.55");
        StringAssert.Contains(bo.Sql, "win_rate_365 >= 0.55");
        var selection = ModeDefinition.ParseBuiltIn(bo.Sql)!;
        Assert.AreEqual("score_win_rate", selection.ScoreColumn);
        CollectionAssert.AreEqual(new[] { 1, 3, 5, 10, 15, 30, 60 }, selection.Holds!.ToArray());
        Assert.AreEqual(0.55, selection.MinWinRate);
        CollectionAssert.AreEqual(new[] { 30, 90, 365 }, selection.WinRatePeriods!.ToArray());
        Assert.IsNull(ModeDefinition.ParseBuiltIn(bo.Sql.Replace("30, 60", "30")), "保有時間が違う SQL はユーザーの SQL として扱う");
        Assert.IsNull(ModeDefinition.ParseBuiltIn(bo.Sql.Replace("win_rate_90 >= 0.55", "win_rate_90 >= 0.6")), "勝率の条件が違う SQL も同じ");
    }

    [TestMethod]
    public void Upgrade_ReplacesOnlyThePreviousDefaultSql()
    {
        var current = ModeDefinition.Defaults.Single(m => m.Id == "win-rate-bo");
        var previous = current with
        {
            Sql = ModeDefinition.BuiltInSql(new BuiltInSelection("score_win_rate", ModeDefinition.BinaryOptionHolds)),
            Description = "保有 1・3・5・10・15・30・60 分（バイナリーオプションの判定時間）に限って、30/90/365日の平均勝率が高い時間帯を抽出",
            Enabled = false,
        };

        var upgraded = ModeDefinition.Upgrade(previous);
        Assert.AreEqual(current.Sql, upgraded.Sql);
        Assert.AreEqual(current.Description, upgraded.Description);
        Assert.IsFalse(upgraded.Enabled, "オン／オフは変えない");

        // 説明だけ編集していれば説明は残す。SQL を編集したモードや、他の既定モード・ユーザーのモードは変えない
        Assert.AreEqual("自分の説明", ModeDefinition.Upgrade(previous with { Description = "自分の説明" }).Description);
        var edited = previous with { Sql = previous.Sql.Replace("30, 60", "30") };
        Assert.AreSame(edited, ModeDefinition.Upgrade(edited));
        var winRate = ModeDefinition.Defaults.Single(m => m.Id == "win-rate");
        Assert.AreSame(winRate, ModeDefinition.Upgrade(winRate));
        var user = previous with { Id = "user-mode" };
        Assert.AreSame(user, ModeDefinition.Upgrade(user));
    }

    [TestMethod]
    public async Task SelectBuiltIn_MatchesModeSql_ForEveryBuiltInColumn()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        // 一部の候補をサンプル不足で除外し、順位に同点が混ざる状態にする
        var table = AnomalyEngine.Compute(SyntheticMarket.Build((d, m) => d >= 180 && m is >= 600 and <= 610), new EngineParameters());
        var run = await db.SaveRunAsync("TEST", SyntheticMarket.ReportDate, table, 1, 1, "{}");

        foreach (var column in ModeDefinition.BuiltInScoreColumns)
        {
            var sql = ModeDefinition.BuiltInSql(column);
            var expected = PointSelector.SelectNonOverlapping(await db.QueryPointCandidatesAsync(run.RunId, sql));
            var actual = PointSelector.SelectBuiltIn(table, column);
            CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray(), column);
            Assert.IsTrue(actual.Count > 0, column);

            // 勝率重視BO と同じ保有時間の条件でも一致する
            var boSql = ModeDefinition.BuiltInSql(ModeDefinition.BinaryOption(column));
            var boExpected = PointSelector.SelectNonOverlapping(await db.QueryPointCandidatesAsync(run.RunId, boSql));
            var boActual = PointSelector.SelectBuiltIn(table, ModeDefinition.BinaryOption(column));
            CollectionAssert.AreEqual(boExpected.ToArray(), boActual.ToArray(), column + " BO");
            Assert.IsTrue(boActual.All(p => ModeDefinition.BinaryOptionHolds.Contains(p.HoldMinutes)), column + " BO");
        }

        // 合成データでは σ = 0 の候補が多く利益効率σは値なしになるが、勝率系の列は 50 件選べる
        Assert.AreEqual(50, PointSelector.SelectBuiltIn(table, "score_win_rate").Count);
        Assert.AreEqual(50, PointSelector.SelectBuiltIn(table, "win_rate_lcb_avg").Count);
    }

    [TestMethod]
    public async Task BinaryOptionWinRateCondition_MatchesSql_AndIncludesExactly55Percent()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        var table = AnomalyEngine.Compute(SyntheticMarket.Build(), new EngineParameters());

        // 合成データで勝率 100% の 3 候補（09:05 に Close する Long）の勝率を書き換える:
        // 短期がちょうど 55% → 残す、中期が 55% 未満 → 除く、長期が値なし → 除く
        var exact = CandidateGrid.Index(1, TradeDirection.Long, 544);
        var below = CandidateGrid.Index(10, TradeDirection.Long, 535);
        var missing = CandidateGrid.Index(60, TradeDirection.Long, 485);
        table.Period(30).WinRate[exact] = 0.55;
        table.Period(90).WinRate[below] = 0.5499;
        table.Period(365).WinRate[missing] = double.NaN;
        var run = await db.SaveRunAsync("TEST", SyntheticMarket.ReportDate, table, 1, 1, "{}");

        var selection = ModeDefinition.BinaryOption("score_win_rate");
        var expected = await db.QueryPointCandidatesAsync(run.RunId, ModeDefinition.BuiltInSql(selection));
        var actual = PointSelector.BuiltInCandidates(table, selection);
        CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray());

        var keys = actual.Select(c => (c.Direction, c.EntryMinute, c.HoldMinutes)).ToHashSet();
        Assert.Contains((TradeDirection.Long, 544, 1), keys, "勝率がちょうど 55% の候補は残す");
        Assert.DoesNotContain((TradeDirection.Long, 535, 10), keys, "中期の勝率が 55% 未満");
        Assert.DoesNotContain((TradeDirection.Long, 485, 60), keys, "長期の勝率が値なし");
        Assert.IsTrue(actual.All(c =>
        {
            var i = CandidateGrid.Index(c.HoldMinutes, c.Direction, c.EntryMinute);
            return new[] { 30, 90, 365 }.All(days => table.Period(days).WinRate[i] >= 0.55);
        }), "すべての候補が 3 期間とも 55% 以上");
    }

    [TestMethod]
    public async Task BuiltInCandidates_MatchComposite_ForEveryMetric()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        var table = AnomalyEngine.Compute(SyntheticMarket.Build(), new EngineParameters());
        var run = await db.SaveRunAsync("TEST", SyntheticMarket.ReportDate, table, 1, 1, "{}");

        foreach (var mode in ModeDefinition.Defaults)
        {
            var metric = mode.EffectiveCompositeMetric!.Value;
            var expected = (await db.QueryCompositeCandidatesAsync(run.RunId, mode.Sql, metric)).OrderBy(c => (c.EntryMinute, c.HoldMinutes, c.Direction)).ToArray();
            var actual = PointSelector.BuiltInCandidates(table, ModeDefinition.ParseBuiltIn(mode.Sql)!, ModeSql.CompositeColumn(metric))
                .OrderBy(c => (c.EntryMinute, c.HoldMinutes, c.Direction)).ToArray();
            CollectionAssert.AreEqual(expected, actual, mode.Name);
        }
    }

    [TestMethod]
    public async Task WinRateLowerBound_IsSaved_AndAddedToOldDatabases()
    {
        // 以前の版の DB: 信頼下限の列がない
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        using (var old = new DuckDB.NET.Data.DuckDBConnection($"Data Source={paths.DatabaseFile}"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = AnalysisDatabase.CandidateStatsSchema.Replace(
                ",\n    win_rate_lcb_30 DOUBLE, win_rate_lcb_90 DOUBLE, win_rate_lcb_180 DOUBLE, win_rate_lcb_365 DOUBLE, win_rate_lcb_avg DOUBLE", string.Empty);
            Assert.IsFalse(cmd.CommandText.Contains("win_rate_lcb", StringComparison.Ordinal));
            cmd.ExecuteNonQuery();
        }

        await using var db = new AnalysisDatabase(paths);
        var table = AnomalyEngine.Compute(SyntheticMarket.Build(), new EngineParameters());
        var run = await db.SaveRunAsync("TEST", SyntheticMarket.ReportDate, table, 1, 1, "{}");

        var points = await db.QueryPointCandidatesAsync(run.RunId, ModeDefinition.BuiltInSql("win_rate_lcb_avg"));
        var winner = points.Single(p => p is { Direction: TradeDirection.Long, EntryMinute: 540, HoldMinutes: 5 });
        Assert.AreEqual(table.WinRateLcbAvg[CandidateGrid.Index(5, TradeDirection.Long, 540)], winner.Score, 1e-9);

        var lcb30 = await db.QueryPointCandidatesAsync(run.RunId,
            "SELECT direction, entry_min, hold_min, win_rate_lcb_30 AS score FROM candidate_stats WHERE run_id = $run_id AND entry_min = 540 AND hold_min = 5 AND direction = 'Long'");
        Assert.AreEqual(0.8864866068260312, lcb30.Single().Score, 1e-9);
    }

    [TestMethod]
    public void ModeSql_RejectsFunctionsThatReadOutsideTheTable()
    {
        foreach (var sql in new[]
        {
            "SELECT direction, entry_min, hold_min, 1 AS score FROM read_parquet('C:/secret.parquet')",
            "SELECT direction, entry_min, hold_min, 1 AS score FROM candidate_stats WHERE run_id = $run_id AND entry_min = (SELECT count(*) FROM glob('*'))",
            "SELECT direction, entry_min, hold_min, getenv('PATH') AS score FROM candidate_stats WHERE run_id = $run_id",
            "SELECT * FROM READ_CSV_AUTO('x.csv')",
        })
        {
            Assert.ThrowsExactly<ModeSqlException>(() => ModeSql.Prepare(sql, 1), sql);
        }

        // 列名や通常の関数は通る
        ModeSql.Prepare("SELECT direction, entry_min, hold_min, coalesce(win_rate_lcb_avg, 0) AS score FROM candidate_stats WHERE run_id = $run_id", 1);
    }
}
