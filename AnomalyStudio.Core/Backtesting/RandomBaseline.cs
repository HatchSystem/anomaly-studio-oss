using AnomalyStudio.Core.Analysis;

namespace AnomalyStudio.Core.Backtesting;

/// <summary>ランダム基準の結果。<see cref="Totals"/> は試行ごとの合計損益（呼び出し側が渡した単位）。</summary>
public sealed record BaselineResult(IReadOnlyList<double> Totals, double ActualTotal)
{
    public int Trials => Totals.Count;

    public double MeanTotal => Totals.Count == 0 ? double.NaN : Totals.Average();

    /// <summary>実績の合計が上回った試行の割合（1 に近いほど、無作為に選ぶより良い）。</summary>
    public double BeatRatio => Totals.Count == 0 ? double.NaN : (double)Totals.Count(t => t < ActualTotal) / Totals.Count;
}

/// <summary>
/// ランダム基準: 実績と同じ取引日・同じ件数・同じ取引時間帯・同じ保有時間の範囲で、ポイントを無作為に選んで評価する。
/// 172,800 候補から上位を選ぶと偶然でも良く見えるので、「無作為に選んだ場合の分布」と比べて順位付けの価値を確かめる。
/// 乱数の種は固定なので、同じ条件なら同じ結果になる。
/// </summary>
public static class RandomBaseline
{
    public const int DefaultTrials = 200;
    public const int DefaultSeed = 20260925;

    /// <summary>無作為ポイントの保有時間（既定モードのポイント抽出と同じ 3〜15 分）。保有時間を限るモード（勝率重視BO）は、その保有時間から選ぶ。</summary>
    public const int HoldMin = 3;
    public const int HoldMax = 15;

    /// <param name="days">取引日と、その日の確定した取引数（同じ件数を無作為に選ぶ）。</param>
    /// <param name="filter">取引時間帯（ポイント数は使わない。件数は <paramref name="days"/> で決まる）。</param>
    /// <param name="symbols">ポイントごとに無作為に選ぶ銘柄（1 銘柄ならその銘柄だけ）。</param>
    /// <param name="net">銘柄・取引日・ポイントの確定損益（表示単位）。未確定や足がなければ null。</param>
    /// <param name="actualTotal">実績の合計損益（同じ単位）。</param>
    /// <param name="holds">無作為に選ぶ保有時間（分）。null は <see cref="HoldMin"/>〜<see cref="HoldMax"/>。</param>
    public static BaselineResult Run(
        IReadOnlyList<(DateOnly Day, int Count)> days,
        PointFilter filter,
        IReadOnlyList<string> symbols,
        Func<string, DateOnly, EntryPoint, double?> net,
        double actualTotal,
        int trials = DefaultTrials,
        int seed = DefaultSeed,
        IReadOnlyList<int>? holds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(trials);
        ArgumentOutOfRangeException.ThrowIfZero(symbols.Count);

        var rng = new Random(seed);
        var totals = new double[trials];
        for (var t = 0; t < trials; t++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double total = 0;
            foreach (var (day, count) in days)
            {
                // 市場が開いている範囲（土曜は早朝だけ、月曜は開場後）で選ぶ。開いていない日は選ばない
                var market = Jst.FxMarketMinutes(day);
                foreach (var point in market is { } m ? SamplePoints(rng, filter, count, m, holds) : [])
                {
                    var symbol = symbols.Count == 1 ? symbols[0] : symbols[rng.Next(symbols.Count)];
                    total += net(symbol, day, point) ?? 0;
                }
            }

            totals[t] = total;
        }

        return new BaselineResult(totals, actualTotal);
    }

    /// <summary>
    /// 取引時間帯に収まり、互いに時間帯が重ならないポイントを最大 <paramref name="count"/> 件、無作為に選ぶ。
    /// 時間帯が狭くて入りきらなければ、入った分だけ返す。
    /// </summary>
    public static IReadOnlyList<EntryPoint> SamplePoints(Random rng, PointFilter filter, int count) =>
        SamplePoints(rng, filter, count, (0, CandidateGrid.MinutesPerDay));

    /// <summary>
    /// 取引時間帯と、その日に市場が開いている範囲（0:00 からの分。終了は含まない）の両方に収める。
    /// <paramref name="holds"/> を渡すと、保有時間はその中から選ぶ（null は <see cref="HoldMin"/>〜<see cref="HoldMax"/>）。
    /// </summary>
    public static IReadOnlyList<EntryPoint> SamplePoints(
        Random rng, PointFilter filter, int count, (int StartMinute, int EndMinute) market, IReadOnlyList<int>? holds = null)
    {
        // 市場の終了時刻ちょうど（土曜 6:00 など）には足がないので、Close はその 1 分前まで。日の終わり（24:00）は翌日の足で閉じる
        var marketEnd = market.EndMinute < CandidateGrid.MinutesPerDay ? market.EndMinute - 1 : market.EndMinute;
        var windowStart = Math.Max(filter.StartHour * 60, market.StartMinute);
        var windowEnd = Math.Min(Math.Min(filter.EndHour * 60, CandidateGrid.MinutesPerDay), marketEnd);
        var maxHold = Math.Min(HoldMax, windowEnd - windowStart);
        var selected = new List<EntryPoint>(Math.Max(0, count));
        var usable = holds?.Where(h => h >= 1 && h <= windowEnd - windowStart).ToArray();
        if (count <= 0 || (usable is null ? maxHold < HoldMin : usable.Length == 0))
        {
            return selected;
        }

        var occupied = new bool[CandidateGrid.MinutesPerDay];
        var attempts = 0;
        while (selected.Count < count && attempts++ < count * 200)
        {
            var hold = usable is null ? rng.Next(HoldMin, maxHold + 1) : usable[rng.Next(usable.Length)];
            var entry = rng.Next(windowStart, windowEnd - hold + 1);
            if (!PointSelector.TryOccupy(occupied, entry, hold))
            {
                continue;
            }

            var direction = rng.Next(2) == 0 ? TradeDirection.Long : TradeDirection.Short;
            selected.Add(new EntryPoint(selected.Count + 1, direction, entry, hold, double.NaN));
        }

        return selected;
    }
}
