using AnomalyStudio.Core.Analysis;
using AnomalyStudio.Core.Backtesting;
using AnomalyStudio.Core.Modes;
using AnomalyStudio.Core.Storage;
using AnomalyStudio.Core.Symbols;

namespace AnomalyStudio.Core.Tests;

/// <summary>期間別の評価で、単一銘柄・SQL・複合ポイントが同じ期間を使うことを確かめる。</summary>
[TestClass]
public sealed class PeriodSelectionTests
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

    private static readonly (int? Days, int Entry, double Value)[] ExpectedLeaders =
        [(null, 780, 4), (30, 600, 9), (90, 660, 9), (365, 720, 9)];

    private static ModeDefinition Mode(int? days) =>
        ModeDefinition.Defaults.Single(m => m.Id == (days is null ? "profit-efficiency" : $"profit-efficiency-{days}"));

    /// <summary>
    /// 各期間の利益効率は (9,1,1)、(1,9,1)、(1,1,9)、(4,4,4)。
    /// 平均では 4、期間別ではそれぞれ 9 の候補が 1 位になる。候補同士の時間帯は重ならない。
    /// </summary>
    private static CandidateTable Table(double factor = 1)
    {
        var table = new CandidateTable([30, 90, 180, 365]);
        Array.Fill(table.QualityExcluded, true);
        foreach (var (entry, shortValue, mediumValue, longValue) in new[]
        {
            (600, 9.0, 1.0, 1.0),
            (660, 1.0, 9.0, 1.0),
            (720, 1.0, 1.0, 9.0),
            (780, 4.0, 4.0, 4.0),
        })
        {
            var i = CandidateGrid.Index(3, TradeDirection.Long, entry);
            table.QualityExcluded[i] = false;
            table.Period(30).ProfitEff[i] = shortValue * factor;
            table.Period(90).ProfitEff[i] = mediumValue * factor;
            table.Period(365).ProfitEff[i] = longValue * factor;
            table.ProfitEffAvg[i] = (shortValue + mediumValue + longValue) * factor / 3;
            table.ScoreProfitEff[i] = table.ProfitEffAvg[i] / (4 * factor) * 100;
        }

        return table;
    }

    [TestMethod]
    public void PeriodSelection_RanksEachPeriodRatherThanTheAverage()
    {
        var table = Table();
        foreach (var (days, entry, _) in ExpectedLeaders)
        {
            var mode = Mode(days);
            var selection = ModeDefinition.ParseBuiltIn(mode.Sql)!;
            var points = PointSelector.SelectBuiltIn(table, selection);
            Assert.AreEqual(4, points.Count, mode.Name);
            Assert.AreEqual(entry, points[0].EntryMinute, mode.Name);
            if (days is { } period)
            {
                Assert.AreSame(table.Period(period).ProfitEff, table.Column(selection.ScoreColumn));
                Assert.AreEqual(9.0, points[0].Score, mode.Name);
            }
            else
            {
                Assert.AreEqual(100.0, points[0].Score, mode.Name);
            }
        }
    }

    [TestMethod]
    public async Task PeriodSelection_SqlAndDirectCandidatesAgree_AndCompositeUsesTheSelectedPeriod()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        var table = Table();
        var run = await db.SaveRunAsync("AAA", SyntheticMarket.ReportDate, table, 1, 1, "{}");
        foreach (var (days, entry, value) in ExpectedLeaders)
        {
            var mode = Mode(days);
            var selection = ModeDefinition.ParseBuiltIn(mode.Sql)!;
            var sqlPoints = PointSelector.SelectNonOverlapping(await db.QueryPointCandidatesAsync(run.RunId, mode.Sql));
            CollectionAssert.AreEqual(PointSelector.SelectBuiltIn(table, selection).ToArray(), sqlPoints.ToArray(), mode.Name);
            Assert.AreEqual(entry, sqlPoints[0].EntryMinute, mode.Name);

            var compositeSql = await db.QueryCompositeCandidatesAsync(run.RunId, mode.Sql, CompositeMetric.ProfitEfficiency, days);
            var compositeDirect = PointSelector.BuiltInCandidates(table, selection, ModeSql.CompositeColumn(CompositeMetric.ProfitEfficiency, days));
            // 複合 SQL の返却順は契約ではない。候補集合を比較し、選定後の順位で評価する。
            CollectionAssert.AreEqual(
                compositeDirect.OrderBy(c => (c.EntryMinute, c.HoldMinutes, c.Direction)).ToArray(),
                compositeSql.OrderBy(c => (c.EntryMinute, c.HoldMinutes, c.Direction)).ToArray(), mode.Name);
            var compositePoints = CompositePointSelector.Select(new Dictionary<string, IReadOnlyList<PointCandidate>> { ["AAA"] = compositeSql });
            Assert.AreEqual(value, compositePoints[0].Point.Score, mode.Name);
            Assert.AreEqual(entry, compositePoints[0].Point.EntryMinute, mode.Name);
        }

        // 2 銘柄でも選んだ中期の値を使う。平均へ戻ると 13:00 が 1 位になるため、この例で検出できる。
        var medium = Mode(90);
        var tableB = Table(2);
        var actual = await db.SelectCompositePointsWithoutSavingAsync(
            [("AAA", table), ("BBB", tableB)], medium.Sql, CompositeMetric.ProfitEfficiency, evaluationDays: medium.EffectiveEvaluationDays);
        var expected = CompositePointSelector.Select(new Dictionary<string, IReadOnlyList<PointCandidate>>
        {
            ["AAA"] = PointSelector.BuiltInCandidates(table, ModeDefinition.ParseBuiltIn(medium.Sql)!, "profit_eff_90"),
            ["BBB"] = PointSelector.BuiltInCandidates(tableB, ModeDefinition.ParseBuiltIn(medium.Sql)!, "profit_eff_90"),
        });
        CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray());
        Assert.AreEqual("BBB", actual[0].SymbolId);
        Assert.AreEqual(660, actual[0].Point.EntryMinute);
        Assert.AreEqual(18.0, actual[0].Point.Score);
        Assert.AreEqual(4, (await db.QueryPointCandidatesAsync(run.RunId, medium.Sql)).Count, "保存しない抽出は元の run を変えない");
    }

    [TestMethod]
    public void CompositeCacheKey_DistinguishesEvaluationPeriods_EvenWhenSqlAndIdAreEqual()
    {
        var mode = Mode(null);
        var symbols = new[] { SymbolCatalog.Create("USD/JPY") };
        var keys = new List<string>();
        foreach (var days in new int?[] { null, 30, 90, 365 })
        {
            // 同じ ID・SQL で期間だけ変え、SQL の違いで偶然別キーになるテストにしない。
            keys.Add(WalkForwardBacktester.CompositeCacheKey(mode with { EvaluationDays = days }, CompositeMetric.ProfitEfficiency, symbols));
        }

        Assert.AreEqual(4, keys.Distinct().Count());
        var shortMode = Mode(30);
        Assert.AreEqual(
            WalkForwardBacktester.CompositeCacheKey(shortMode, CompositeMetric.ProfitEfficiency, symbols),
            WalkForwardBacktester.CompositeCacheKey(shortMode with { EvaluationDays = null }, CompositeMetric.ProfitEfficiency, symbols),
            "保存データに期間の項目がなくても、既定 ID の評価期間でキャッシュを識別する");
    }
}
