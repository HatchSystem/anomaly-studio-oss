namespace AnomalyStudio.Core.Backtesting;

/// <summary>
/// 確定した取引の損益列（取引順）から求める安定性の指標。値幅は呼び出し側が渡した単位（表示単位）。
/// 勝率と合計だけでは「たまたま勝った」かを判断できないので、平均損益の t 値と信頼区間、プロフィットファクター、
/// 最大ドローダウン、最大連敗数を添える。
/// </summary>
/// <param name="Trades">確定した取引数。</param>
/// <param name="Wins">勝ち数（net &gt; 0）。</param>
/// <param name="Total">合計損益。</param>
/// <param name="Mean">1 取引平均。取引がなければ NaN。</param>
/// <param name="StdDev">損益の標本標準偏差（ddof=1）。取引が 2 件未満なら NaN。</param>
/// <param name="TStat">平均損益 ÷ 標準誤差。0 から離れるほど偶然でない（±2 が目安）。</param>
/// <param name="MeanCiHalfWidth">平均損益の 95% 信頼区間の半幅（正規近似）。</param>
/// <param name="ProfitFactor">総利益 ÷ 総損失。損失がなければ正の無限大、利益も損失もなければ NaN。</param>
/// <param name="MaxDrawdown">累積損益の最大の落ち込み（0 以上の値。大きいほど悪い）。</param>
/// <param name="MaxConsecutiveLosses">最大連敗数（net ≤ 0 の連続）。</param>
public sealed record BacktestStatistics(
    int Trades,
    int Wins,
    double Total,
    double Mean,
    double StdDev,
    double TStat,
    double MeanCiHalfWidth,
    double ProfitFactor,
    double MaxDrawdown,
    int MaxConsecutiveLosses)
{
    public static BacktestStatistics Empty { get; } = new(0, 0, 0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, 0, 0);

    /// <param name="netsInTradeOrder">確定した取引の損益（Entry 時刻順）。</param>
    public static BacktestStatistics Compute(IEnumerable<double> netsInTradeOrder)
    {
        var nets = netsInTradeOrder as IReadOnlyList<double> ?? [.. netsInTradeOrder];
        var n = nets.Count;
        if (n == 0)
        {
            return Empty;
        }

        double total = 0, grossProfit = 0, grossLoss = 0, cumulative = 0, peak = 0, maxDrawdown = 0;
        int wins = 0, losingStreak = 0, maxLosingStreak = 0;
        foreach (var net in nets)
        {
            total += net;
            if (net > 0)
            {
                wins++;
                grossProfit += net;
                losingStreak = 0;
            }
            else
            {
                grossLoss -= net;
                losingStreak++;
                maxLosingStreak = Math.Max(maxLosingStreak, losingStreak);
            }

            cumulative += net;
            peak = Math.Max(peak, cumulative);
            maxDrawdown = Math.Max(maxDrawdown, peak - cumulative);
        }

        var mean = total / n;
        double stdDev = double.NaN, tStat = double.NaN, ciHalfWidth = double.NaN;
        if (n >= 2)
        {
            double ssq = 0;
            foreach (var net in nets)
            {
                var c = net - mean;
                ssq += c * c;
            }

            stdDev = Math.Sqrt(ssq / (n - 1));
            var standardError = stdDev / Math.Sqrt(n);
            tStat = standardError > 0 ? mean / standardError : (mean == 0 ? 0 : double.NaN);
            ciHalfWidth = Statistics.Z95 * standardError;
        }

        var profitFactor = grossLoss > 0 ? grossProfit / grossLoss : (grossProfit > 0 ? double.PositiveInfinity : double.NaN);
        return new BacktestStatistics(n, wins, total, mean, stdDev, tStat, ciHalfWidth, profitFactor, maxDrawdown, maxLosingStreak);
    }
}
