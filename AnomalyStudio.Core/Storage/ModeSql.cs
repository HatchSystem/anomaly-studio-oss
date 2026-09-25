using System.Globalization;
using System.Text.RegularExpressions;
using AnomalyStudio.Core.Modes;

namespace AnomalyStudio.Core.Storage;

public sealed class ModeSqlException(string message) : Exception(message);

/// <summary>モードの抽出 SQL の検証と整形。</summary>
public static partial class ModeSql
{
    public const string RunIdPlaceholder = "$run_id";

    /// <summary>
    /// 1 文の SELECT（WITH を含む）だけを許可し、<c>$run_id</c> を run の番号に置き換える。
    /// run_id はアプリが採番した整数なので文字列置換でも安全。
    /// </summary>
    public static string Prepare(string sql, long runId)
    {
        var text = StripComments(sql).Trim().TrimEnd(';').Trim();
        if (text.Length == 0)
        {
            throw new ModeSqlException("抽出 SQL が空です。");
        }

        if (!StartsWithSelect().IsMatch(text))
        {
            throw new ModeSqlException("抽出 SQL は SELECT 文（または WITH で始まる SELECT）にしてください。");
        }

        if (text.Contains(';', StringComparison.Ordinal))
        {
            throw new ModeSqlException("抽出 SQL には 1 文だけを書いてください。");
        }

        if (Forbidden().IsMatch(text))
        {
            throw new ModeSqlException("抽出 SQL でデータを変更する命令は使えません。");
        }

        if (ExternalAccess().IsMatch(text))
        {
            throw new ModeSqlException("抽出 SQL でファイルや外部リソースを読む関数は使えません（candidate_stats だけを参照してください）。");
        }

        return text.Replace(RunIdPlaceholder, runId.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 複合ポイント用: モードの SQL を候補の絞り込みとして使い、score を銘柄をまたいで比べられる指標のもとの値に置き換える。
    /// 列名は列挙値から決まる定数なので文字列で組み立てても安全。
    /// </summary>
    public static string PrepareComposite(string sql, long runId, CompositeMetric metric)
    {
        var inner = Prepare(sql, runId);
        var column = CompositeColumn(metric);
        var run = runId.ToString(CultureInfo.InvariantCulture);
        return $"""
            SELECT c.direction, c.entry_min, c.hold_min, c.{column} AS score
            FROM ({inner}) AS q
            JOIN candidate_stats c
              ON c.run_id = {run} AND c.direction = q.direction AND c.entry_min = q.entry_min AND c.hold_min = q.hold_min
            WHERE isfinite(c.{column})
            """;
    }

    /// <summary>複合ポイントで銘柄をまたいで比べる指標の列名。</summary>
    public static string CompositeColumn(CompositeMetric metric) => metric switch
    {
        CompositeMetric.WinRate => "win_rate_avg",
        CompositeMetric.ProfitEfficiency => "profit_eff_avg",
        CompositeMetric.WinRateLowerBound => "win_rate_lcb_avg",
        _ => throw new ArgumentOutOfRangeException(nameof(metric)),
    };

    private static string StripComments(string sql) => LineComment().Replace(BlockComment().Replace(sql, " "), " ");

    [GeneratedRegex(@"^(SELECT|WITH)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StartsWithSelect();

    [GeneratedRegex(@"\b(INSERT|UPDATE|DELETE|DROP|CREATE|ALTER|COPY|ATTACH|DETACH|INSTALL|LOAD|PRAGMA|SET|EXPORT|IMPORT|CALL|CHECKPOINT|VACUUM)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Forbidden();

    /// <summary>
    /// ローカルのファイルや環境変数、ネットワークを読める DuckDB の関数。モードの SQL は AI アシストの出力（未信頼データ）も通るので、
    /// 変更系の命令と同じく拒否する。
    /// </summary>
    [GeneratedRegex(@"\b(read_\w+|scan_\w+|parquet_\w+|glob|getenv|sniff_csv|from_\w+|iceberg_\w+|delta_\w+|http\w*|s3\w*)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalAccess();

    [GeneratedRegex(@"--[^\r\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();
}
