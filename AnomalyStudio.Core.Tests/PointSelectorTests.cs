using AnomalyStudio.Core.Analysis;

namespace AnomalyStudio.Core.Tests;

[TestClass]
public sealed class PointSelectorTests
{
    [TestMethod]
    public void CloseEqualToNextEntry_IsNotOverlap()
    {
        // 09:21→09:25 は 09:21〜09:24 を占有。09:25 開始は重ならない（連続ドテン可）
        var points = PointSelector.SelectNonOverlapping(
        [
            new(TradeDirection.Long, 561, 4, 100),
            new(TradeDirection.Short, 565, 3, 90),
        ]);

        Assert.AreEqual(2, points.Count);
    }

    [TestMethod]
    public void OverlappingCandidate_IsSkipped()
    {
        var points = PointSelector.SelectNonOverlapping(
        [
            new(TradeDirection.Long, 561, 4, 100),
            new(TradeDirection.Short, 564, 3, 90),
            new(TradeDirection.Long, 600, 3, 80),
        ]);

        CollectionAssert.AreEqual(new[] { 561, 600 }, points.Select(p => p.EntryMinute).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 2 }, points.Select(p => p.Rank).ToArray());
    }

    [TestMethod]
    public void OccupancyWrapsAroundMidnight()
    {
        var points = PointSelector.SelectNonOverlapping(
        [
            new(TradeDirection.Long, 1438, 5, 100),
            new(TradeDirection.Long, 1, 3, 90),
            new(TradeDirection.Long, 3, 3, 80),
        ]);

        CollectionAssert.AreEqual(new[] { 1438, 3 }, points.Select(p => p.EntryMinute).ToArray());
    }

    [TestMethod]
    public void Priority_IsScoreThenEntryThenHoldThenLongFirst()
    {
        var ordered = PointSelector.OrderByPriority(
        [
            new(TradeDirection.Short, 10, 3, 50),
            new(TradeDirection.Long, 10, 3, 50),
            new(TradeDirection.Long, 10, 5, 50),
            new(TradeDirection.Long, 5, 9, 50),
            new(TradeDirection.Long, 900, 3, 60),
        ]).ToList();

        Assert.AreEqual(900, ordered[0].EntryMinute);
        Assert.AreEqual((5, 9), (ordered[1].EntryMinute, ordered[1].HoldMinutes));
        Assert.AreEqual((TradeDirection.Long, 3), (ordered[2].Direction, ordered[2].HoldMinutes));
        Assert.AreEqual((TradeDirection.Short, 3), (ordered[3].Direction, ordered[3].HoldMinutes));
        Assert.AreEqual(5, ordered[4].HoldMinutes);
    }

    [TestMethod]
    public void StopsAtLimit()
    {
        var candidates = Enumerable.Range(0, 200).Select(i => new PointCandidate(TradeDirection.Long, i * 5, 3, 100 - i));
        Assert.AreEqual(50, PointSelector.SelectNonOverlapping(candidates).Count);
    }

    [TestMethod]
    public void SyntheticMarket_WinRatePointsFollowReferenceTieBreak()
    {
        // 勝率 100% は「09:05 に Close する Long」と「09:05 に Entry する Short」。
        // 同スコアは Entry 昇順なので Long 08:50（保有 15 分）が 1 位、09:05 Short（保有 3 分）が 2 位
        var table = AnomalyEngine.Compute(SyntheticMarket.Build(), new EngineParameters());
        var pool = Enumerable.Range(0, CandidateGrid.Count)
            .Where(i => !table.QualityExcluded[i] && !double.IsNaN(table.ScoreWinRate[i])
                        && CandidateGrid.Hold(i) is >= PointSelector.BuiltInHoldMin and <= PointSelector.BuiltInHoldMax)
            .Select(i => new PointCandidate(CandidateGrid.Direction(i), CandidateGrid.Entry(i), CandidateGrid.Hold(i), table.ScoreWinRate[i]));

        var points = PointSelector.SelectNonOverlapping(PointSelector.OrderByPriority(pool));

        Assert.AreEqual((TradeDirection.Long, 530, 15), (points[0].Direction, points[0].EntryMinute, points[0].HoldMinutes));
        Assert.AreEqual((TradeDirection.Short, 545, 3), (points[1].Direction, points[1].EntryMinute, points[1].HoldMinutes));
        Assert.AreEqual(50, points.Count);
    }
}
