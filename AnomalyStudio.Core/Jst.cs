using System.Collections.Concurrent;

namespace AnomalyStudio.Core;

/// <summary>
/// 日本時間（UTC+9、夏時間なし）との変換。市場データは UTC で保存し、解析と表示は JST で行う。
/// 変換はこのクラスに集約する。
/// </summary>
public static class Jst
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(9);

    public static DateTime FromUtc(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Unspecified) + Offset;

    public static DateTime ToUtc(DateTime jst) => DateTime.SpecifyKind(jst - Offset, DateTimeKind.Utc);

    public static DateTime Now => FromUtc(DateTime.UtcNow);

    public static DateOnly Today => DateOnly.FromDateTime(Now);

    /// <summary>JST の日付の 0:00 を UTC で返す。</summary>
    public static DateTime StartOfDayUtc(DateOnly jstDate) => ToUtc(jstDate.ToDateTime(TimeOnly.MinValue));

    /// <summary>FX 市場の週の区切り（NY クローズ）に使う米国東部時間（夏時間あり）。</summary>
    private static readonly TimeZoneInfo NewYork = FindNewYork();

    private static TimeZoneInfo FindNewYork()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }

    /// <summary>
    /// FX 市場が開いている時刻（JST）か。週の取引時間は NY 時間の日曜 17:00 〜 金曜 17:00（NY クローズ）とし、
    /// JST では月曜 6:00（米国夏時間。冬は 7:00）〜 土曜 6:00（冬は 7:00）。銘柄によらず同じ扱いにする。
    /// </summary>
    public static bool IsFxMarketOpen(DateTime jst)
    {
        var ny = TimeZoneInfo.ConvertTimeFromUtc(ToUtc(jst), NewYork);
        return ny.DayOfWeek switch
        {
            DayOfWeek.Saturday => false,
            DayOfWeek.Sunday => ny.Hour >= 17,
            DayOfWeek.Friday => ny.Hour < 17,
            _ => true,
        };
    }

    /// <summary>日ごとの市場の開いている範囲（ランダム基準が試行 × 日ごとに引くので、時差の計算を繰り返さない）。</summary>
    private static readonly ConcurrentDictionary<int, (int StartMinute, int EndMinute)?> MarketMinutesByDay = new();

    /// <summary>
    /// JST の日付のうち FX 市場が開いている範囲（0:00 からの分。終了は含まない）。開いていない日（日曜）は null。
    /// 週の区切りは NY の 17:00 で、JST との差は整数時間なので、時間単位で調べれば足りる。
    /// </summary>
    public static (int StartMinute, int EndMinute)? FxMarketMinutes(DateOnly jstDate) =>
        MarketMinutesByDay.GetOrAdd(jstDate.DayNumber, _ =>
        {
            var start = jstDate.ToDateTime(TimeOnly.MinValue);
            var open = Enumerable.Range(0, 24).Where(h => IsFxMarketOpen(start.AddHours(h))).ToList();
            return open.Count == 0 ? null : (open[0] * 60, (open[^1] + 1) * 60);
        });
}
