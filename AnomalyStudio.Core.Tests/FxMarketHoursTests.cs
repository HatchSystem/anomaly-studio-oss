using AnomalyStudio.Core.Backtesting;

namespace AnomalyStudio.Core.Tests;

[TestClass]
public sealed class FxMarketHoursTests
{
    [TestMethod]
    [DataRow("2026-09-25 23:00", true)] // 金曜の夜（NY 時間 金曜 10:00）
    [DataRow("2026-09-26 05:59", true)] // 土曜早朝は米国夏時間なら 6:00 まで
    [DataRow("2026-09-26 06:00", false)] // NY クローズ（金曜 17:00 EDT）
    [DataRow("2026-09-26 12:00", false)] // 土曜の日中
    [DataRow("2026-09-27 12:00", false)] // 日曜
    [DataRow("2026-09-28 05:59", false)] // 月曜の開場前
    [DataRow("2026-09-28 06:00", true)] // 開場（NY 日曜 17:00 EDT）
    [DataRow("2026-01-10 06:59", true)] // 冬（EST）は土曜 7:00 まで
    [DataRow("2026-01-10 07:00", false)]
    [DataRow("2026-01-12 06:59", false)] // 冬の月曜は 7:00 から
    [DataRow("2026-01-12 07:00", true)]
    public void IsFxMarketOpen_FollowsTheNewYorkClose(string jst, bool expected) =>
        Assert.AreEqual(expected, Jst.IsFxMarketOpen(DateTime.Parse(jst)));

    [TestMethod]
    public void FxMarketMinutes_ReturnsTheOpenRangeOfTheJstDay()
    {
        Assert.AreEqual((0, 360), Jst.FxMarketMinutes(new DateOnly(2026, 9, 26))); // 土曜（夏）
        Assert.IsNull(Jst.FxMarketMinutes(new DateOnly(2026, 9, 27))); // 日曜
        Assert.AreEqual((360, 1440), Jst.FxMarketMinutes(new DateOnly(2026, 9, 28))); // 月曜（夏）
        Assert.AreEqual((0, 1440), Jst.FxMarketMinutes(new DateOnly(2026, 9, 29))); // 火曜
        Assert.AreEqual((0, 420), Jst.FxMarketMinutes(new DateOnly(2026, 1, 10))); // 土曜（冬）
    }

    [TestMethod]
    public void RandomBaseline_SamplesOnlyWhileTheMarketIsOpen()
    {
        // 土曜（夏）は 0:00〜5:59。Close の 6:00 ちょうどは足がないので含めない
        var rng = new Random(1);
        for (var trial = 0; trial < 50; trial++)
        {
            var points = RandomBaseline.SamplePoints(rng, new PointFilter(0, 24, 10), 10, (0, 360));
            Assert.IsTrue(points.Count > 0);
            Assert.IsTrue(points.All(p => p.EntryMinute + p.HoldMinutes < 360), "土曜の NY クローズより前に Close する");
        }

        // 取引時間帯（8〜19 時）と重ならない土曜は選ばない
        Assert.AreEqual(0, RandomBaseline.SamplePoints(new Random(2), new PointFilter(8, 19, 10), 10, (0, 360)).Count);
    }
}
