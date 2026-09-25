using System.Globalization;

namespace AnomalyStudio.Helpers;

public static class Format
{
    private const string Weekdays = "日月火水木金土";

    public static char Weekday(DateTime d) => Weekday(d.DayOfWeek);

    public static char Weekday(DayOfWeek d) => Weekdays[(int)d];

    public static string LongDate(DateTime d) => $"{d.Year}年{d.Month}月{d.Day}日（{Weekday(d)}）";

    public static string DayLabel(DateTime d) => $"{d.Month}月{d.Day}日（{Weekday(d)}）";

    public static string Clock(DateTime d) => d.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public static string HourMinute(DateTime d) => d.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>秒以下を切り捨てた時刻（エントリーは分単位なので、分が変わったかの判定に使う）。</summary>
    public static DateTime StartOfMinute(DateTime t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerMinute), t.Kind);

    public static string MonthDay(DateTime d) => d.ToString("MM/dd", CultureInfo.InvariantCulture);

    public static string Slash(DateTime d) => d.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);

    /// <summary>小数 1 桁の符号付き表記。丸めて 0 になる値は "0.0"（"-0.0" を出さない）。</summary>
    public static string Signed(double v)
    {
        var rounded = Math.Round(v, 1, MidpointRounding.AwayFromZero);
        if (rounded == 0)
        {
            return "0.0";
        }

        return (rounded > 0 ? "+" : string.Empty) + rounded.ToString("0.0", CultureInfo.InvariantCulture);
    }

    public static string Percent(double fraction) => (fraction * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>1 時間以上は h:mm:ss、未満は mm:ss。</summary>
    public static string Countdown(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            t = TimeSpan.Zero;
        }

        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    public static string LongCountdown(TimeSpan t) => $"{(int)Math.Max(0, t.TotalHours)}:{Math.Max(0, t.Minutes):00}:{Math.Max(0, t.Seconds):00}";

    public static string StarsOn(int n) => new('★', Math.Clamp(n, 0, 5));

    public static string StarsOff(int n) => new('★', 5 - Math.Clamp(n, 0, 5));

    /// <summary>米国夏時間（3 月第 2 日曜〜11 月第 1 日曜）かどうか。</summary>
    public static bool IsUsDaylightSaving(DateTime now)
    {
        static DateTime NthSunday(int year, int month, int n)
        {
            var first = new DateTime(year, month, 1);
            return first.AddDays((7 - (int)first.DayOfWeek) % 7 + (n - 1) * 7);
        }

        return now >= NthSunday(now.Year, 3, 2) && now < NthSunday(now.Year, 11, 1);
    }

    public static string DayPrefix(DateTime target, DateTime now) =>
        (target.Date - now.Date).Days switch
        {
            -1 => "昨日",
            1 => "明日",
            _ => string.Empty,
        };
}
