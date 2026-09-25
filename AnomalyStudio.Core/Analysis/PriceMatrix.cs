namespace AnomalyStudio.Core.Analysis;

/// <summary>分析に使う最小限の価格（足の開始時点の BID と ASK）。</summary>
public readonly record struct OpenQuote(DateTime TimeUtc, double BidOpen, double AskOpen);

/// <summary>
/// 日（JST）× 時刻（0〜1439 分）の BID Open と Entry 時点 spread。欠損は NaN。
/// 行 0 は基準日−365 日、最終行は基準日当日（日跨ぎ Close 用）。
/// <para>
/// 配列は時刻ごとに日が連続する並び（時刻 × 日）で持つ。分析エンジンは同じ時刻の 365 日分を続けて読むので、
/// 日ごとに 1440 分を並べるより cache に乗りやすい（同じ計算で 2 倍以上速い）。
/// </para>
/// <para>
/// 長い期間の行列から <see cref="Window"/> で基準日ごとの窓を切り出せる（配列を共有し、複製しない）。
/// ウォークフォワード・バックテストは連続する基準日の分析にこれを使う。
/// </para>
/// </summary>
public sealed class PriceMatrix
{
    private readonly double[] _bid;
    private readonly double[] _spread;
    private readonly int[] _dayBarCounts;
    private readonly int _stride;
    private readonly int _offset;

    public PriceMatrix(DateOnly firstDay, int days)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(days);
        FirstDay = firstDay;
        Days = days;
        ValidDays = days;
        _stride = days;
        _offset = 0;
        _bid = new double[days * CandidateGrid.MinutesPerDay];
        _spread = new double[days * CandidateGrid.MinutesPerDay];
        _dayBarCounts = new int[days];
        Array.Fill(_bid, double.NaN);
        Array.Fill(_spread, double.NaN);
    }

    private PriceMatrix(PriceMatrix source, DateOnly firstDay, int offset, int days, int validDays)
    {
        _bid = source._bid;
        _spread = source._spread;
        _dayBarCounts = source._dayBarCounts;
        _stride = source._stride;
        _offset = offset;
        FirstDay = firstDay;
        Days = days;
        ValidDays = validDays;
    }

    public DateOnly FirstDay { get; }

    public int Days { get; }

    /// <summary>
    /// 価格を使う日数（先頭から）。これ以降の行は欠損として扱う。
    /// ウォークフォワードで基準日当日の価格を使わないときに <see cref="Days"/> − 1 になる（先読み防止）。
    /// </summary>
    public int ValidDays { get; }

    /// <summary>この行列（窓なら窓の中の使う日）にある足の本数。</summary>
    public int BarCount
    {
        get
        {
            var count = 0;
            for (var d = 0; d < ValidDays; d++)
            {
                count += _dayBarCounts[_offset + d];
            }

            return count;
        }
    }

    public double Bid(int day, int minute) => _bid[(minute * _stride) + _offset + day];

    public double Spread(int day, int minute) => _spread[(minute * _stride) + _offset + day];

    /// <summary>ある時刻の BID Open を日 0 から <see cref="Days"/> 日分（窓の最終日は使わない設定でも含む）。</summary>
    internal ReadOnlySpan<double> BidColumn(int minute) => new(_bid, (minute * _stride) + _offset, Days);

    /// <summary>ある時刻の Entry 時点 spread を日 0 から <see cref="Days"/> 日分。</summary>
    internal ReadOnlySpan<double> SpreadColumn(int minute) => new(_spread, (minute * _stride) + _offset, Days);

    public void Set(int day, int minute, double bidOpen, double spread)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(day);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(day, Days);
        var i = (minute * _stride) + _offset + day;
        _bid[i] = bidOpen;
        _spread[i] = spread >= 0 ? spread : double.NaN;
        _dayBarCounts[_offset + day]++;
    }

    /// <summary>基準日の分析用マトリクス（Entry 日 365 日分 + 基準日当日）を作る。</summary>
    public static PriceMatrix Build(DateOnly reportDate, int maxPeriod, IEnumerable<OpenQuote> quotes) =>
        BuildRange(reportDate.AddDays(-maxPeriod), maxPeriod + 1, quotes);

    /// <summary><paramref name="firstDay"/>（JST）から <paramref name="days"/> 日分の行列を作る。範囲外の足は捨てる。</summary>
    public static PriceMatrix BuildRange(DateOnly firstDay, int days, IEnumerable<OpenQuote> quotes)
    {
        var matrix = new PriceMatrix(firstDay, days);
        foreach (var q in quotes)
        {
            var jst = Jst.FromUtc(q.TimeUtc);
            var day = DateOnly.FromDateTime(jst).DayNumber - firstDay.DayNumber;
            if (day < 0 || day >= matrix.Days || double.IsNaN(q.BidOpen))
            {
                continue;
            }

            matrix.Set(day, (jst.Hour * 60) + jst.Minute, q.BidOpen, q.AskOpen - q.BidOpen);
        }

        return matrix;
    }

    /// <summary>
    /// この行列から基準日の分析用の窓（基準日−<paramref name="maxPeriod"/> 日 〜 基準日当日）を切り出す。配列は共有する。
    /// <paramref name="excludeReportDate"/> なら基準日当日の価格を使わない（<see cref="ValidDays"/> = <paramref name="maxPeriod"/>。
    /// 基準日の 0:00 より前の足だけで作った行列と同じ結果になる）。
    /// </summary>
    public PriceMatrix Window(DateOnly reportDate, int maxPeriod, bool excludeReportDate)
    {
        var firstDay = reportDate.AddDays(-maxPeriod);
        var offset = firstDay.DayNumber - FirstDay.DayNumber;
        var days = maxPeriod + 1;
        if (offset < 0 || offset + days > Days)
        {
            throw new ArgumentOutOfRangeException(nameof(reportDate), $"{reportDate:yyyy/MM/dd} の窓は行列（{FirstDay:yyyy/MM/dd} から {Days} 日）に収まりません。");
        }

        return new PriceMatrix(this, firstDay, _offset + offset, days, excludeReportDate ? maxPeriod : days);
    }

    /// <summary>分析に必要な UTC の範囲（基準日−maxPeriod 日 0:00 JST 〜 基準日翌日 0:00 JST）。</summary>
    public static (DateTime FromUtc, DateTime ToUtc) RequiredRange(DateOnly reportDate, int maxPeriod) =>
        (Jst.StartOfDayUtc(reportDate.AddDays(-maxPeriod)), Jst.StartOfDayUtc(reportDate.AddDays(1)));
}
