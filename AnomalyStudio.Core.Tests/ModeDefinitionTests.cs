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
        CollectionAssert.AreEquivalent(new[] { "win-rate", "win-rate-bo", "profit-efficiency" }, ModeDefinition.Defaults.Select(m => m.Id).ToArray());

        // 以前の版の modes.json（信頼下限の指標を持つモード）を読めるように、指標の値は残す
        Assert.IsTrue(Enum.IsDefined(CompositeMetric.WinRateLowerBound));
    }
}
