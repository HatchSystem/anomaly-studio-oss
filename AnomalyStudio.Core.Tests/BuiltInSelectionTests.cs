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
    public void BuiltInScoreColumn_RecognizesDefaultSqlOnly()
    {
        foreach (var mode in ModeDefinition.Defaults)
        {
            Assert.IsNotNull(ModeDefinition.BuiltInScoreColumn(mode.Sql), mode.Name);
        }

        Assert.AreEqual("score_win_rate", ModeDefinition.BuiltInScoreColumn(ModeDefinition.BuiltInSql("score_win_rate")));
        Assert.AreEqual("win_rate_lcb_avg", ModeDefinition.BuiltInScoreColumn("  " + ModeDefinition.BuiltInSql("win_rate_lcb_avg").Replace("\n", "\r\n  ") + ";\n"));
        Assert.IsNull(ModeDefinition.BuiltInScoreColumn(ModeDefinition.BuiltInSql("score_win_rate").Replace("BETWEEN 3 AND 15", "BETWEEN 3 AND 10")));
        Assert.IsNull(ModeDefinition.BuiltInScoreColumn(ModeDefinition.BuiltInSql("n_30")));
        Assert.IsNull(ModeDefinition.BuiltInScoreColumn("SELECT 1"));
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
        }

        // 合成データでは σ = 0 の候補が多く利益効率σは値なしになるが、勝率系の列は 50 件選べる
        Assert.AreEqual(50, PointSelector.SelectBuiltIn(table, "score_win_rate").Count);
        Assert.AreEqual(50, PointSelector.SelectBuiltIn(table, "win_rate_lcb_avg").Count);
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
            var actual = PointSelector.BuiltInCandidates(table, ModeDefinition.BuiltInScoreColumn(mode.Sql)!, ModeSql.CompositeColumn(metric))
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
