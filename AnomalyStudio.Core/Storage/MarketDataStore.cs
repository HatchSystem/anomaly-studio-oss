using System.Text;
using AnomalyStudio.Core.Analysis;
using AnomalyStudio.Core.Diagnostics;
using AnomalyStudio.Core.MarketData;
using DuckDB.NET.Data;

namespace AnomalyStudio.Core.Storage;

/// <summary>
/// 市場データ（1 分足の月別 Parquet）の読み書き。メモリ上の DuckDB を Parquet の読み書きにだけ使い、分析結果の DB ファイルには触れない。
/// 分析結果の DB（<see cref="AnalysisDatabase"/>）と別の接続・別の直列化にしているので、分析結果の保存や重い SQL の間も画面の読込が待たされない。
/// Parquet ファイルの読み書きは 1 つずつ直列に行う（書き換え中のファイルを読まないため）。
/// アプリからは <see cref="AnalysisDatabase"/> を通して使う。
/// </summary>
public sealed class MarketDataStore : IAsyncDisposable
{
    private const string ParquetColumns =
        "ts_utc, bid_open, bid_high, bid_low, bid_close, bid_volume, ask_open, ask_high, ask_low, ask_close, ask_volume";

    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DuckDBConnection _connection;

    public MarketDataStore(AppPaths paths)
    {
        _paths = paths;
        paths.EnsureCreated();
        _connection = new DuckDBConnection("Data Source=:memory:");
        _connection.Open();

        // Parquet の読み書きだけなので、メモリとスレッドを分析結果の DB より控えめにする
        Execute("SET memory_limit = '512MB'; SET threads = 4;");
    }

    /// <summary>保存済みの 1 分足が変わった（追加・削除）。引数は銘柄 ID、null はすべての銘柄。読み込んだ足のキャッシュを捨てる合図。</summary>
    public event Action<string?>? Changed;

    /// <summary>保存済みの最新の足の時刻（UTC）。未取得なら null。</summary>
    public Task<DateTime?> GetLatestBarTimeAsync(string symbolId) => RunAsync(() =>
    {
        var glob = MarketGlob(symbolId);
        if (glob is null)
        {
            return (DateTime?)null;
        }

        using var cmd = Command($"SELECT max(ts_utc) FROM read_parquet({glob})");
        return cmd.ExecuteScalar() is DateTime t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null;
    });

    /// <summary>保存済みの足の本数と期間。</summary>
    public Task<(long Count, DateTime? FirstUtc, DateTime? LastUtc)> GetCoverageAsync(string symbolId) => RunAsync(() =>
    {
        var glob = MarketGlob(symbolId);
        if (glob is null)
        {
            return (0L, (DateTime?)null, (DateTime?)null);
        }

        using var cmd = Command($"SELECT count(*), min(ts_utc), max(ts_utc) FROM read_parquet({glob})");
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), AsUtc(reader.GetValue(1)), AsUtc(reader.GetValue(2)));
    });

    /// <summary>1 分足を月別の Parquet に追記する（同じ時刻は新しい値で置き換え）。</summary>
    public async Task SaveBarsAsync(string symbolId, IReadOnlyList<MinuteBar> bars)
    {
        await RunAsync(() =>
        {
            using var timing = Timing.Start("storage", "save-bars");
            timing.Detail = $"{symbolId} {bars.Count:N0} 本";
            Directory.CreateDirectory(_paths.MarketDirectory(symbolId));
            foreach (var month in bars.GroupBy(b => (b.TimeUtc.Year, b.TimeUtc.Month)))
            {
                Execute($"CREATE OR REPLACE TEMP TABLE staging_m1 (ts_utc TIMESTAMP, bid_open DOUBLE, bid_high DOUBLE, bid_low DOUBLE, bid_close DOUBLE, bid_volume DOUBLE, ask_open DOUBLE, ask_high DOUBLE, ask_low DOUBLE, ask_close DOUBLE, ask_volume DOUBLE)");
                using (var appender = _connection.CreateAppender("staging_m1"))
                {
                    foreach (var b in month)
                    {
                        appender.CreateRow()
                            .AppendValue(b.TimeUtc)
                            .AppendValue(b.BidOpen).AppendValue(b.BidHigh).AppendValue(b.BidLow).AppendValue(b.BidClose).AppendValue(b.BidVolume)
                            .AppendValue(b.AskOpen).AppendValue(b.AskHigh).AppendValue(b.AskLow).AppendValue(b.AskClose).AppendValue(b.AskVolume)
                            .EndRow();
                    }
                }

                var file = _paths.MarketFile(symbolId, month.Key.Year, month.Key.Month);
                var temp = file + ".tmp";
                var source = File.Exists(file)
                    ? $"SELECT {ParquetColumns}, 0 AS src FROM read_parquet({Literal(file)}) UNION ALL SELECT {ParquetColumns}, 1 AS src FROM staging_m1"
                    : $"SELECT {ParquetColumns}, 1 AS src FROM staging_m1";
                Execute($"""
                    COPY (
                        SELECT {ParquetColumns} FROM ({source})
                        QUALIFY row_number() OVER (PARTITION BY ts_utc ORDER BY src DESC) = 1
                        ORDER BY ts_utc
                    ) TO {Literal(temp)} (FORMAT PARQUET, COMPRESSION ZSTD)
                    """);
                File.Move(temp, file, overwrite: true);
                Execute("DROP TABLE staging_m1");
            }
        });

        Changed?.Invoke(symbolId);
    }

    /// <summary>指定範囲の BID / ASK Open を時刻順に読む。</summary>
    public Task<IReadOnlyList<OpenQuote>> LoadOpenQuotesAsync(string symbolId, DateTime fromUtc, DateTime toUtc) => RunAsync(() =>
    {
        var glob = MarketGlob(symbolId);
        if (glob is null)
        {
            return (IReadOnlyList<OpenQuote>)[];
        }

        using var timing = Timing.Start("storage", "load-quotes");
        using var cmd = Command($"""
            SELECT ts_utc, bid_open, ask_open FROM read_parquet({glob})
            WHERE ts_utc >= $from AND ts_utc < $to ORDER BY ts_utc
            """);
        cmd.Parameters.Add(new DuckDBParameter("from", fromUtc));
        cmd.Parameters.Add(new DuckDBParameter("to", toUtc));
        using var reader = cmd.ExecuteReader();
        var list = new List<OpenQuote>();
        while (reader.Read())
        {
            list.Add(new OpenQuote(DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc), reader.GetDouble(1), reader.GetDouble(2)));
        }

        timing.Detail = $"{symbolId} {list.Count:N0} 本（{(toUtc - fromUtc).TotalDays:0} 日）";
        return (IReadOnlyList<OpenQuote>)list;
    });

    /// <summary>銘柄の市場データ（Parquet のフォルダー）を削除する。</summary>
    public async Task DeleteAsync(string symbolId)
    {
        await RunAsync(() =>
        {
            var dir = _paths.MarketDirectory(symbolId);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        });

        Changed?.Invoke(symbolId);
    }

    /// <summary>すべての銘柄の市場データを削除する。</summary>
    public async Task DeleteAllAsync()
    {
        await RunAsync(() =>
        {
            var market = Path.Combine(_paths.DataDirectory, "market");
            if (Directory.Exists(market))
            {
                Directory.Delete(market, recursive: true);
            }
        });

        Changed?.Invoke(null);
    }

    private Task<T> RunAsync<T>(Func<T> action) => Task.Run(async () =>
    {
        await _gate.WaitAsync();
        try
        {
            return action();
        }
        finally
        {
            _gate.Release();
        }
    });

    private Task RunAsync(Action action) => RunAsync(() =>
    {
        action();
        return true;
    });

    private DuckDBCommand Command(string sql)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    private void Execute(string sql)
    {
        using var cmd = Command(sql);
        cmd.ExecuteNonQuery();
    }

    /// <summary>銘柄の Parquet ファイルがあれば read_parquet に渡すリスト式、なければ null。</summary>
    private string? MarketGlob(string symbolId)
    {
        var dir = _paths.MarketDirectory(symbolId);
        if (!Directory.Exists(dir))
        {
            return null;
        }

        var files = Directory.GetFiles(dir, "*.parquet");
        if (files.Length == 0)
        {
            return null;
        }

        var sb = new StringBuilder("[");
        sb.AppendJoin(", ", files.Order(StringComparer.Ordinal).Select(Literal));
        return sb.Append(']').ToString();
    }

    private static string Literal(string path) => "'" + path.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal) + "'";

    private static DateTime? AsUtc(object value) => value is DateTime t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null;

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        await _connection.DisposeAsync();
        _gate.Dispose();
    }
}
