using System.Text.Json;
using AnomalyStudio.Core.Modes;

namespace AnomalyStudio.Core.Tests;

/// <summary>モードの編集・削除で使う判定（名前の確認、既定モード、廃止した既定モード）。</summary>
[TestClass]
public sealed class ModeDefinitionTests
{
    private static ModeDefinition UserMode(string id, string name) =>
        new() { Id = id, Name = name, Description = string.Empty, Sql = ModeDefinition.BuiltInSql("score_win_rate") };

    [TestMethod]
    public void ValidateName_RejectsDuplicatesAndUnusableNames()
    {
        var modes = new List<ModeDefinition>([.. ModeDefinition.Defaults, UserMode("u1", "朝の逆張り")]);

        Assert.IsNull(ModeDefinition.ValidateName("夜の順張り", modes, selfId: null), "新しい名前は使える");
        Assert.IsNull(ModeDefinition.ValidateName("朝の逆張り", modes, selfId: "u1"), "自分の名前のままなら保存できる");
        Assert.IsNull(ModeDefinition.ValidateName("勝率重視", modes, selfId: "win-rate"));

        // 同じ名前のモードは登録できない（既定モード・自作モードのどちらとも）
        StringAssert.Contains(ModeDefinition.ValidateName("朝の逆張り", modes, selfId: null), "既にあります");
        StringAssert.Contains(ModeDefinition.ValidateName("勝率重視", modes, selfId: "u1"), "既にあります");
        StringAssert.Contains(ModeDefinition.ValidateName("勝率重視BO", modes, selfId: "win-rate"), "既にあります");

        Assert.IsNotNull(ModeDefinition.ValidateName("", modes, null));
        Assert.IsNotNull(ModeDefinition.ValidateName("   ", modes, null));
        Assert.IsNotNull(ModeDefinition.ValidateName(" 前に空白", modes, null));
        Assert.IsNotNull(ModeDefinition.ValidateName(ModeDefinition.AllModesLabel, modes, null), "エントリー画面の「すべて」と区別できない");
        foreach (var c in "\\/:*?\"<>|")
        {
            Assert.IsNotNull(ModeDefinition.ValidateName($"名前{c}", modes, null), $"ファイル名に使えない文字 {c}");
        }
    }

    [TestMethod]
    public void DefaultModes_AreRecognized_AndKnowWhetherTheirSqlWasEdited()
    {
        var winRate = ModeDefinition.Defaults.Single(m => m.Id == "win-rate");
        Assert.IsTrue(winRate.IsDefault);
        Assert.IsTrue(winRate.HasDefaultSql);
        Assert.IsTrue((winRate with { Sql = "  " + winRate.Sql + ";\n" }).HasDefaultSql, "空白の違いは同じ SQL");
        Assert.IsFalse((winRate with { Sql = winRate.Sql.Replace("BETWEEN 3 AND 15", "BETWEEN 3 AND 10") }).HasDefaultSql);

        var user = UserMode("u1", "自作");
        Assert.IsFalse(user.IsDefault);
        Assert.IsFalse(user.HasDefaultSql, "自作モードは既定の SQL と同じ文でも既定ではない");
    }

    [TestMethod]
    public void WinRateLowerBoundMode_IsRetired()
    {
        Assert.IsFalse(ModeDefinition.Defaults.Any(m => m.Id == "win-rate-lcb"));
        Assert.Contains("win-rate-lcb", ModeDefinition.RetiredIds);
        CollectionAssert.AreEquivalent(new[] { "win-rate", "win-rate-bo", "profit-efficiency", "profit-efficiency-30", "profit-efficiency-90", "profit-efficiency-365" }, ModeDefinition.Defaults.Select(m => m.Id).ToArray());

        // 以前の版の modes.json（信頼下限の指標を持つモード）を読めるように、指標の値は残す
        Assert.IsTrue(Enum.IsDefined(CompositeMetric.WinRateLowerBound));
    }

    [TestMethod]
    public void PeriodProfitEfficiencyDefaults_AreOptional_AndKeepTheAverageMode()
    {
        var average = ModeDefinition.Defaults.Single(m => m.Id == "profit-efficiency");
        Assert.IsTrue(average.Enabled);
        Assert.IsNull(average.EffectiveEvaluationDays);
        Assert.AreEqual(ModeDefinition.BuiltInSql("score_profit_eff"), average.Sql);

        foreach (var (days, label) in new[] { (30, "短期30日"), (90, "中期90日"), (365, "長期365日") })
        {
            var mode = ModeDefinition.Defaults.Single(m => m.Id == $"profit-efficiency-{days}");
            Assert.AreEqual($"利益効率（{label}）", mode.Name);
            Assert.IsFalse(mode.Enabled, "追加したモードで既存の分析負荷や抽出結果を変えない");
            Assert.AreEqual(days, mode.EvaluationDays);
            Assert.AreEqual(days, mode.EffectiveEvaluationDays);
            Assert.AreEqual(CompositeMetric.ProfitEfficiency, mode.EffectiveCompositeMetric);
            Assert.AreEqual(ModeDefinition.BuiltInSql($"profit_eff_{days}"), mode.Sql);
            Assert.IsTrue(mode.IsDefault);
            Assert.IsTrue(mode.HasDefaultSql);
        }
    }

    [TestMethod]
    public void EvaluationDays_UsesDefaultIdWhenMissingFromSavedJson_AndPreservesExplicitValues()
    {
        foreach (var days in new[] { 30, 90, 365 })
        {
            // 評価期間・複合指標を持たない保存データも、既定の ID から補完する。
            var json = $$"""
                {"Id":"profit-efficiency-{{days}}","Name":"保存済み","Description":"","Sql":"SELECT 1","Enabled":false}
                """;
            var saved = JsonSerializer.Deserialize<ModeDefinition>(json)!;
            Assert.IsNull(saved.EvaluationDays);
            Assert.AreEqual(days, saved.EffectiveEvaluationDays);
            Assert.AreEqual(CompositeMetric.ProfitEfficiency, saved.EffectiveCompositeMetric);
            Assert.AreEqual(90, (saved with { EvaluationDays = 90 }).EffectiveEvaluationDays);
            Assert.IsNull((saved with { Id = "custom" }).EffectiveEvaluationDays);

            var explicitMode = saved with { EvaluationDays = days };
            var serialized = JsonSerializer.Serialize(explicitMode);
            Assert.IsFalse(serialized.Contains("EffectiveEvaluationDays", StringComparison.Ordinal));
            Assert.AreEqual(days, JsonSerializer.Deserialize<ModeDefinition>(serialized)!.EvaluationDays);
        }
    }
}
