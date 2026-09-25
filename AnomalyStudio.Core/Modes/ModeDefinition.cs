using System.Text.Json.Serialization;

namespace AnomalyStudio.Core.Modes;

/// <summary>
/// 複合ポイント（すべての銘柄をまとめた順位）で、銘柄をまたいで比べる指標。正規化スコアは銘柄ごとの最高値が 100 なので使わず、もとの値で比べる。
/// 単位のある指標は銘柄をまたいで比べられないので対象にしない。
/// </summary>
public enum CompositeMetric
{
    /// <summary>平均勝率（win_rate_avg）。</summary>
    WinRate,

    /// <summary>利益効率σの平均（profit_eff_avg）。</summary>
    ProfitEfficiency,

    /// <summary>勝率の Wilson 95% 信頼下限の平均（win_rate_lcb_avg）。</summary>
    WinRateLowerBound,
}

/// <summary>
/// データ抽出モード = ポイント抽出の定義。
/// SQL は candidate_stats に対する SELECT で、direction / entry_min / hold_min / score 列を優先順に返す。
/// アプリはその順に時間帯の重ならない候補を最大 50 件選ぶ。
/// </summary>
public sealed record ModeDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string Sql { get; init; }

    public bool Enabled { get; init; }

    /// <summary>複合ポイントで比べる指標。null は複合ポイントの対象外（既定モードは <see cref="EffectiveCompositeMetric"/> で補う）。</summary>
    public CompositeMetric? CompositeMetric { get; init; }

    /// <summary>
    /// 複合ポイントに使う指標。保存済みの modes.json に項目がない既定モードは、同じ ID の既定値を使う。
    /// </summary>
    [JsonIgnore]
    public CompositeMetric? EffectiveCompositeMetric =>
        CompositeMetric ?? Defaults.FirstOrDefault(d => d.Id == Id)?.CompositeMetric;

    /// <summary>
    /// 廃止した既定モードの ID。保存済みの modes.json から取り除く
    /// （時間効率 = 最大利益基礎値 ÷ 保有時間。単位があり銘柄をまたいで比べられないため廃止）。
    /// </summary>
    public static IReadOnlyList<string> RetiredIds { get; } = ["time-efficiency"];

    /// <summary>
    /// 既定のポイント（勝率重視・利益効率と、勝率の信頼下限）。
    /// 保存済みの modes.json に無い既定モードは、読み込み時に無効の状態で追加する（既存の抽出結果を変えないため）。
    /// </summary>
    public static IReadOnlyList<ModeDefinition> Defaults { get; } =
    [
        new()
        {
            Id = "win-rate",
            Name = "勝率重視",
            Description = "30/90/365日の平均勝率が高い時間帯を抽出",
            Sql = BuiltInSql("score_win_rate"),
            Enabled = true,
            CompositeMetric = Modes.CompositeMetric.WinRate,
        },
        new()
        {
            Id = "profit-efficiency",
            Name = "利益効率",
            Description = "利益効率σ（合計 ÷ σ√n）の平均が高い時間帯を抽出",
            Sql = BuiltInSql("score_profit_eff"),
            Enabled = true,
            CompositeMetric = Modes.CompositeMetric.ProfitEfficiency,
        },
        new()
        {
            Id = "win-rate-lcb",
            Name = "勝率（信頼下限）",
            Description = "勝率の 95% 信頼区間の下限（サンプルが少ない偶然の高勝率を割り引いた値）の 30/90/365 日平均が高い時間帯を抽出",
            Sql = BuiltInSql("win_rate_lcb_avg"),
            Enabled = true,
            CompositeMetric = Modes.CompositeMetric.WinRateLowerBound,
        },
    ];

    /// <summary>
    /// ポイント抽出の並び順に使える列（<see cref="BuiltInSql"/> の score 列として認める列）。
    /// この列で作った既定の SQL は、DuckDB を通さずに候補統計から直接同じ結果を選べる（<see cref="BuiltInScoreColumn"/>）。
    /// </summary>
    public static IReadOnlyList<string> BuiltInScoreColumns { get; } =
        ["score_win_rate", "score_profit_eff", "score_max_profit", "win_rate_lcb_avg", "win_rate_avg", "profit_eff_avg", "max_profit_base"];

    /// <summary>
    /// SQL が <see cref="BuiltInSql"/> で作った既定の形（空白の違いは無視）なら、その score 列名。ユーザーが編集した SQL は null。
    /// </summary>
    public static string? BuiltInScoreColumn(string sql)
    {
        var normalized = Normalize(sql);
        return BuiltInScoreColumns.FirstOrDefault(column => Normalize(BuiltInSql(column)) == normalized);
    }

    private static string Normalize(string sql) =>
        string.Join(' ', sql.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).TrimEnd(';').Trim();

    /// <summary>
    /// 既定のポイント抽出条件: 品質除外なし・保有 3〜15 分、
    /// 並びは score 降順 → Entry 昇順 → 保有 昇順 → Long 先。
    /// </summary>
    public static string BuiltInSql(string scoreColumn) => $"""
        SELECT direction, entry_min, hold_min, {scoreColumn} AS score
        FROM candidate_stats
        WHERE run_id = $run_id
          AND NOT quality_excluded
          AND {scoreColumn} IS NOT NULL
          AND hold_min BETWEEN 3 AND 15
        ORDER BY score DESC, entry_min, hold_min, direction
        """;
}
