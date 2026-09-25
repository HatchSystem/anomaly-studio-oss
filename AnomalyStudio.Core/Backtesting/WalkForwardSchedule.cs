namespace AnomalyStudio.Core.Backtesting;

/// <summary>バックテストの評価サイクル。ウォークフォワードでは、使う分析の時点と集計の単位を兼ねる。</summary>
public enum BacktestCycle
{
    Daily,
    Weekly,
    Monthly,
}

/// <summary>
/// ウォークフォワード・バックテストで、取引日ごとにどの基準日の分析結果を使うかを決める。
/// 基準日の分析は「基準日−365 日 〜 基準日の前日」を評価する（その時点で確定している情報だけ）。
/// </summary>
public static class WalkForwardSchedule
{
    /// <summary>
    /// 取引日に使う分析の基準日。
    /// 毎日 = 当日（前日までのデータ）、毎週 = 週（月〜日）の直前の日曜（土曜までのデータ）、毎月 = 当月 1 日（前月末までのデータ）。
    /// </summary>
    public static DateOnly ReportDateFor(DateOnly tradeDate, BacktestCycle cycle) => cycle switch
    {
        BacktestCycle.Daily => tradeDate,
        BacktestCycle.Weekly => WeekStart(tradeDate).AddDays(-1),
        _ => new DateOnly(tradeDate.Year, tradeDate.Month, 1),
    };

    /// <summary>評価サイクルの 1 期間の開始日（集計の単位）。週は月曜始まり。</summary>
    public static DateOnly PeriodStart(DateOnly day, BacktestCycle cycle) => cycle switch
    {
        BacktestCycle.Daily => day,
        BacktestCycle.Weekly => WeekStart(day),
        _ => new DateOnly(day.Year, day.Month, 1),
    };

    /// <summary>検証期間の各取引日に必要な基準日（重複なし、昇順）。</summary>
    public static IReadOnlyList<DateOnly> ReportDates(DateOnly from, DateOnly to, BacktestCycle cycle)
    {
        var dates = new SortedSet<DateOnly>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            dates.Add(ReportDateFor(day, cycle));
        }

        return [.. dates];
    }

    private static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
}
