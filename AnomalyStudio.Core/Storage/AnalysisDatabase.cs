using System.Data;
using System.Globalization;
using AnomalyStudio.Core.Analysis;
using AnomalyStudio.Core.Diagnostics;
using AnomalyStudio.Core.MarketData;
using AnomalyStudio.Core.Modes;
using DuckDB.NET.Data;

namespace AnomalyStudio.Core.Storage;

/// <summary>分析実行（run）の記録。</summary>
public sealed record AnalysisRun(
    long RunId,
    string SymbolId,
    DateOnly ReportDate,
    string LogicVersion,
    DateTime CreatedAtUtc,
    long DurationMs,
    long BarCount)
{
    /// <summary>
    /// 今の計算方式（<see cref="EngineParameters.LogicVersion"/>）で計算した結果か。違う版の結果は、更新で計算方式が変わる前のものなので、
    /// 画面に使わず「再分析が必要」として扱う。
    /// </summary>
    public bool IsCurrent => LogicVersion == EngineParameters.LogicVersion;
}

/// <summary>ポイントの表示用に候補統計から引く値。値幅は価格単位。</summary>
public sealed record CandidateSummary(
    TradeDirection Direction,
    int EntryMinute,
    int HoldMinutes,
    double WinRateAvg,
    double MaxProfitBase,
    double ProfitEffAvg,
    double SpreadAvg);

/// <summary>
/// 市場データ（Parquet）と分析結果（DuckDB）の読み書きの窓口。
/// DuckDB のファイルは同時に 1 プロセス・1 接続からしか安全に書けないため、分析結果の操作はこのクラスに集約し、1 つずつ直列に実行する。
/// 市場データは <see cref="MarketDataStore"/>（別の接続・別の直列化）に委ね、分析結果の保存や重い SQL の間も画面の読込が待たされないようにする。
/// </summary>
public sealed class AnalysisDatabase : IAsyncDisposable
{
    /// <summary>自動で DB ファイルを作り直す（縮小する）目安: 空き領域がこの大きさ以上、かつファイルの <see cref="CompactionMinFreeRatio"/> 以上。</summary>
    public const long DefaultCompactionMinFreeBytes = 64L * 1024 * 1024;

    public const double CompactionMinFreeRatio = 0.25;

    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MarketDataStore _market;
    private readonly long _compactionMinFreeBytes;
    private DuckDBConnection _connection;

    /// <param name="compactionMinFreeBytes">自動縮小の空き領域のしきい値（テスト用に下げられる）。</param>
    public AnalysisDatabase(AppPaths paths, long compactionMinFreeBytes = DefaultCompactionMinFreeBytes)
    {
        _paths = paths;
        _compactionMinFreeBytes = compactionMinFreeBytes;
        paths.EnsureCreated();
        _market = new MarketDataStore(paths);
        _market.Changed += symbol => MarketDataChanged?.Invoke(symbol);
        _connection = Open();

        // 古い run を消して空きが大きくなっていれば、起動時に作り直して縮める（DELETE だけではファイルは縮まない）
        CompactIfNeeded();
    }

    /// <summary>保存済みの 1 分足が変わった（追加・削除）。引数は銘柄 ID、null はすべての銘柄。読み込んだ足のキャッシュを捨てる合図。</summary>
    public event Action<string?>? MarketDataChanged;

    /// <summary>DB ファイルを作り直して縮めた（起動時か古い run の削除後）。引数は縮小前後のバイト数。</summary>
    public event Action<(long Before, long After)>? Compacted;

    private DuckDBConnection Open()
    {
        var connection = new DuckDBConnection($"Data Source={_paths.DatabaseFile}");
        connection.Open();
        Execute(connection, "SET memory_limit = '1GB';");
        Execute(connection, Schema);

        // 時間効率（単位があり銘柄をまたいで比べられない）の列は廃止した。以前の版で作った DB からも消す
        Execute(connection, """
            ALTER TABLE candidate_stats DROP COLUMN IF EXISTS time_eff_base;
            ALTER TABLE candidate_stats DROP COLUMN IF EXISTS score_time_eff;
            ALTER TABLE candidate_stats DROP COLUMN IF EXISTS rank_time_eff;
            """);

        // 勝率の信頼下限（後から追加した列）。以前の版で作った DB には末尾に足す（既存の run は NULL のまま。次の分析で埋まる）
        Execute(connection, string.Join('\n', LcbColumns.Select(c => $"ALTER TABLE candidate_stats ADD COLUMN IF NOT EXISTS {c} DOUBLE;")));
        return connection;
    }

    /// <summary>勝率の Wilson 95% 信頼下限の列（表の末尾。追加した順に並ぶので Appender の順序と一致する）。</summary>
    private static readonly string[] LcbColumns =
        ["win_rate_lcb_30", "win_rate_lcb_90", "win_rate_lcb_180", "win_rate_lcb_365", "win_rate_lcb_avg"];

    /// <summary>candidate_stats に保存する期間（列の並び）。分析にない期間は NULL。</summary>
    private static readonly int[] StoredPeriods = [30, 90, 180, 365];

    private const string Schema = """
        CREATE SEQUENCE IF NOT EXISTS run_seq START 1;
        CREATE TABLE IF NOT EXISTS analysis_run (
            run_id BIGINT PRIMARY KEY,
            symbol VARCHAR NOT NULL,
            report_date DATE NOT NULL,
            logic_version VARCHAR NOT NULL,
            created_at TIMESTAMP NOT NULL,
            duration_ms BIGINT NOT NULL,
            bar_count BIGINT NOT NULL,
            params VARCHAR
        );
        CREATE TABLE IF NOT EXISTS backtest_points (
            symbol VARCHAR NOT NULL,
            report_date DATE NOT NULL,
            cache_key VARCHAR NOT NULL,
            points VARCHAR NOT NULL,
            created_at TIMESTAMP NOT NULL,
            PRIMARY KEY (symbol, report_date, cache_key)
        );
        CREATE TABLE IF NOT EXISTS candidate_stats (
            run_id BIGINT NOT NULL,
            symbol VARCHAR NOT NULL,
            direction VARCHAR NOT NULL,
            entry_min INTEGER NOT NULL,
            hold_min INTEGER NOT NULL,
            close_min INTEGER NOT NULL,
            crosses_day BOOLEAN NOT NULL,
            n_30 INTEGER, wins_30 INTEGER, win_rate_30 DOUBLE, total_30 DOUBLE, mean_30 DOUBLE, sigma_30 DOUBLE, profit_eff_30 DOUBLE, spread_30 DOUBLE,
            n_90 INTEGER, wins_90 INTEGER, win_rate_90 DOUBLE, total_90 DOUBLE, mean_90 DOUBLE, sigma_90 DOUBLE, profit_eff_90 DOUBLE, spread_90 DOUBLE,
            n_180 INTEGER, wins_180 INTEGER, win_rate_180 DOUBLE, total_180 DOUBLE, mean_180 DOUBLE, sigma_180 DOUBLE, profit_eff_180 DOUBLE, spread_180 DOUBLE,
            n_365 INTEGER, wins_365 INTEGER, win_rate_365 DOUBLE, total_365 DOUBLE, mean_365 DOUBLE, sigma_365 DOUBLE, profit_eff_365 DOUBLE, spread_365 DOUBLE,
            win_rate_avg DOUBLE, total_avg DOUBLE, sigma_avg DOUBLE, profit_eff_avg DOUBLE, spread_avg DOUBLE,
            max_profit_base DOUBLE,
            quality_excluded BOOLEAN NOT NULL,
            score_win_rate DOUBLE, score_profit_eff DOUBLE, score_max_profit DOUBLE,
            rank_win_rate INTEGER, rank_profit_eff INTEGER, rank_max_profit INTEGER,
            win_rate_lcb_30 DOUBLE, win_rate_lcb_90 DOUBLE, win_rate_lcb_180 DOUBLE, win_rate_lcb_365 DOUBLE, win_rate_lcb_avg DOUBLE
        );
        """;

    /// <summary>モードの SQL から参照できる列（AI アシストの生成にも使う）。</summary>
    public static string CandidateStatsSchema => Schema[Schema.IndexOf("CREATE TABLE IF NOT EXISTS candidate_stats", StringComparison.Ordinal)..];

    // ------------------------------------------------------------------
    // 市場データ（Parquet、月別ファイル）。MarketDataStore に委ねる
    // ------------------------------------------------------------------

    /// <summary>保存済みの最新の足の時刻（UTC）。未取得なら null。</summary>
    public Task<DateTime?> GetLatestBarTimeAsync(string symbolId) => _market.GetLatestBarTimeAsync(symbolId);

    /// <summary>保存済みの足の本数と期間。</summary>
    public Task<(long Count, DateTime? FirstUtc, DateTime? LastUtc)> GetMarketCoverageAsync(string symbolId) => _market.GetCoverageAsync(symbolId);

    /// <summary>1 分足を月別の Parquet に追記する（同じ時刻は新しい値で置き換え）。</summary>
    public Task SaveBarsAsync(string symbolId, IReadOnlyList<MinuteBar> bars) => _market.SaveBarsAsync(symbolId, bars);

    /// <summary>指定範囲の BID / ASK Open を時刻順に読む。</summary>
    public Task<IReadOnlyList<OpenQuote>> LoadOpenQuotesAsync(string symbolId, DateTime fromUtc, DateTime toUtc) =>
        _market.LoadOpenQuotesAsync(symbolId, fromUtc, toUtc);

    /// <summary>
    /// 銘柄の市場データと、それから計算した分析結果・バックテスト用のポイントを削除する（銘柄を削除したとき）。
    /// 分析結果を残すと、同じ銘柄を追加し直したときに 1 分足のない古い分析結果が表示されるので、あわせて消す。
    /// </summary>
    public async Task DeleteMarketDataAsync(string symbolId)
    {
        await RunAsync(() =>
        {
            // 複合ポイントは全銘柄の分析から選ぶので、あわせて消す
            using (var cmd = Command("""
                DELETE FROM backtest_points WHERE symbol = $symbol OR symbol = $composite;
                DELETE FROM candidate_stats WHERE run_id IN (SELECT run_id FROM analysis_run WHERE symbol = $symbol);
                DELETE FROM analysis_run WHERE symbol = $symbol;
                """))
            {
                cmd.Parameters.Add(new DuckDBParameter("symbol", symbolId));
                cmd.Parameters.Add(new DuckDBParameter("composite", CompositeKey));
                cmd.ExecuteNonQuery();
            }

            CompactIfNeeded();
        });
        await _market.DeleteAsync(symbolId);
    }

    /// <summary>
    /// データの初期化: 取り込んだ 1 分足（全銘柄の Parquet）、分析結果、バックテスト用に保存したポイントを消す。
    /// 設定・モード・銘柄・経済指標カレンダーは消さない。
    /// </summary>
    public async Task ResetAsync()
    {
        await RunAsync(() =>
        {
            Execute("DELETE FROM candidate_stats; DELETE FROM analysis_run; DELETE FROM backtest_points; CHECKPOINT;");
            CompactIfNeeded();
        });
        await _market.DeleteAllAsync();
    }

    // ------------------------------------------------------------------
    // 分析結果（DuckDB）
    // ------------------------------------------------------------------

    public Task<AnalysisRun> SaveRunAsync(
        string symbolId, DateOnly reportDate, CandidateTable table, long durationMs, long barCount, string parametersJson) => RunAsync(() =>
    {
        using var timing = Timing.Start("storage", "save-run");
        timing.Detail = symbolId;
        using var tx = _connection.BeginTransaction();
        long runId;
        using (var cmd = Command("SELECT nextval('run_seq')"))
        {
            runId = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        var created = DateTime.UtcNow;
        using (var cmd = Command("""
            INSERT INTO analysis_run VALUES ($id, $symbol, $date, $version, $created, $duration, $bars, $params)
            """))
        {
            cmd.Parameters.Add(new DuckDBParameter("id", runId));
            cmd.Parameters.Add(new DuckDBParameter("symbol", symbolId));
            cmd.Parameters.Add(new DuckDBParameter("date", reportDate.ToDateTime(TimeOnly.MinValue)));
            cmd.Parameters.Add(new DuckDBParameter("version", EngineParameters.LogicVersion));
            cmd.Parameters.Add(new DuckDBParameter("created", created));
            cmd.Parameters.Add(new DuckDBParameter("duration", durationMs));
            cmd.Parameters.Add(new DuckDBParameter("bars", barCount));
            cmd.Parameters.Add(new DuckDBParameter("params", parametersJson));
            cmd.ExecuteNonQuery();
        }

        AppendCandidates(runId, symbolId, table);
        tx.Commit();
        return new AnalysisRun(runId, symbolId, reportDate, EngineParameters.LogicVersion, created, durationMs, barCount);
    });

    private void AppendCandidates(long runId, string symbolId, CandidateTable t)
    {
        var periods = StoredPeriods.Select(t.PeriodOrNull).ToArray();
        using var appender = _connection.CreateAppender("candidate_stats");
        for (var i = 0; i < CandidateGrid.Count; i++)
        {
            var row = appender.CreateRow()
                .AppendValue(runId)
                .AppendValue(symbolId)
                .AppendValue(CandidateGrid.DirectionName(CandidateGrid.Direction(i)))
                .AppendValue(CandidateGrid.Entry(i))
                .AppendValue(CandidateGrid.Hold(i))
                .AppendValue(CandidateGrid.Close(i))
                .AppendValue(CandidateGrid.CrossesDay(i));

            foreach (var m in periods)
            {
                if (m is null)
                {
                    for (var k = 0; k < 8; k++)
                    {
                        row.AppendNullValue();
                    }

                    continue;
                }

                row.AppendValue(m.N[i]).AppendValue(m.Wins[i]);
                Double(row, m.WinRate[i]);
                Double(row, m.Total[i]);
                Double(row, m.Mean[i]);
                Double(row, m.Sigma[i]);
                Double(row, m.ProfitEff[i]);
                Double(row, m.Spread[i]);
            }

            Double(row, t.WinRateAvg[i]);
            Double(row, t.TotalAvg[i]);
            Double(row, t.SigmaAvg[i]);
            Double(row, t.ProfitEffAvg[i]);
            Double(row, t.SpreadAvg[i]);
            Double(row, t.MaxProfitBase[i]);
            row.AppendValue(t.QualityExcluded[i]);
            Double(row, t.ScoreWinRate[i]);
            Double(row, t.ScoreProfitEff[i]);
            Double(row, t.ScoreMaxProfit[i]);
            Rank(row, t.RankWinRate[i]);
            Rank(row, t.RankProfitEff[i]);
            Rank(row, t.RankMaxProfit[i]);
            foreach (var m in periods)
            {
                Double(row, m is null ? double.NaN : m.WinRateLcb[i]);
            }

            Double(row, t.WinRateLcbAvg[i]);
            row.EndRow();
        }

        static void Double(IDuckDBAppenderRow row, double v)
        {
            if (double.IsFinite(v))
            {
                row.AppendValue(v);
            }
            else
            {
                row.AppendNullValue();
            }
        }

        static void Rank(IDuckDBAppenderRow row, int v)
        {
            if (v > 0)
            {
                row.AppendValue(v);
            }
            else
            {
                row.AppendNullValue();
            }
        }
    }

    /// <summary>銘柄ごとに新しい順で <paramref name="keep"/> 件を残し、古い run を削除する。空きが大きくなればファイルを作り直して縮める。</summary>
    public Task PruneRunsAsync(string symbolId, int keep) => RunAsync(() =>
    {
        using (var cmd = Command("""
            DELETE FROM candidate_stats WHERE run_id IN (
                SELECT run_id FROM analysis_run WHERE symbol = $symbol ORDER BY run_id DESC OFFSET $keep);
            DELETE FROM analysis_run WHERE run_id IN (
                SELECT run_id FROM analysis_run WHERE symbol = $symbol ORDER BY run_id DESC OFFSET $keep);
            """))
        {
            cmd.Parameters.Add(new DuckDBParameter("symbol", symbolId));
            cmd.Parameters.Add(new DuckDBParameter("keep", keep));
            cmd.ExecuteNonQuery();
        }

        CompactIfNeeded();
    });

    /// <summary>書き込みをファイルへ反映する（アプリを終了・再起動する前に呼ぶ）。</summary>
    public Task CheckpointAsync() => RunAsync(() => Execute("CHECKPOINT"));

    /// <summary>DB ファイルの大きさと、削除で空いた領域（バイト）。</summary>
    public Task<(long FileBytes, long FreeBytes)> GetDatabaseSizeAsync() => RunAsync(DatabaseSize);

    /// <summary>DB ファイルを作り直して縮める（空きの大きさによらず必ず行う）。</summary>
    public Task CompactAsync() => RunAsync(Compact);

    /// <summary>空き領域がしきい値を超えていれば縮める。</summary>
    internal static bool ShouldCompact(long fileBytes, long freeBytes, long minFreeBytes) =>
        freeBytes >= minFreeBytes && freeBytes >= fileBytes * CompactionMinFreeRatio;

    private void CompactIfNeeded()
    {
        Execute("CHECKPOINT");
        var (file, free) = DatabaseSize();
        if (ShouldCompact(file, free, _compactionMinFreeBytes))
        {
            Compact();
        }
    }

    private (long FileBytes, long FreeBytes) DatabaseSize()
    {
        var name = Path.GetFileNameWithoutExtension(_paths.DatabaseFile);
        using var cmd = Command("SELECT block_size, total_blocks, free_blocks FROM pragma_database_size() WHERE database_name = $name");
        cmd.Parameters.Add(new DuckDBParameter("name", name));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return (0, 0);
        }

        var blockSize = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
        var total = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
        var free = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
        return (blockSize * total, blockSize * free);
    }

    /// <summary>
    /// 表・順序番号・データを新しいファイルへ写し、元のファイルと入れ替える（DuckDB は DELETE した領域を再利用はするが返さないため）。
    /// 失敗したら元のファイルのまま続ける。
    /// </summary>
    private void Compact()
    {
        var file = _paths.DatabaseFile;
        var temp = file + ".compact";
        var backup = file + ".old";
        var before = new FileInfo(file).Length;

        Execute("CHECKPOINT");
        long nextRunId;
        using (var cmd = Command("SELECT coalesce(max(run_id), 0) + 1 FROM analysis_run"))
        {
            nextRunId = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        _connection.Dispose();
        try
        {
            DeleteIfExists(temp);
            DeleteIfExists(temp + ".wal");
            using (var work = new DuckDBConnection("Data Source=:memory:"))
            {
                work.Open();
                Execute(work, $"ATTACH {Literal(file)} AS src (READ_ONLY); ATTACH {Literal(temp)} AS dst;");
                Execute(work, "COPY FROM DATABASE src TO dst;");
                // 順序番号は写されても現在値が引き継がれないことがあるので、次の run の番号から始め直す
                Execute(work, $"USE dst; DROP SEQUENCE IF EXISTS run_seq; CREATE SEQUENCE run_seq START {nextRunId.ToString(CultureInfo.InvariantCulture)}; USE memory; DETACH dst; DETACH src;");
            }

            DeleteIfExists(temp + ".wal");
            File.Move(file, backup, overwrite: true);
            File.Move(temp, file);
            DeleteIfExists(file + ".wal");
            DeleteIfExists(backup);
        }
        catch (Exception ex) when (ex is DuckDBException or IOException or UnauthorizedAccessException)
        {
            // 元のファイルを戻す（写している途中で失敗したときは temp を消すだけ）
            if (!File.Exists(file) && File.Exists(backup))
            {
                File.Move(backup, file);
            }

            DeleteIfExists(temp);
            DeleteIfExists(temp + ".wal");
            _connection = Open();
            return;
        }

        _connection = Open();
        Compacted?.Invoke((before, new FileInfo(file).Length));
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string Literal(string path) => "'" + path.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal) + "'";

    public Task<AnalysisRun?> GetLatestRunAsync(string symbolId) => RunAsync(() =>
    {
        using var cmd = Command("""
            SELECT run_id, symbol, report_date, logic_version, created_at, duration_ms, bar_count
            FROM analysis_run WHERE symbol = $symbol ORDER BY run_id DESC LIMIT 1
            """);
        cmd.Parameters.Add(new DuckDBParameter("symbol", symbolId));
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new AnalysisRun(
                reader.GetInt64(0),
                reader.GetString(1),
                DateOnly.FromDateTime(reader.GetDateTime(2)),
                reader.GetString(3),
                DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
                reader.GetInt64(5),
                reader.GetInt64(6))
            : null;
    });

    /// <summary>
    /// モードの SQL を実行し、ポイント候補を SQL の並び順のまま返す。
    /// SQL は direction / entry_min / hold_min / score 列を返す SELECT 文で、<c>$run_id</c> が対象の run に置き換わる。
    /// </summary>
    public Task<IReadOnlyList<PointCandidate>> QueryPointCandidatesAsync(long runId, string sql) => RunAsync(() =>
    {
        var statement = ModeSql.Prepare(sql, runId);

        // 読み取り専用で実行し、万一の変更はロールバックする
        using var tx = _connection.BeginTransaction();
        var list = ReadPointCandidates(statement);
        tx.Rollback();
        return (IReadOnlyList<PointCandidate>)list;
    });

    /// <summary>
    /// モードの SQL の文法チェック。分析結果がなくても、行を読まずに実行して DuckDB の文法・列名と、
    /// 必須の列（direction / entry_min / hold_min / score）を確かめる。誤りは <see cref="ModeSqlException"/> か DuckDB の例外。
    /// </summary>
    public Task ValidateModeSqlAsync(string sql) => RunAsync(() =>
    {
        var statement = ModeSql.Prepare(sql, 0);
        using var tx = _connection.BeginTransaction();
        try
        {
            ReadPointCandidates($"SELECT * FROM ({statement}) AS q LIMIT 0");
        }
        finally
        {
            tx.Rollback();
        }
    });

    /// <summary>
    /// 候補統計を保存せずに、モードの SQL でポイントを選ぶ（ウォークフォワード・バックテスト用）。
    /// トランザクション内で一時的な run として追加し、各モードの SQL を実行したあとロールバックするので、DB には何も残らない。
    /// SQL が失敗したモードとそれ以降のモードは null を返す（失敗したトランザクションでは続けて実行できないため）。
    /// </summary>
    public Task<IReadOnlyList<IReadOnlyList<EntryPoint>?>> SelectPointsWithoutSavingAsync(
        string symbolId, CandidateTable table, IReadOnlyList<string> modeSqls, int limit = PointSelector.DefaultLimit) => RunAsync(() =>
    {
        var results = new IReadOnlyList<EntryPoint>?[modeSqls.Count];
        using var tx = _connection.BeginTransaction();
        try
        {
            AppendCandidates(TemporaryRunId, symbolId, table);
            for (var i = 0; i < modeSqls.Count; i++)
            {
                try
                {
                    var candidates = ReadPointCandidates(ModeSql.Prepare(modeSqls[i], TemporaryRunId));
                    results[i] = PointSelector.SelectNonOverlapping(candidates, limit);
                }
                catch (Exception ex) when (ex is ModeSqlException or DuckDBException)
                {
                    if (i == 0)
                    {
                        throw;
                    }

                    break;
                }
            }
        }
        finally
        {
            tx.Rollback();
        }

        return (IReadOnlyList<IReadOnlyList<EntryPoint>?>)results;
    });

    /// <summary>保存しない分析に使う run の番号（採番される番号とは重ならない）。</summary>
    private const long TemporaryRunId = long.MaxValue;

    /// <summary>複合ポイントの候補（score は銘柄をまたいで比べられる指標のもとの値）を返す。</summary>
    public Task<IReadOnlyList<PointCandidate>> QueryCompositeCandidatesAsync(long runId, string sql, CompositeMetric metric) => RunAsync(() =>
    {
        var statement = ModeSql.PrepareComposite(sql, runId, metric);
        using var tx = _connection.BeginTransaction();
        var list = ReadPointCandidates(statement);
        tx.Rollback();
        return (IReadOnlyList<PointCandidate>)list;
    });

    /// <summary>
    /// 候補統計を保存せずに、複合ポイントを選ぶ（ウォークフォワード・バックテスト用）。
    /// 銘柄ごとに一時的な run を追加してモードの SQL で候補を集め、銘柄をまたいで選んだあとロールバックする。
    /// </summary>
    public Task<IReadOnlyList<SymbolPoint>> SelectCompositePointsWithoutSavingAsync(
        IReadOnlyList<(string SymbolId, CandidateTable Table)> tables, string sql, CompositeMetric metric, int limit = PointSelector.DefaultLimit) =>
        RunAsync(() =>
        {
            var candidates = new Dictionary<string, IReadOnlyList<PointCandidate>>();
            using var tx = _connection.BeginTransaction();
            try
            {
                for (var i = 0; i < tables.Count; i++)
                {
                    var runId = TemporaryRunId - i;
                    AppendCandidates(runId, tables[i].SymbolId, tables[i].Table);
                    candidates[tables[i].SymbolId] = ReadPointCandidates(ModeSql.PrepareComposite(sql, runId, metric));
                }
            }
            finally
            {
                tx.Rollback();
            }

            return CompositePointSelector.Select(candidates, limit);
        });

    /// <summary>複合ポイントを backtest_points に保存するときの symbol 列の値（銘柄 ID には使われない文字を含む）。</summary>
    public const string CompositeKey = "*composite*";

    /// <summary>バックテスト用に保存した複合ポイントを基準日ごとに返す。</summary>
    public Task<IReadOnlyDictionary<DateOnly, IReadOnlyList<SymbolPoint>>> GetCompositeBacktestPointsAsync(string cacheKey) => RunAsync(() =>
    {
        using var cmd = Command("SELECT report_date, points FROM backtest_points WHERE symbol = $symbol AND cache_key = $key");
        cmd.Parameters.Add(new DuckDBParameter("symbol", CompositeKey));
        cmd.Parameters.Add(new DuckDBParameter("key", cacheKey));
        using var reader = cmd.ExecuteReader();
        var map = new Dictionary<DateOnly, IReadOnlyList<SymbolPoint>>();
        while (reader.Read())
        {
            map[DateOnly.FromDateTime(reader.GetDateTime(0))] = DecodeSymbolPoints(reader.GetString(1));
        }

        return (IReadOnlyDictionary<DateOnly, IReadOnlyList<SymbolPoint>>)map;
    });

    /// <summary>バックテスト用の複合ポイントを保存する（同じキーは置き換え）。</summary>
    public Task SaveCompositeBacktestPointsAsync(DateOnly reportDate, string cacheKey, IReadOnlyList<SymbolPoint> points) => RunAsync(() =>
    {
        using var cmd = Command("INSERT OR REPLACE INTO backtest_points VALUES ($symbol, $date, $key, $points, $created)");
        cmd.Parameters.Add(new DuckDBParameter("symbol", CompositeKey));
        cmd.Parameters.Add(new DuckDBParameter("date", reportDate.ToDateTime(TimeOnly.MinValue)));
        cmd.Parameters.Add(new DuckDBParameter("key", cacheKey));
        cmd.Parameters.Add(new DuckDBParameter("points", string.Join(';', points.Select(p => p.SymbolId + "," + EncodePoints([p.Point])))));
        cmd.Parameters.Add(new DuckDBParameter("created", DateTime.UtcNow));
        cmd.ExecuteNonQuery();
    });

    /// <summary>「銘柄,方向,Entry,保有,score」を ; で区切った文字列を読む（順位は並び順）。</summary>
    private static IReadOnlyList<SymbolPoint> DecodeSymbolPoints(string text) =>
        [.. text.Split(';', StringSplitOptions.RemoveEmptyEntries).Select((item, i) =>
        {
            var comma = item.IndexOf(',');
            return new SymbolPoint(item[..comma], DecodePoints(item[(comma + 1)..])[0] with { Rank = i + 1 });
        })];

    /// <summary>バックテスト用に保存したポイントを基準日ごとに返す（なければその日は含まない）。</summary>
    public Task<IReadOnlyDictionary<DateOnly, IReadOnlyList<EntryPoint>>> GetBacktestPointsAsync(string symbolId, string cacheKey) => RunAsync(() =>
    {
        using var cmd = Command("SELECT report_date, points FROM backtest_points WHERE symbol = $symbol AND cache_key = $key");
        cmd.Parameters.Add(new DuckDBParameter("symbol", symbolId));
        cmd.Parameters.Add(new DuckDBParameter("key", cacheKey));
        using var reader = cmd.ExecuteReader();
        var map = new Dictionary<DateOnly, IReadOnlyList<EntryPoint>>();
        while (reader.Read())
        {
            map[DateOnly.FromDateTime(reader.GetDateTime(0))] = DecodePoints(reader.GetString(1));
        }

        return (IReadOnlyDictionary<DateOnly, IReadOnlyList<EntryPoint>>)map;
    });

    /// <summary>バックテスト用のポイントを保存する（同じキーは置き換え）。</summary>
    public Task SaveBacktestPointsAsync(string symbolId, DateOnly reportDate, string cacheKey, IReadOnlyList<EntryPoint> points) => RunAsync(() =>
    {
        using var cmd = Command("INSERT OR REPLACE INTO backtest_points VALUES ($symbol, $date, $key, $points, $created)");
        cmd.Parameters.Add(new DuckDBParameter("symbol", symbolId));
        cmd.Parameters.Add(new DuckDBParameter("date", reportDate.ToDateTime(TimeOnly.MinValue)));
        cmd.Parameters.Add(new DuckDBParameter("key", cacheKey));
        cmd.Parameters.Add(new DuckDBParameter("points", EncodePoints(points)));
        cmd.Parameters.Add(new DuckDBParameter("created", DateTime.UtcNow));
        cmd.ExecuteNonQuery();
    });

    /// <summary>ポイントを「方向,Entry,保有,score」を ; で区切った文字列にする（順位は並び順）。</summary>
    private static string EncodePoints(IReadOnlyList<EntryPoint> points) => string.Join(';', points.Select(p => string.Join(',',
        CandidateGrid.DirectionName(p.Direction),
        p.EntryMinute.ToString(CultureInfo.InvariantCulture),
        p.HoldMinutes.ToString(CultureInfo.InvariantCulture),
        p.Score.ToString("R", CultureInfo.InvariantCulture))));

    private static IReadOnlyList<EntryPoint> DecodePoints(string text) =>
        [.. text.Split(';', StringSplitOptions.RemoveEmptyEntries).Select((item, i) =>
        {
            var f = item.Split(',');
            return new EntryPoint(
                i + 1,
                CandidateGrid.ParseDirection(f[0]),
                int.Parse(f[1], CultureInfo.InvariantCulture),
                int.Parse(f[2], CultureInfo.InvariantCulture),
                double.Parse(f[3], CultureInfo.InvariantCulture));
        })];

    private List<PointCandidate> ReadPointCandidates(string statement)
    {
        using var cmd = Command(statement);
        using var reader = cmd.ExecuteReader();
        var dir = Ordinal(reader, "direction");
        var entry = Ordinal(reader, "entry_min");
        var hold = Ordinal(reader, "hold_min");
        var score = Ordinal(reader, "score");

        var list = new List<PointCandidate>();
        while (reader.Read())
        {
            if (reader.IsDBNull(dir) || reader.IsDBNull(entry) || reader.IsDBNull(hold))
            {
                continue;
            }

            list.Add(new PointCandidate(
                CandidateGrid.ParseDirection(reader.GetString(dir)),
                Convert.ToInt32(reader.GetValue(entry), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(hold), CultureInfo.InvariantCulture),
                reader.IsDBNull(score) ? double.NaN : Convert.ToDouble(reader.GetValue(score), CultureInfo.InvariantCulture)));
        }

        return list;
    }

    /// <summary>ポイントの表示用に候補統計を引く。</summary>
    public Task<IReadOnlyList<CandidateSummary>> GetCandidateSummariesAsync(long runId, IEnumerable<EntryPoint> points) => RunAsync(() =>
    {
        var keys = points.Select(p => $"('{CandidateGrid.DirectionName(p.Direction)}', {p.EntryMinute}, {p.HoldMinutes})").ToList();
        if (keys.Count == 0)
        {
            return (IReadOnlyList<CandidateSummary>)[];
        }

        using var cmd = Command($"""
            SELECT c.direction, c.entry_min, c.hold_min, c.win_rate_avg, c.max_profit_base, c.profit_eff_avg, c.spread_avg
            FROM candidate_stats c
            JOIN (VALUES {string.Join(", ", keys)}) AS k(direction, entry_min, hold_min)
              ON c.direction = k.direction AND c.entry_min = k.entry_min AND c.hold_min = k.hold_min
            WHERE c.run_id = $run
            """);
        cmd.Parameters.Add(new DuckDBParameter("run", runId));
        using var reader = cmd.ExecuteReader();
        var list = new List<CandidateSummary>();
        while (reader.Read())
        {
            list.Add(new CandidateSummary(
                CandidateGrid.ParseDirection(reader.GetString(0)),
                reader.GetInt32(1),
                reader.GetInt32(2),
                NullableDouble(reader, 3),
                NullableDouble(reader, 4),
                NullableDouble(reader, 5),
                NullableDouble(reader, 6)));
        }

        return (IReadOnlyList<CandidateSummary>)list;
    });

    // ------------------------------------------------------------------

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

    private void Execute(string sql) => Execute(_connection, sql);

    private static void Execute(DuckDBConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static int Ordinal(IDataRecord reader, string name)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (reader.GetName(i).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new ModeSqlException($"SQL の結果に列 {name} がありません。direction, entry_min, hold_min, score を返してください。");
    }

    private static double NullableDouble(IDataRecord reader, int i) => reader.IsDBNull(i) ? double.NaN : reader.GetDouble(i);

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        await _connection.DisposeAsync();
        _gate.Dispose();
        await _market.DisposeAsync();
    }
}
