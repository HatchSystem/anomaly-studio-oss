using System.Globalization;
using System.Text.Json.Serialization;
using AnomalyStudio.Core.Analysis;

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

    /// <summary>
    /// 勝率の Wilson 95% 信頼下限の平均（win_rate_lcb_avg）。既定モード「勝率（信頼下限）」は廃止したが、
    /// 以前の版の modes.json を読めるように値は残す（読み込み後に <see cref="ModeDefinition.RetiredIds"/> で取り除く）。
    /// </summary>
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
    /// （時間効率 = 最大利益基礎値 ÷ 保有時間。単位があり銘柄をまたいで比べられないため廃止。
    /// 勝率（信頼下限）は利用者の判断で廃止。列 win_rate_lcb_* は自作モードの SQL で使えるように残す）。
    /// </summary>
    public static IReadOnlyList<string> RetiredIds { get; } = ["time-efficiency", "win-rate-lcb"];

    /// <summary>
    /// 勝率重視BO の保有時間（分）。Entry の足の始値から、この分数後の足の始値で決済する（10 分なら 9:00 → 9:10）。
    /// 勝率重視BO の条件（この定数と下の 2 つ）は <see cref="Defaults"/> より前に初期化する（static の初期化は書いた順）。
    /// </summary>
    public static IReadOnlyList<int> BinaryOptionHolds { get; } = [1, 3, 5, 10, 15, 30, 60];

    /// <summary>勝率重視BO の最低勝率（短期・中期・長期のすべてでこの値以上）。</summary>
    public const double BinaryOptionMinWinRate = 0.55;

    /// <summary>勝率重視BO の最低勝率を見る期間（日）: 短期 30・中期 90・長期 365（勝率重視の並び順と同じ期間）。</summary>
    public static IReadOnlyList<int> BinaryOptionWinRatePeriods { get; } = [30, 90, 365];

    /// <summary>勝率重視BO の抽出条件（<paramref name="scoreColumn"/> の順に、保有時間と最低勝率で絞る）。</summary>
    public static BuiltInSelection BinaryOption(string scoreColumn) =>
        new(scoreColumn, BinaryOptionHolds, BinaryOptionMinWinRate, BinaryOptionWinRatePeriods);

    /// <summary>
    /// 以前の版の既定モードの SQL と説明。保存済みの modes.json がこの形のままなら、今の既定に置き換える（<see cref="Upgrade"/>）。
    /// </summary>
    private static readonly (string Id, string Sql, string Description)[] PreviousDefaults =
    [
        ("win-rate-bo",
            BuiltInSql(new BuiltInSelection("score_win_rate", BinaryOptionHolds)),
            "保有 1・3・5・10・15・30・60 分（バイナリーオプションの判定時間）に限って、30/90/365日の平均勝率が高い時間帯を抽出"),
    ];

    /// <summary>
    /// 既定のポイント（勝率重視・勝率重視BO・利益効率）。
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
            Id = "win-rate-bo",
            Name = "勝率重視BO",
            Description = "保有 1・3・5・10・15・30・60 分（バイナリーオプションの判定時間）に限り、短期（30日）・中期（90日）・長期（365日）の勝率がすべて 55% 以上の中から、30/90/365日の平均勝率が高い時間帯を抽出",
            Sql = BuiltInSql(BinaryOption("score_win_rate")),
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
    ];

    /// <summary>既定モード（<see cref="Defaults"/> にある ID）か。既定モードは削除できず、「既定に戻す」で既定の名前・説明・SQL に戻せる。</summary>
    [JsonIgnore]
    public bool IsDefault => Defaults.Any(d => d.Id == Id);

    /// <summary>既定モードで、SQL が今の既定と同じ（空白の違いは無視）か。既定モードでなければ false。</summary>
    [JsonIgnore]
    public bool HasDefaultSql => Defaults.FirstOrDefault(d => d.Id == Id) is { } d && Normalize(d.Sql) == Normalize(Sql);

    /// <summary>エントリー画面のモードの選択肢で「絞り込まない」を表す語。モード名には使えない。</summary>
    public const string AllModesLabel = "すべて";

    /// <summary>モード名に使えない文字（CSV・レポートのファイル名に使うため、どの OS でもファイル名に使えない文字）。</summary>
    private static readonly char[] InvalidNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// モード名を確かめる。問題があれば理由（画面に出す文）、なければ null。
    /// 空・前後の空白・同じ名前のモード（<paramref name="selfId"/> 以外）・「すべて」・ファイル名に使えない文字は使えない。
    /// 画面や設定はモードを名前で覚えているので、名前は一意にする。
    /// </summary>
    public static string? ValidateName(string name, IEnumerable<ModeDefinition> modes, string? selfId)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "モード名を入力してください";
        }

        if (name != name.Trim())
        {
            return "モード名の前後に空白を入れないでください";
        }

        if (name == AllModesLabel)
        {
            return $"「{AllModesLabel}」はモード名に使えません";
        }

        if (name.IndexOfAny(InvalidNameChars) >= 0 || name.Any(char.IsControl))
        {
            return $"モード名に次の文字は使えません: {string.Join(" ", InvalidNameChars)}";
        }

        return modes.Any(m => m.Id != selfId && string.Equals(m.Name, name, StringComparison.Ordinal))
            ? $"「{name}」という名前のモードは既にあります"
            : null;
    }

    /// <summary>
    /// 保存済みのモードを今の版へ合わせる。既定モードの SQL が以前の版の既定の形のままなら今の既定の SQL に、
    /// 説明も以前の既定のままなら今の説明にする。ユーザーが編集した SQL・説明とユーザーのモードはそのまま返す。
    /// </summary>
    public static ModeDefinition Upgrade(ModeDefinition saved)
    {
        if (Defaults.FirstOrDefault(d => d.Id == saved.Id) is not { } current)
        {
            return saved;
        }

        var normalized = Normalize(saved.Sql);
        foreach (var (id, sql, description) in PreviousDefaults)
        {
            if (id == saved.Id && Normalize(sql) == normalized)
            {
                return saved with
                {
                    Sql = current.Sql,
                    Description = saved.Description == description ? current.Description : saved.Description,
                };
            }
        }

        return saved;
    }

    /// <summary>
    /// ポイント抽出の並び順に使える列（<see cref="BuiltInSql(string)"/> の score 列として認める列）。
    /// この列で作った既定の SQL は、DuckDB を通さずに候補統計から直接同じ結果を選べる（<see cref="ParseBuiltIn"/>）。
    /// </summary>
    public static IReadOnlyList<string> BuiltInScoreColumns { get; } =
        ["score_win_rate", "score_profit_eff", "score_max_profit", "win_rate_lcb_avg", "win_rate_avg", "profit_eff_avg", "max_profit_base"];

    /// <summary>
    /// SQL が既定の形（<see cref="BuiltInSql(string)"/>、または勝率重視BO の条件の <see cref="BinaryOption"/>。
    /// 空白の違いは無視）なら、その抽出条件。ユーザーが編集した SQL は null。
    /// </summary>
    public static BuiltInSelection? ParseBuiltIn(string sql)
    {
        var normalized = Normalize(sql);
        foreach (var column in BuiltInScoreColumns)
        {
            foreach (var selection in new[] { new BuiltInSelection(column), BinaryOption(column) })
            {
                if (Normalize(BuiltInSql(selection)) == normalized)
                {
                    return selection;
                }
            }
        }

        return null;
    }

    private static string Normalize(string sql) =>
        string.Join(' ', sql.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).TrimEnd(';').Trim();

    /// <summary>
    /// 既定のポイント抽出条件: 品質除外なし・保有 3〜15 分、
    /// 並びは score 降順 → Entry 昇順 → 保有 昇順 → Long 先。
    /// </summary>
    public static string BuiltInSql(string scoreColumn) => BuiltInSql(new BuiltInSelection(scoreColumn));

    /// <summary>
    /// 既定の形のポイント抽出条件。保有時間の指定があればその保有時間（なければ 3〜15 分）に限り、
    /// 最低勝率の指定があれば各期間の勝率（win_rate_{日数}）がその値以上の候補に限る。それ以外は <see cref="BuiltInSql(string)"/> と同じ。
    /// </summary>
    public static string BuiltInSql(BuiltInSelection selection)
    {
        var conditions = new List<string>
        {
            selection.Holds is null ? "hold_min BETWEEN 3 AND 15" : $"hold_min IN ({string.Join(", ", selection.Holds)})",
        };
        if (selection.MinWinRate is { } min)
        {
            conditions.AddRange(selection.WinRatePeriods!.Select(days =>
                $"win_rate_{days} >= {min.ToString(CultureInfo.InvariantCulture)}"));
        }

        var column = selection.ScoreColumn;
        return $"""
            SELECT direction, entry_min, hold_min, {column} AS score
            FROM candidate_stats
            WHERE run_id = $run_id
              AND NOT quality_excluded
              AND {column} IS NOT NULL
              AND {string.Join("\n  AND ", conditions)}
            ORDER BY score DESC, entry_min, hold_min, direction
            """;
    }
}
