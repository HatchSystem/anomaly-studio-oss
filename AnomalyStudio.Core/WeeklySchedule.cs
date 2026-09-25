namespace AnomalyStudio.Core;

/// <summary>週次スケジュールの計算。</summary>
public static class WeeklySchedule
{
    /// <summary><paramref name="now"/> 以前で直近の「曜日 + 時刻」。</summary>
    public static DateTime LastOccurrence(DateTime now, DayOfWeek day, TimeOnly time)
    {
        var candidate = now.Date.AddDays(-(((int)now.DayOfWeek - (int)day + 7) % 7)) + time.ToTimeSpan();
        return candidate > now ? candidate.AddDays(-7) : candidate;
    }
}
