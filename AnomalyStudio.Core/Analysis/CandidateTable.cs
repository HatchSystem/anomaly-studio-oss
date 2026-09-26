namespace AnomalyStudio.Core.Analysis;

/// <summary>1 期間分の候補統計（列指向）。値幅は価格単位。</summary>
public sealed class PeriodMetrics(int days)
{
    public int Days { get; } = days;
    public int[] N { get; } = new int[CandidateGrid.Count];
    public int[] Wins { get; } = new int[CandidateGrid.Count];
    public double[] WinRate { get; } = new double[CandidateGrid.Count];
    public double[] Total { get; } = new double[CandidateGrid.Count];
    public double[] Mean { get; } = new double[CandidateGrid.Count];
    public double[] Sigma { get; } = new double[CandidateGrid.Count];
    public double[] ProfitEff { get; } = new double[CandidateGrid.Count];
    public double[] Spread { get; } = new double[CandidateGrid.Count];

    /// <summary>勝率の Wilson 95% 信頼区間の下限（サンプルが少ない候補の偶然の高勝率を割り引いた値）。</summary>
    public double[] WinRateLcb { get; } = new double[CandidateGrid.Count];
}

/// <summary>
/// 172,800 候補の統計・スコア・ランキング（列指向）。NaN は「値なし」。ランキングの 0 は「対象外」。
/// 約 75 MB あるので、連続して分析するとき（ウォークフォワード）は <see cref="AnomalyEngine.Compute"/> に渡して使い回す。
/// </summary>
public sealed class CandidateTable
{
    public CandidateTable(IReadOnlyList<int> periods)
    {
        Periods = [.. periods];
        ByPeriod = [.. periods.Select(p => new PeriodMetrics(p))];
    }

    public IReadOnlyList<int> Periods { get; }
    public PeriodMetrics[] ByPeriod { get; }

    /// <summary>期間（日数）の統計。ない期間は例外。</summary>
    public PeriodMetrics Period(int days) =>
        PeriodOrNull(days) ?? throw new ArgumentOutOfRangeException(nameof(days), $"{days} 日の期間はありません。");

    /// <summary>期間（日数）の統計。ない期間は null。</summary>
    public PeriodMetrics? PeriodOrNull(int days)
    {
        for (var i = 0; i < Periods.Count; i++)
        {
            if (Periods[i] == days)
            {
                return ByPeriod[i];
            }
        }

        return null;
    }

    public double[] WinRateAvg { get; } = new double[CandidateGrid.Count];
    public double[] TotalAvg { get; } = new double[CandidateGrid.Count];
    public double[] SigmaAvg { get; } = new double[CandidateGrid.Count];
    public double[] ProfitEffAvg { get; } = new double[CandidateGrid.Count];
    public double[] SpreadAvg { get; } = new double[CandidateGrid.Count];
    public double[] MaxProfitBase { get; } = new double[CandidateGrid.Count];

    /// <summary>勝率の信頼下限の 30 / 90 / 365 日平均（銘柄をまたいで比べられる）。</summary>
    public double[] WinRateLcbAvg { get; } = new double[CandidateGrid.Count];
    public bool[] QualityExcluded { get; } = new bool[CandidateGrid.Count];

    public double[] ScoreWinRate { get; } = new double[CandidateGrid.Count];
    public double[] ScoreProfitEff { get; } = new double[CandidateGrid.Count];
    public double[] ScoreMaxProfit { get; } = new double[CandidateGrid.Count];

    public int[] RankWinRate { get; } = new int[CandidateGrid.Count];
    public int[] RankProfitEff { get; } = new int[CandidateGrid.Count];
    public int[] RankMaxProfit { get; } = new int[CandidateGrid.Count];

    /// <summary>品質基準（期間 → 最大サンプル数, 採用最低サンプル数）。</summary>
    public Dictionary<int, (int Max, int Threshold)> QualityThresholds { get; } = [];

    /// <summary>
    /// 使い回す前に、計算で上書きされない（足し込まれる）項目を空にする。
    /// 統計・スコア・順位の列は分析ですべての候補が書き換わるので消さない。
    /// </summary>
    internal void Reset()
    {
        Array.Clear(QualityExcluded);
        QualityThresholds.Clear();
    }

    /// <summary>
    /// candidate_stats の列名で、ポイント抽出の並び順に使える列を引く（既定モードを SQL なしで選ぶときに使う）。
    /// 対応しない列名は null。
    /// </summary>
    public double[]? Column(string name) => name switch
    {
        "score_win_rate" => ScoreWinRate,
        "score_profit_eff" => ScoreProfitEff,
        "score_max_profit" => ScoreMaxProfit,
        "win_rate_avg" => WinRateAvg,
        "profit_eff_avg" => ProfitEffAvg,
        "win_rate_lcb_avg" => WinRateLcbAvg,
        "max_profit_base" => MaxProfitBase,
        _ => null,
    };
}
