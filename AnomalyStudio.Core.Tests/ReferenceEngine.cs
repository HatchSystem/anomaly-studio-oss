using AnomalyStudio.Core.Analysis;

namespace AnomalyStudio.Core.Tests;

/// <summary>
/// 高速化前の分析エンジン（日 × 1440 分の配列、保有時間ごとの並列、LINQ の順位付け）をそのまま残した参照実装。
/// 高速化したエンジンが同じ入力から同じ値（ビット単位）を出すことを確かめるためだけに使う。計算仕様を変えるときは
/// こちらも同じ変更をする（<see cref="EngineRegressionTests"/>）。
/// </summary>
internal static class ReferenceEngine
{
    public sealed class Result(IReadOnlyList<int> periods)
    {
        public IReadOnlyList<int> Periods { get; } = periods;
        public PeriodMetrics[] ByPeriod { get; } = [.. periods.Select(p => new PeriodMetrics(p))];
        public double[] WinRateAvg { get; } = new double[CandidateGrid.Count];
        public double[] TotalAvg { get; } = new double[CandidateGrid.Count];
        public double[] SigmaAvg { get; } = new double[CandidateGrid.Count];
        public double[] ProfitEffAvg { get; } = new double[CandidateGrid.Count];
        public double[] SpreadAvg { get; } = new double[CandidateGrid.Count];
        public double[] MaxProfitBase { get; } = new double[CandidateGrid.Count];
        public double[] WinRateLcbAvg { get; } = new double[CandidateGrid.Count];
        public bool[] QualityExcluded { get; } = new bool[CandidateGrid.Count];
        public double[] ScoreWinRate { get; } = new double[CandidateGrid.Count];
        public double[] ScoreProfitEff { get; } = new double[CandidateGrid.Count];
        public double[] ScoreMaxProfit { get; } = new double[CandidateGrid.Count];
        public int[] RankWinRate { get; } = new int[CandidateGrid.Count];
        public int[] RankProfitEff { get; } = new int[CandidateGrid.Count];
        public int[] RankMaxProfit { get; } = new int[CandidateGrid.Count];
        public Dictionary<int, (int Max, int Threshold)> QualityThresholds { get; } = [];

        public PeriodMetrics Period(int days) => ByPeriod[Periods.ToList().IndexOf(days)];
    }

    /// <summary>日 × 1440 分の配列（行 0 が最初の日）。<paramref name="validDays"/> 以降の日は欠損。</summary>
    public static Result Compute(PriceMatrix matrix, EngineParameters parameters)
    {
        var days = matrix.Days;
        var bid = new double[days * CandidateGrid.MinutesPerDay];
        var spread = new double[days * CandidateGrid.MinutesPerDay];
        for (var d = 0; d < days; d++)
        {
            for (var m = 0; m < CandidateGrid.MinutesPerDay; m++)
            {
                var use = d < matrix.ValidDays;
                bid[(d * CandidateGrid.MinutesPerDay) + m] = use ? matrix.Bid(d, m) : double.NaN;
                spread[(d * CandidateGrid.MinutesPerDay) + m] = use ? matrix.Spread(d, m) : double.NaN;
            }
        }

        return Compute(bid, spread, days, parameters);
    }

    private static Result Compute(double[] bid, double[] spread, int days, EngineParameters parameters)
    {
        var maxPeriod = parameters.MaxPeriod;
        if (days < maxPeriod + 1)
        {
            throw new ArgumentException("日数不足");
        }

        var table = new Result(parameters.Periods);
        Parallel.For(CandidateGrid.HoldMin, CandidateGrid.HoldMax + 1, hold =>
        {
            var raw = new double[maxPeriod];
            for (var entry = 0; entry < CandidateGrid.MinutesPerDay; entry++)
            {
                var closeAbs = entry + hold;
                var closeMinute = closeAbs % CandidateGrid.MinutesPerDay;
                var dayOffset = closeAbs / CandidateGrid.MinutesPerDay;

                for (var d = 0; d < maxPeriod; d++)
                {
                    raw[d] = bid[((d + dayOffset) * CandidateGrid.MinutesPerDay) + closeMinute]
                             - bid[(d * CandidateGrid.MinutesPerDay) + entry];
                }

                for (var p = 0; p < table.Periods.Count; p++)
                {
                    ComputePeriod(table.ByPeriod[p], raw, spread, maxPeriod - table.Periods[p], maxPeriod, entry, hold, parameters.FallbackSpread);
                }
            }
        });

        Score(table, parameters);
        return table;
    }

    private static void ComputePeriod(
        PeriodMetrics metrics, double[] raw, double[] spread, int fromDay, int toDay, int entry, int hold, double fallbackSpread)
    {
        int n = 0, spreadN = 0;
        double rawSum = 0, spreadSum = 0;
        for (var d = fromDay; d < toDay; d++)
        {
            if (!double.IsFinite(raw[d]))
            {
                continue;
            }

            n++;
            rawSum += raw[d];
            var sp = spread[(d * CandidateGrid.MinutesPerDay) + entry];
            if (double.IsFinite(sp))
            {
                spreadN++;
                spreadSum += sp;
            }
        }

        var rawMean = n > 0 ? rawSum / n : double.NaN;
        var spreadMean = spreadN > 0 ? spreadSum / spreadN : fallbackSpread;

        double ssq = 0;
        int winsLong = 0, winsShort = 0;
        for (var d = fromDay; d < toDay; d++)
        {
            if (!double.IsFinite(raw[d]))
            {
                continue;
            }

            var c = raw[d] - rawMean;
            ssq += c * c;
            if (raw[d] > spreadMean)
            {
                winsLong++;
            }

            if (-raw[d] > spreadMean)
            {
                winsShort++;
            }
        }

        var sigma = n >= 2 ? Math.Sqrt(ssq / (n - 1)) : double.NaN;
        var sigmaValid = n >= 2 && double.IsFinite(sigma) && sigma > 0;

        foreach (var direction in new[] { TradeDirection.Long, TradeDirection.Short })
        {
            var sign = direction == TradeDirection.Long ? 1.0 : -1.0;
            var i = CandidateGrid.Index(hold, direction, entry);
            var total = (sign * rawSum) - (n * spreadMean);

            metrics.N[i] = n;
            metrics.Wins[i] = direction == TradeDirection.Long ? winsLong : winsShort;
            metrics.WinRate[i] = n > 0 ? (double)metrics.Wins[i] / n : double.NaN;
            metrics.WinRateLcb[i] = Statistics.WilsonLowerBound(metrics.Wins[i], n);
            metrics.Total[i] = total;
            metrics.Mean[i] = n > 0 ? (sign * rawMean) - spreadMean : double.NaN;
            metrics.Sigma[i] = sigma;
            metrics.ProfitEff[i] = sigmaValid ? total / (sigma * Math.Sqrt(n)) : double.NaN;
            metrics.Spread[i] = spreadMean;
        }
    }

    private static void Score(Result table, EngineParameters parameters)
    {
        var scorePeriods = parameters.ScorePeriods.Select(table.Period).ToArray();

        for (var i = 0; i < CandidateGrid.Count; i++)
        {
            table.WinRateAvg[i] = MeanSkipNaN(scorePeriods, m => m.WinRate[i]);
            table.TotalAvg[i] = MeanSkipNaN(scorePeriods, m => m.Total[i]);
            table.SigmaAvg[i] = MeanSkipNaN(scorePeriods, m => m.Sigma[i]);
            table.ProfitEffAvg[i] = MeanSkipNaN(scorePeriods, m => m.ProfitEff[i]);
            table.SpreadAvg[i] = MeanSkipNaN(scorePeriods, m => m.Spread[i]);
            table.MaxProfitBase[i] = MeanSkipNaN(scorePeriods, m => m.Mean[i]);
            table.WinRateLcbAvg[i] = MeanSkipNaN(scorePeriods, m => m.WinRateLcb[i]);
        }

        foreach (var m in scorePeriods)
        {
            var max = m.N.Max();
            var threshold = (int)Math.Ceiling(max * parameters.MinSampleRatio);
            table.QualityThresholds[m.Days] = (max, threshold);
            for (var i = 0; i < CandidateGrid.Count; i++)
            {
                table.QualityExcluded[i] |= m.N[i] < threshold;
            }
        }

        Normalize(table.WinRateAvg, table.QualityExcluded, table.ScoreWinRate);
        Normalize(table.ProfitEffAvg, table.QualityExcluded, table.ScoreProfitEff);
        Normalize(table.MaxProfitBase, table.QualityExcluded, table.ScoreMaxProfit);

        RankDescending(table.ScoreWinRate, table.QualityExcluded, table.RankWinRate);
        RankDescending(table.ScoreProfitEff, table.QualityExcluded, table.RankProfitEff);
        RankDescending(table.ScoreMaxProfit, table.QualityExcluded, table.RankMaxProfit);
    }

    private static double MeanSkipNaN(PeriodMetrics[] periods, Func<PeriodMetrics, double> value)
    {
        double sum = 0;
        var count = 0;
        foreach (var p in periods)
        {
            var v = value(p);
            if (!double.IsNaN(v))
            {
                sum += v;
                count++;
            }
        }

        return count > 0 ? sum / count : double.NaN;
    }

    private static void Normalize(double[] values, bool[] excluded, double[] output)
    {
        var max = double.NegativeInfinity;
        var any = false;
        for (var i = 0; i < values.Length; i++)
        {
            if (!excluded[i] && !double.IsNaN(values[i]))
            {
                max = Math.Max(max, values[i]);
                any = true;
            }
        }

        var valid = any && double.IsFinite(max) && max != 0;
        for (var i = 0; i < values.Length; i++)
        {
            output[i] = valid ? values[i] / max * 100.0 : double.NaN;
        }
    }

    private static void RankDescending(double[] scores, bool[] excluded, int[] output)
    {
        Array.Clear(output);
        var indices = Enumerable.Range(0, scores.Length)
            .Where(i => !excluded[i] && !double.IsNaN(scores[i]))
            .OrderByDescending(i => scores[i])
            .ToArray();

        for (var r = 0; r < indices.Length; r++)
        {
            var i = indices[r];
            output[i] = r > 0 && scores[indices[r - 1]] == scores[i] ? output[indices[r - 1]] : r + 1;
        }
    }
}
