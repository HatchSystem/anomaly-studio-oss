using AnomalyStudio.Core.Analysis;

namespace AnomalyStudio.Core.Tests;

/// <summary>
/// 手計算で期待値を出せる人工的な価格。
/// BID は常に 100、ただし 09:05（545 分）だけ偶数日 101・奇数日 102 に跳ねる。spread は常に 0.1。
/// </summary>
internal static class SyntheticMarket
{
    public const int SpikeMinute = 545;
    public const double Base = 100;
    public const double Spread = 0.1;

    public static readonly DateOnly ReportDate = new(2026, 8, 16);

    public static double SpikeFor(int day) => day % 2 == 0 ? 1 : 2;

    public static PriceMatrix Build(Func<int, int, bool>? missing = null)
    {
        var matrix = new PriceMatrix(ReportDate.AddDays(-365), 366);
        for (var d = 0; d < matrix.Days; d++)
        {
            for (var m = 0; m < CandidateGrid.MinutesPerDay; m++)
            {
                if (missing?.Invoke(d, m) == true)
                {
                    continue;
                }

                var bid = m == SpikeMinute ? Base + SpikeFor(d) : Base;
                matrix.Set(d, m, bid, Spread);
            }
        }

        return matrix;
    }
}
