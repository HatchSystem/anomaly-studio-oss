using AnomalyStudio.Core.Modes;
using AnomalyStudio.Core.Storage;

namespace AnomalyStudio.Core.Tests;

[TestClass]
public sealed class ModeSqlPromptTests
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
    public void ExtractSql_TakesTheCodeBlock_AndDropsTheSemicolon()
    {
        const string content = "以下の SQL です。\n```sql\nSELECT direction, entry_min, hold_min, score_win_rate AS score\nFROM candidate_stats;\n```\n以上";
        Assert.AreEqual("SELECT direction, entry_min, hold_min, score_win_rate AS score\nFROM candidate_stats", ModeSqlPrompt.ExtractSql(content));
    }

    [TestMethod]
    public void ExtractSql_UsesTheWholeText_WithoutCodeBlock()
    {
        Assert.AreEqual("SELECT 1", ModeSqlPrompt.ExtractSql("  SELECT 1;\r\n"));
        Assert.AreEqual("SELECT 1", ModeSqlPrompt.ExtractSql("```\nSELECT 1\n```"));
    }

    [TestMethod]
    public void System_IncludesSchemaRulesAndFormatExample()
    {
        var prompt = ModeSqlPrompt.System(AnalysisDatabase.CandidateStatsSchema);
        StringAssert.Contains(prompt, "CREATE TABLE IF NOT EXISTS candidate_stats");
        StringAssert.Contains(prompt, "profit_eff_avg");
        StringAssert.Contains(prompt, "run_id = $run_id");
        StringAssert.Contains(prompt, "ORDER BY score DESC, entry_min, hold_min, direction");
        StringAssert.Contains(prompt, ModeSqlPrompt.FormatExample.Trim());
        StringAssert.Contains(ModeSqlPrompt.RepairRequest("Binder Error: no_such_column"), "Binder Error: no_such_column");
    }

    [TestMethod]
    public void ReviewPrompts_IncludeSchemaInstructionAndSql_ButNotTheFormatSection()
    {
        var system = ModeSqlPrompt.ReviewSystem(AnalysisDatabase.CandidateStatsSchema);
        StringAssert.Contains(system, "CREATE TABLE IF NOT EXISTS candidate_stats");
        StringAssert.Contains(system, "\"match\": true, \"issues\": []");
        Assert.IsFalse(system.Contains("## 書き方", StringComparison.Ordinal));

        var request = ModeSqlPrompt.ReviewRequest("勝率60%以上", "SELECT 1");
        StringAssert.Contains(request, "勝率60%以上");
        StringAssert.Contains(request, "SELECT 1");
    }

    [TestMethod]
    public void ParseReview_ReadsJson_InsideTextOrCodeBlock()
    {
        Assert.AreEqual(new SqlReview(true, []).Match, ModeSqlPrompt.ParseReview("{\"match\": true, \"issues\": []}")!.Match);

        var review = ModeSqlPrompt.ParseReview("結果です。\n```json\n{\"match\": false, \"issues\": [\"勝率が 0.6 ではなく 60 になっている\", \"\"]}\n```");
        Assert.IsNotNull(review);
        Assert.IsFalse(review.Match);
        CollectionAssert.AreEqual(new[] { "勝率が 0.6 ではなく 60 になっている" }, review.Issues.ToArray());
    }

    [TestMethod]
    public void ParseReview_ReturnsNull_ForUnreadableAnswers()
    {
        Assert.IsNull(ModeSqlPrompt.ParseReview("一致しています"));
        Assert.IsNull(ModeSqlPrompt.ParseReview("{\"ok\": true}"));
        Assert.IsNull(ModeSqlPrompt.ParseReview("{\"match\": \"yes\"}"));
        Assert.IsNull(ModeSqlPrompt.ParseReview("{broken"));
    }

    [TestMethod]
    public async Task Validate_AcceptsValidSql_WithoutAnalysisResults()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        await db.ValidateModeSqlAsync(ModeSqlPrompt.FormatExample);
        foreach (var mode in ModeDefinition.Defaults)
        {
            await db.ValidateModeSqlAsync(mode.Sql);
        }
    }

    [TestMethod]
    public async Task Validate_RejectsUnknownColumns_MissingOutputs_AndSyntaxErrors()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        await Assert.ThrowsAsync<DuckDB.NET.Data.DuckDBException>(() =>
            db.ValidateModeSqlAsync("SELECT direction, entry_min, hold_min, no_such_column AS score FROM candidate_stats WHERE run_id = $run_id"));
        await Assert.ThrowsAsync<ModeSqlException>(() =>
            db.ValidateModeSqlAsync("SELECT direction, entry_min, hold_min FROM candidate_stats WHERE run_id = $run_id"));
        await Assert.ThrowsAsync<DuckDB.NET.Data.DuckDBException>(() =>
            db.ValidateModeSqlAsync("SELECT direction entry_min, FROM candidate_stats WHERE"));
        await Assert.ThrowsAsync<ModeSqlException>(() => db.ValidateModeSqlAsync("DELETE FROM candidate_stats"));
    }
}
