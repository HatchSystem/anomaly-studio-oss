using System.Collections.Concurrent;

namespace AnomalyStudio.Core.Analysis;

/// <summary>
/// 全候補（1440 Entry 時刻 × 保有時間 × Long/Short）を期間ごとに検証する分析エンジン（仕様は docs/design/anomaly-engine.md §2）。
/// <list type="bullet">
/// <item>価格は BID Open → BID Open。Long raw = Close − Entry、Short raw = Entry − Close</item>
/// <item>net = raw − 期間平均 spread（Entry・Close の両方がある有効 trade の Entry 時点 spread の平均）</item>
/// <item>勝率 = net &gt; 0、σ = raw の標本標準偏差（ddof=1、spread は固定控除なので net の σ と同じ）</item>
/// <item>利益効率σ = 合計 / (σ·√n)</item>
/// </list>
/// あわせて、勝率の Wilson 95% 信頼下限（win_rate_lcb_*）とその 30 / 90 / 365 日平均を計算する。
/// <para>
/// 計算の順序（足し合わせる順を含む）は結果をビット単位で変えないように固定している。速さは価格の並び（時刻 × 日）と
/// 並列の分け方で出す。順序を変えるときは計算方式の変更として扱う（<see cref="EngineParameters.LogicVersion"/>）。
/// </para>
/// </summary>
public static class AnomalyEngine
{
    /// <summary>並列に処理する候補（保有 × Entry 時刻）の塊の大きさ。28 × 1440 = 40,320 を 224 個に分ける。</summary>
    private const int BlockSize = 180;

    /// <param name="reuse">前回の結果の入れ物を使い回す（期間が同じときだけ。連続して分析するときの GC 負荷を減らす）。</param>
    public static CandidateTable Compute(PriceMatrix matrix, EngineParameters parameters, IProgress<double>? progress = null, CandidateTable? reuse = null)
    {
        var maxPeriod = parameters.MaxPeriod;
        if (matrix.Days < maxPeriod + 1)
        {
            throw new ArgumentException($"価格マトリクスは {maxPeriod + 1} 日分必要です（{matrix.Days} 日）。", nameof(matrix));
        }

        if (matrix.ValidDays < maxPeriod)
        {
            throw new ArgumentException($"価格マトリクスの使える日数が {maxPeriod} 日に足りません（{matrix.ValidDays} 日）。", nameof(matrix));
        }

        var table = reuse is not null && reuse.Periods.SequenceEqual(parameters.Periods) ? reuse : new CandidateTable(parameters.Periods);
        table.Reset();
        var validDays = matrix.ValidDays;
        var done = 0;
        const int total = CandidateGrid.HoldCount * CandidateGrid.MinutesPerDay;

        Parallel.ForEach(Partitioner.Create(0, total, BlockSize), range =>
        {
            var raw = new double[maxPeriod];
            for (var k = range.Item1; k < range.Item2; k++)
            {
                var hold = (k / CandidateGrid.MinutesPerDay) + CandidateGrid.HoldMin;
                var entry = k % CandidateGrid.MinutesPerDay;
                var closeAbs = entry + hold;
                var closeMinute = closeAbs % CandidateGrid.MinutesPerDay;
                var dayOffset = closeAbs / CandidateGrid.MinutesPerDay;

                var bidEntry = matrix.BidColumn(entry);
                var bidClose = matrix.BidColumn(closeMinute);
                for (var d = 0; d < maxPeriod; d++)
                {
                    raw[d] = bidClose[d + dayOffset] - bidEntry[d];
                }

                // 使わない日（基準日当日の価格を使わない窓）に Close がある取引は欠損
                for (var d = Math.Max(0, validDays - dayOffset); d < maxPeriod; d++)
                {
                    raw[d] = double.NaN;
                }

                var spreadEntry = matrix.SpreadColumn(entry);
                for (var p = 0; p < table.Periods.Count; p++)
                {
                    ComputePeriod(table.ByPeriod[p], raw, spreadEntry, maxPeriod - table.Periods[p], maxPeriod, entry, hold, parameters.FallbackSpread);
                }
            }

            progress?.Report(Interlocked.Add(ref done, range.Item2 - range.Item1) / (double)total);
        });

        Score(table, parameters);
        return table;
    }

    private static void ComputePeriod(
        PeriodMetrics metrics, double[] raw, ReadOnlySpan<double> spread, int fromDay, int toDay, int entry, int hold, double fallbackSpread)
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
            var sp = spread[d];
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

        foreach (var direction in (ReadOnlySpan<TradeDirection>)[TradeDirection.Long, TradeDirection.Short])
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

    /// <summary>スコア平均・品質除外・正規化スコア・ランキング。</summary>
    internal static void Score(CandidateTable table, EngineParameters parameters)
    {
        var scorePeriods = parameters.ScorePeriods.Select(table.Period).ToArray();

        MeanSkipNaN([.. scorePeriods.Select(m => m.WinRate)], table.WinRateAvg);
        MeanSkipNaN([.. scorePeriods.Select(m => m.Total)], table.TotalAvg);
        MeanSkipNaN([.. scorePeriods.Select(m => m.Sigma)], table.SigmaAvg);
        MeanSkipNaN([.. scorePeriods.Select(m => m.ProfitEff)], table.ProfitEffAvg);
        MeanSkipNaN([.. scorePeriods.Select(m => m.Spread)], table.SpreadAvg);
        MeanSkipNaN([.. scorePeriods.Select(m => m.Mean)], table.MaxProfitBase);
        MeanSkipNaN([.. scorePeriods.Select(m => m.WinRateLcb)], table.WinRateLcbAvg);

        // 日次休止帯などでサンプルが欠ける候補を、固定時刻ではなくサンプルカバレッジで除外する
        foreach (var m in scorePeriods)
        {
            var max = 0;
            foreach (var n in m.N)
            {
                max = Math.Max(max, n);
            }

            var threshold = (int)Math.Ceiling(max * parameters.MinSampleRatio);
            table.QualityThresholds[m.Days] = (max, threshold);
            for (var i = 0; i < CandidateGrid.Count; i++)
            {
                table.QualityExcluded[i] |= m.N[i] < threshold;
            }
        }

        // 3 つのスコアは互いに独立なので、正規化と順位付けを並行して行う
        Parallel.Invoke(
            () =>
            {
                Normalize(table.WinRateAvg, table.QualityExcluded, table.ScoreWinRate);
                RankDescending(table.ScoreWinRate, table.QualityExcluded, table.RankWinRate);
            },
            () =>
            {
                Normalize(table.ProfitEffAvg, table.QualityExcluded, table.ScoreProfitEff);
                RankDescending(table.ScoreProfitEff, table.QualityExcluded, table.RankProfitEff);
            },
            () =>
            {
                Normalize(table.MaxProfitBase, table.QualityExcluded, table.ScoreMaxProfit);
                RankDescending(table.ScoreMaxProfit, table.QualityExcluded, table.RankMaxProfit);
            });
    }

    /// <summary>候補ごとに、値のある期間（列の順）の平均。すべて NaN なら NaN。</summary>
    private static void MeanSkipNaN(double[][] columns, double[] output)
    {
        for (var i = 0; i < output.Length; i++)
        {
            double sum = 0;
            var count = 0;
            foreach (var column in columns)
            {
                var v = column[i];
                if (!double.IsNaN(v))
                {
                    sum += v;
                    count++;
                }
            }

            output[i] = count > 0 ? sum / count : double.NaN;
        }
    }

    /// <summary>母集団（品質除外でない・値あり）の最大値で割って 100 倍する。最大値が 0 や非有限なら全て NaN。</summary>
    internal static void Normalize(double[] values, bool[] excluded, double[] output)
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

    /// <summary>降順の順位（同値は最小順位 = pandas の method="min"）。母集団外は 0。</summary>
    internal static void RankDescending(double[] scores, bool[] excluded, int[] output)
    {
        Array.Clear(output);
        var count = 0;
        var indices = new int[scores.Length];
        for (var i = 0; i < scores.Length; i++)
        {
            if (!excluded[i] && !double.IsNaN(scores[i]))
            {
                indices[count++] = i;
            }
        }

        // 符号を反転した値を昇順に並べる（= 降順）。double の配列の並べ替えは委譲の比較より数倍速い。
        // 同値の並びは安定でなくてよい（同値には同じ最小順位を付けるので結果は変わらない）
        var keys = new double[count];
        for (var k = 0; k < count; k++)
        {
            keys[k] = -scores[indices[k]];
        }

        Array.Sort(keys, indices, 0, count);
        for (var r = 0; r < count; r++)
        {
            var i = indices[r];
            output[i] = r > 0 && scores[indices[r - 1]] == scores[i] ? output[indices[r - 1]] : r + 1;
        }
    }
}
