using AnomalyStudio.Core.Modes;
using AnomalyStudio.Core.Storage;
using DuckDB.NET.Data;

namespace AnomalyStudio.Core.Tests;

[TestClass]
public sealed class ModeSqlValidationTests
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
    [DataRow("SELECT direction, entry_min, hold_min, missing_score AS score FROM candidate_stats WHERE run_id = $run_id")]
    [DataRow("SELECT direction, entry_min, hold_min, 1 AS score FROM candidate_stats WHERE (")]
    public async Task BeforeFirstAnalysis_InvalidSqlIsRejected(string sql)
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        Assert.IsNull(await db.GetLatestRunAsync("USDJPY"));

        // SELECT 文かどうかの文字列検査だけでは両方とも通る。未分析でも DB の文法・列名検査が必要。
        _ = ModeSql.Prepare(sql, 0);
        await Assert.ThrowsExactlyAsync<DuckDBException>(() => db.ValidateModeSqlAsync(sql));

        // 検証が失敗しても接続を壊さず、既定 SQL をすべて検証できる。
        foreach (var mode in ModeDefinition.Defaults)
        {
            await db.ValidateModeSqlAsync(mode.Sql);
        }

        Assert.IsNull(await db.GetLatestRunAsync("USDJPY"));
    }

    [TestMethod]
    public async Task BeforeFirstAnalysis_MissingRequiredColumnsAreRejected()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));

        await Assert.ThrowsExactlyAsync<ModeSqlException>(() => db.ValidateModeSqlAsync("SELECT 1 AS score"));
    }
}
