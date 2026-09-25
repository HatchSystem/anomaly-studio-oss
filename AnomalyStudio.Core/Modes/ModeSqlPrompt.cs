using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnomalyStudio.Core.Modes;

/// <summary>
/// AI アシスト（LLM）でモードの抽出 SQL を作るときのプロンプトと、応答からの SQL の取り出し。
/// 表の定義・列の意味・必須の規則・書き方の見本を渡し、アプリがそのまま実行できる SQL だけを返させる。
/// </summary>
public static partial class ModeSqlPrompt
{
    /// <summary>書き方の見本（既定の「勝率重視」と同じ形）。</summary>
    public static string FormatExample { get; } = """
        SELECT direction, entry_min, hold_min, score_win_rate AS score
        FROM candidate_stats
        WHERE run_id = $run_id
          AND NOT quality_excluded
          AND win_rate_30 >= 0.60
          AND win_rate_90 >= 0.60
          AND hold_min BETWEEN 3 AND 15
        ORDER BY score DESC, entry_min, hold_min, direction
        """;

    /// <summary>システムプロンプト。<paramref name="schema"/> は candidate_stats の CREATE TABLE 文。</summary>
    public static string System(string schema) => $"""
        あなたは FX アノマリー分析アプリ AnomalyStudio の「モードの抽出 SQL」を書く DuckDB の専門家です。
        ユーザーの要望を、次の表から候補を選ぶ SELECT 文 1 つに変換してください。

        ## 表の定義（DuckDB）
        {schema.Trim()}

        ## 表の意味
        - 1 行が 1 つの取引候補: 毎日同じ時刻（entry_min）に Entry し、hold_min 分後に決済する取引。値は過去の日数ごとの成績。
        - run_id: 分析結果の番号。必ず `run_id = $run_id` で絞る（$run_id はアプリが置き換えるので、そのまま書く）。
        - direction: 'Long' または 'Short'。
        - entry_min: Entry 時刻を 0:00（日本時間）からの分で表す（0〜1439）。例 8:00 = 480、21:30 = 1290。
        - hold_min: 保有時間（分、3〜30）。close_min: 決済時刻の分（0〜1439）。crosses_day: 決済が翌日になる。
        - 期間ごとの列（接尾辞 _30 / _90 / _180 / _365 = 直近の日数）:
          n_ = 取引数、wins_ = 勝ち数、win_rate_ = 勝率（0〜1 の割合。60% は 0.60）、
          total_ = 合計損益、mean_ = 1 回平均損益、sigma_ = 損益の標準偏差、
          profit_eff_ = 利益効率σ（total / (sigma·√n)、大きいほど安定して勝つ）、spread_ = 平均スプレッド。
          損益は価格の単位（スプレッド控除後）で、銘柄によって単位が違う。
        - win_rate_lcb_: 勝率の 95% 信頼区間の下限（Wilson 法、0〜1）。取引数が少ない偶然の高勝率を割り引いた値で、
          「安定して勝率が高い」順に並べるときは win_rate_ より win_rate_lcb_ を使う。
        - *_avg: 30 / 90 / 365 日の平均。max_profit_base: 1 回平均損益の 30 / 90 / 365 日平均。
        - quality_excluded: サンプル不足で評価から外した候補（通常は `NOT quality_excluded` で除く）。
        - score_win_rate / score_profit_eff / score_max_profit: 銘柄内の最大値を 100 にした 0〜100 のスコア。
          rank_*: そのスコアの順位（1 が最上位）。

        ## 必須の規則
        1. SELECT 文（WITH 可）を 1 文だけ書く。INSERT / UPDATE / DELETE / CREATE など変更系の命令は使わない。
        2. 結果の列は direction, entry_min, hold_min, score の 4 つ（score は並べ替えの基準。大きいほど良い）。
        3. FROM candidate_stats と WHERE run_id = $run_id を必ず含める。
        4. 並び順は ORDER BY score DESC, entry_min, hold_min, direction。
        5. 要望に指定がなければ NOT quality_excluded と hold_min BETWEEN 3 AND 15 を入れる。
        6. LIMIT は付けない（アプリが時間帯の重ならない上位 50 件を選ぶ）。
        7. 勝率は割合（0〜1）、時刻は 0:00 からの分に直して書く。

        ## 書き方（この見本と同じ形・インデント・大文字のキーワードで書く）
        ```sql
        {FormatExample.Trim()}
        ```

        ## 出力
        説明は書かず、SQL だけを ```sql で始まるコードブロック 1 つで返す。
        """;

    /// <summary>文法チェックのエラーや、指示との不一致がある SQL の修正を頼むメッセージ。</summary>
    public static string RepairRequest(string problem) => $"""
        この SQL には次の問題があります（実行時のエラー、または指示との不一致）。規則を守って修正した SQL だけを返してください。

        問題:
        {problem.Trim()}
        """;

    /// <summary>
    /// 指示文と SQL が一致しているかを確かめるシステムプロンプト。表の定義・列の意味は生成と同じものを渡す。
    /// 応答は {"match": true|false, "issues": ["..."]} の JSON だけにさせる。
    /// </summary>
    public static string ReviewSystem(string schema) => $"""
        あなたは FX アノマリー分析アプリ AnomalyStudio の抽出 SQL のレビュー担当です。
        ユーザーの指示文と、それをもとに作った SQL が渡されます。SQL が指示の内容を正しく表しているかだけを確かめてください。

        {TableAndRules(schema)}

        ## 確かめること
        - 指示にある条件（期間、しきい値、比較の向き、方向 Long / Short、時間帯、保有時間）がすべて SQL にあり、値が正しいか。
        - 単位の換算（勝率は 0〜1 の割合、時刻は 0:00 からの分）が正しいか。
        - 並び順の基準（score に使う列）が指示の「〜順」「〜重視」に合っているか。
        - 指示にない条件を勝手に足していないか（規則 5 の既定の条件は除く）。
        書き方（インデントや大文字）や、必須の規則どおりの部分は問題にしない。

        ## 出力
        説明は書かず、次の形の JSON だけを返す。issues には一致しない点を日本語で具体的に書く（一致していれば空）。
        {"{"}"match": true, "issues": []{"}"}
        """;

    /// <summary>生成用プロンプトのうち、表の定義・表の意味・必須の規則（照合でも同じ前提を使う）。</summary>
    private static string TableAndRules(string schema)
    {
        var text = System(schema);
        var start = text.IndexOf("## 表の定義", StringComparison.Ordinal);
        return text[start..text.IndexOf("## 書き方", start, StringComparison.Ordinal)].Trim();
    }

    /// <summary>照合を頼むメッセージ。</summary>
    public static string ReviewRequest(string instruction, string sql) => $"""
        ## 指示文
        {instruction.Trim()}

        ## SQL
        ```sql
        {sql.Trim()}
        ```
        """;

    /// <summary>照合の応答を読む（```json コードブロックや前後の文も許す）。読めなければ null。</summary>
    public static SqlReview? ParseReview(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(content[start..(end + 1)]);
            var root = document.RootElement;
            if (!root.TryGetProperty("match", out var match) || match.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            var issues = root.TryGetProperty("issues", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!.Trim())
                    .Where(i => i.Length > 0).ToList()
                : [];
            return new SqlReview(match.GetBoolean(), issues);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>応答から SQL を取り出す（```sql コードブロックがあればその中、なければ全体）。末尾の ; は外す。</summary>
    public static string ExtractSql(string content)
    {
        var match = CodeBlock().Match(content);
        var sql = match.Success ? match.Groups["sql"].Value : content;
        return sql.Trim().TrimEnd(';').Trim().Replace("\r\n", "\n");
    }

    [GeneratedRegex(@"```(?:sql|SQL)?[ \t]*\r?\n(?<sql>.*?)```", RegexOptions.Singleline)]
    private static partial Regex CodeBlock();
}

/// <summary>指示文と SQL の照合結果。<see cref="Issues"/> は一致しない点。</summary>
public sealed record SqlReview(bool Match, IReadOnlyList<string> Issues);
