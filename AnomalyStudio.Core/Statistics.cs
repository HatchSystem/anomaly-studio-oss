namespace AnomalyStudio.Core;

/// <summary>分析とバックテストで共有する統計の計算。</summary>
public static class Statistics
{
    /// <summary>両側 95% に対応する標準正規分布の分位点。</summary>
    public const double Z95 = 1.959963984540054;

    /// <summary>
    /// 二項比率（勝率）の Wilson スコア区間の下限。点推定の勝率はサンプルが少ないほど偶然で高く出るので、
    /// 「その勝率が偶然でない範囲の下限」で候補を比べるために使う。n = 0 は NaN。
    /// </summary>
    public static double WilsonLowerBound(int wins, int n, double z = Z95)
    {
        if (n <= 0)
        {
            return double.NaN;
        }

        var p = (double)wins / n;
        var z2 = z * z;
        var center = p + (z2 / (2.0 * n));
        var margin = z * Math.Sqrt((p * (1 - p) / n) + (z2 / (4.0 * n * n)));
        return Math.Max(0, (center - margin) / (1 + (z2 / n)));
    }
}
