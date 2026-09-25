namespace AnomalyStudio.Core.Tests;

[TestClass]
public sealed class WeeklyScheduleTests
{
    [TestMethod]
    [DataRow("2026-09-24 05:00", "2026-09-20 07:00")] // 木曜 → 直前の日曜 07:00
    [DataRow("2026-09-20 06:59", "2026-09-13 07:00")] // 日曜の予定時刻前 → 前週
    [DataRow("2026-09-20 07:00", "2026-09-20 07:00")] // ちょうど予定時刻
    public void LastOccurrence_IsMostRecentScheduledTime(string now, string expected)
    {
        var actual = WeeklySchedule.LastOccurrence(DateTime.Parse(now), DayOfWeek.Sunday, new TimeOnly(7, 0));
        Assert.AreEqual(DateTime.Parse(expected), actual);
    }
}
