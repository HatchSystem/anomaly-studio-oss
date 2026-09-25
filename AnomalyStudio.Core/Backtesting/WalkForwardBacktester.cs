using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AnomalyStudio.Core.Analysis;
using AnomalyStudio.Core.Diagnostics;
using AnomalyStudio.Core.Modes;
using AnomalyStudio.Core.Storage;
using AnomalyStudio.Core.Symbols;
using AnomalyStudio.Core.Trading;

namespace AnomalyStudio.Core.Backtesting;

/// <summary>ウォークフォワード・バックテストの進捗（0〜1）と、処理中の説明。</summary>
public readonly record struct WalkForwardProgress(double Fraction, string Message);

/// <summary>1 回の取引と、そのポイントを選んだ分析の基準日・取引した銘柄（バックテスト共通）。</summary>
public sealed record BacktestTrade(DateOnly ReportDate, TradeResult Trade, string SymbolId);

/// <param name="Trades">取引日・ポイント順の取引。</param>
/// <param name="UnavailableReportDates">データ不足で分析できなかった基準日（その基準日を使う取引日は取引なし）。</param>
/// <param name="ComputedAnalyses">今回新しく計算した分析の数（残りは保存済みのポイントを使った）。</param>
public sealed record WalkForwardResult(
    IReadOnlyList<BacktestTrade> Trades,
    IReadOnlyList<DateOnly> UnavailableReportDates,
    int ComputedAnalyses);

/// <summary>
/// 取引日ごとに、評価サイクルで決まる基準日の分析結果から選んだポイントで取引を評価する。
/// 基準日の分析は基準日の 0:00（JST）より前の価格だけで計算する（日跨ぎ Close の先読みを防ぐため、通常の分析と違い基準日当日の価格は使わない）。
/// 分析結果は保存せず、選んだポイントだけを基準日・モードごとに保存して次回以降に使い回す。
/// <para>
/// 価格は 1 回だけ読んで検証期間全体の行列（<see cref="PriceMatrix.BuildRange"/>）を作り、基準日ごとに窓（<see cref="PriceMatrix.Window"/>）をずらして分析する。
/// 候補統計の入れ物も使い回すので、毎日サイクルで 365 回分析しても行列と統計を作り直さない。
/// </para>
/// </summary>
public sealed class WalkForwardBacktester(AnalysisDatabase database)
{
    /// <summary>分析対象の先頭と末尾で、足がこの幅だけ欠けていても（週末・年末年始）分析できるとみなす。</summary>
    private static readonly TimeSpan CoverageTolerance = TimeSpan.FromDays(4);

    /// <summary>ポイントの保存・再利用に使うキーの版。先読み防止など計算の前提を変えたら上げる。</summary>
    private const string CacheVersion = "wf1";

    /// <summary>検証期間のバックテストに必要な価格の範囲（最初の基準日−365 日 〜 検証期間の最終日の翌々日）。</summary>
    public static (DateTime FromUtc, DateTime ToUtc) RequiredRange(DateOnly from, DateOnly to, BacktestCycle cycle, int maxPeriod)
    {
        var firstReportDate = WalkForwardSchedule.ReportDateFor(from, cycle);
        return (Jst.StartOfDayUtc(firstReportDate.AddDays(-maxPeriod)), TradeEvaluator.RequiredRange(from, to).ToUtc);
    }

    /// <summary>保存したポイントのキー。計算方式・既定 spread・モードの SQL のどれかが変われば別のキーになる。</summary>
    public static string CacheKey(ModeDefinition mode, EngineParameters parameters, int limit = PointSelector.DefaultLimit)
    {
        var text = string.Join('|',
            CacheVersion,
            EngineParameters.LogicVersion,
            parameters.FallbackSpread.ToString("R", CultureInfo.InvariantCulture),
            limit.ToString(CultureInfo.InvariantCulture),
            mode.Sql.Trim());
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <param name="mode">評価するモード。</param>
    /// <param name="otherModes">新しく分析したときに、あわせてポイントを保存しておくモード（モードを切り替えたときに分析をやり直さないため）。</param>
    /// <param name="dataUntilUtc">取込済みの最終時刻。これより後の Close は「未確定」。</param>
    public async Task<WalkForwardResult> RunAsync(
        SymbolProfile symbol,
        ModeDefinition mode,
        IReadOnlyList<ModeDefinition> otherModes,
        DateOnly from,
        DateOnly to,
        BacktestCycle cycle,
        DateTime dataUntilUtc,
        IProgress<WalkForwardProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new EngineParameters { FallbackSpread = symbol.FallbackSpread };
        var modes = new List<ModeDefinition> { mode };
        modes.AddRange(otherModes.Where(m => m.Sql.Trim() != mode.Sql.Trim()).DistinctBy(m => m.Sql.Trim()));
        var keys = modes.Select(m => CacheKey(m, parameters)).ToArray();

        var reportDates = WalkForwardSchedule.ReportDates(from, to, cycle);
        var cached = await database.GetBacktestPointsAsync(symbol.Id, keys[0]);
        var points = reportDates.Where(cached.ContainsKey).ToDictionary(d => d, d => cached[d]);
        var missing = reportDates.Where(d => !points.ContainsKey(d)).ToList();

        var evaluationRange = TradeEvaluator.RequiredRange(from, to);
        var loadFromUtc = missing.Count > 0 ? RequiredRange(from, to, cycle, parameters.MaxPeriod).FromUtc : evaluationRange.FromUtc;
        var quotes = await database.LoadOpenQuotesAsync(symbol.Id, loadFromUtc, evaluationRange.ToUtc);

        var unavailable = new List<DateOnly>();
        var analyzer = missing.Count > 0 ? new SlidingAnalyzer(quotes, missing, parameters) : null;
        var stats = new Stats();
        for (var i = 0; i < missing.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reportDate = missing[i];
            progress?.Report(new WalkForwardProgress(
                (double)i / missing.Count, $"{reportDate:yyyy/MM/dd} 基準の分析（{i + 1} / {missing.Count}）"));

            if (await analyzer!.AnalyzeAsync(reportDate, stats, cancellationToken) is not { } table)
            {
                unavailable.Add(reportDate);
                continue;
            }

            var selected = await SelectPointsAsync(symbol.Id, table, modes, stats);
            for (var k = 0; k < selected.Count; k++)
            {
                if (selected[k] is { } p)
                {
                    await database.SaveBacktestPointsAsync(symbol.Id, reportDate, keys[k], p);
                }
            }

            points[reportDate] = selected[0]!;
        }

        stats.Report(missing.Count - unavailable.Count);
        progress?.Report(new WalkForwardProgress(1, $"{(to.DayNumber - from.DayNumber) + 1:N0} 日分の取引を評価"));
        using var evaluateTiming = Timing.Start("walkforward", "evaluate");
        var evaluator = new TradeEvaluator(
            quotes.Where(q => q.TimeUtc >= evaluationRange.FromUtc), dataUntilUtc, symbol.FallbackSpread);
        var trades = new List<BacktestTrade>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var reportDate = WalkForwardSchedule.ReportDateFor(day, cycle);
            if (points.TryGetValue(reportDate, out var dayPoints))
            {
                trades.AddRange(dayPoints.Select(p => new BacktestTrade(reportDate, evaluator.Evaluate(day, p), symbol.Id)));
            }
        }

        evaluateTiming.Detail = $"{trades.Count:N0} 取引";
        return new WalkForwardResult(trades, unavailable, missing.Count - unavailable.Count);
    }

    /// <summary>複合ポイントを保存・再利用するキー。銘柄の組み合わせ（と各銘柄の既定 spread）・指標・モードの SQL のどれかが変われば別のキーになる。</summary>
    public static string CompositeCacheKey(ModeDefinition mode, CompositeMetric metric, IReadOnlyList<SymbolProfile> symbols, int limit = PointSelector.DefaultLimit)
    {
        var text = string.Join('|',
            CacheVersion,
            "composite",
            EngineParameters.LogicVersion,
            metric.ToString(),
            limit.ToString(CultureInfo.InvariantCulture),
            string.Join(',', symbols.OrderBy(s => s.Id, StringComparer.Ordinal)
                .Select(s => s.Id + ":" + s.FallbackSpread.ToString("R", CultureInfo.InvariantCulture))),
            mode.Sql.Trim());
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>
    /// 複合ポイントのウォークフォワード・バックテスト。基準日ごとに全銘柄を分析し、銘柄をまたいで選んだポイントで各銘柄の取引を評価する。
    /// データが足りない銘柄はその基準日の候補から外し（その基準日のポイントは保存しない）、全銘柄が足りなければ取引なし。
    /// </summary>
    /// <param name="dataUntilUtc">銘柄ごとの取込済みの最終時刻。これより後の Close は「未確定」。</param>
    public async Task<WalkForwardResult> RunCompositeAsync(
        IReadOnlyList<SymbolProfile> symbols,
        ModeDefinition mode,
        DateOnly from,
        DateOnly to,
        BacktestCycle cycle,
        IReadOnlyDictionary<string, DateTime> dataUntilUtc,
        IProgress<WalkForwardProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var metric = mode.EffectiveCompositeMetric
            ?? throw new ArgumentException($"モード「{mode.Name}」は複合ポイントに対応していません。", nameof(mode));
        var key = CompositeCacheKey(mode, metric, symbols);
        var maxPeriod = new EngineParameters().MaxPeriod;

        var reportDates = WalkForwardSchedule.ReportDates(from, to, cycle);
        var cached = await database.GetCompositeBacktestPointsAsync(key);
        var points = reportDates.Where(cached.ContainsKey).ToDictionary(d => d, d => cached[d]);
        var missing = reportDates.Where(d => !points.ContainsKey(d)).ToList();

        var evaluationRange = TradeEvaluator.RequiredRange(from, to);
        var loadFromUtc = missing.Count > 0 ? RequiredRange(from, to, cycle, maxPeriod).FromUtc : evaluationRange.FromUtc;
        var quotes = new Dictionary<string, IReadOnlyList<OpenQuote>>();
        var analyzers = new Dictionary<string, SlidingAnalyzer>();
        foreach (var symbol in symbols)
        {
            quotes[symbol.Id] = await database.LoadOpenQuotesAsync(symbol.Id, loadFromUtc, evaluationRange.ToUtc);
            if (missing.Count > 0)
            {
                analyzers[symbol.Id] = new SlidingAnalyzer(quotes[symbol.Id], missing, new EngineParameters { FallbackSpread = symbol.FallbackSpread });
            }
        }

        var unavailable = new List<DateOnly>();
        var stats = new Stats();
        for (var i = 0; i < missing.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reportDate = missing[i];
            progress?.Report(new WalkForwardProgress(
                (double)i / missing.Count, $"{reportDate:yyyy/MM/dd} 基準の分析（全銘柄、{i + 1} / {missing.Count}）"));

            var tables = new List<(string SymbolId, CandidateTable Table)>();
            foreach (var symbol in symbols)
            {
                if (await analyzers[symbol.Id].AnalyzeAsync(reportDate, stats, cancellationToken) is { } table)
                {
                    tables.Add((symbol.Id, table));
                }
            }

            if (tables.Count == 0)
            {
                unavailable.Add(reportDate);
                continue;
            }

            var selected = await SelectCompositePointsAsync(tables, mode, metric, stats);
            if (tables.Count == symbols.Count)
            {
                await database.SaveCompositeBacktestPointsAsync(reportDate, key, selected);
            }

            points[reportDate] = selected;
        }

        stats.Report(missing.Count - unavailable.Count);
        progress?.Report(new WalkForwardProgress(1, $"{(to.DayNumber - from.DayNumber) + 1:N0} 日分の取引を評価"));
        using var evaluateTiming = Timing.Start("walkforward", "evaluate");
        var evaluators = symbols.ToDictionary(
            s => s.Id,
            s => new TradeEvaluator(
                quotes[s.Id].Where(q => q.TimeUtc >= evaluationRange.FromUtc), dataUntilUtc.GetValueOrDefault(s.Id, DateTime.MinValue), s.FallbackSpread));
        var trades = new List<BacktestTrade>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var reportDate = WalkForwardSchedule.ReportDateFor(day, cycle);
            if (points.TryGetValue(reportDate, out var dayPoints))
            {
                trades.AddRange(dayPoints
                    .Where(p => evaluators.ContainsKey(p.SymbolId))
                    .Select(p => new BacktestTrade(reportDate, evaluators[p.SymbolId].Evaluate(day, p.Point), p.SymbolId)));
            }
        }

        evaluateTiming.Detail = $"{trades.Count:N0} 取引";
        return new WalkForwardResult(trades, unavailable, missing.Count - unavailable.Count);
    }

    /// <summary>
    /// 基準日ごとのポイント抽出。すべてのモードが既定の SQL（<see cref="ModeDefinition.BuiltInScoreColumn"/>）なら、
    /// 80,640 行を DuckDB に書かずに候補統計から直接選ぶ（結果は SQL と同じ。毎日サイクルの初回で最も時間がかかる部分）。
    /// ユーザーが編集した SQL を含むときは DuckDB で実行する。
    /// </summary>
    private async Task<IReadOnlyList<IReadOnlyList<EntryPoint>?>> SelectPointsAsync(
        string symbolId, CandidateTable table, IReadOnlyList<ModeDefinition> modes, Stats stats)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var columns = modes.Select(m => ModeDefinition.BuiltInScoreColumn(m.Sql)).ToArray();
            if (columns.All(c => c is not null))
            {
                // モードごとの抽出は独立なので並行して行う
                return await Task.Run(() =>
                {
                    var results = new IReadOnlyList<EntryPoint>?[columns.Length];
                    Parallel.For(0, columns.Length, k => results[k] = PointSelector.SelectBuiltIn(table, columns[k]!));
                    return (IReadOnlyList<IReadOnlyList<EntryPoint>?>)results;
                });
            }

            return await database.SelectPointsWithoutSavingAsync(symbolId, table, [.. modes.Select(m => m.Sql)]);
        }
        finally
        {
            stats.Select += Stopwatch.GetElapsedTime(started);
        }
    }

    /// <summary>複合ポイントの抽出。既定の SQL なら候補統計から直接、そうでなければ DuckDB で（<see cref="SelectPointsAsync"/> と同じ方針）。</summary>
    private async Task<IReadOnlyList<SymbolPoint>> SelectCompositePointsAsync(
        IReadOnlyList<(string SymbolId, CandidateTable Table)> tables, ModeDefinition mode, CompositeMetric metric, Stats stats)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (ModeDefinition.BuiltInScoreColumn(mode.Sql) is { } column)
            {
                var valueColumn = ModeSql.CompositeColumn(metric);
                return await Task.Run(() => CompositePointSelector.Select(
                    tables.ToDictionary(t => t.SymbolId, t => PointSelector.BuiltInCandidates(t.Table, column, valueColumn))));
            }

            return await database.SelectCompositePointsWithoutSavingAsync(tables, mode.Sql, metric);
        }
        finally
        {
            stats.Select += Stopwatch.GetElapsedTime(started);
        }
    }

    /// <summary>基準日ごとの分析・抽出にかかった時間の合計（計測ログ用）。</summary>
    private sealed class Stats
    {
        public TimeSpan Matrix { get; set; }

        public TimeSpan Compute { get; set; }

        public TimeSpan Select { get; set; }

        public int Analyses { get; set; }

        public void Report(int computed)
        {
            if (Analyses == 0)
            {
                return;
            }

            Timing.Record("walkforward", "matrix", Matrix);
            Timing.Record("walkforward", "compute", Compute, $"{Analyses} 回, 平均 {Compute.TotalMilliseconds / Analyses:0} ms");
            Timing.Record("walkforward", "select", Select, $"{computed} 回, 平均 {(computed > 0 ? Select.TotalMilliseconds / computed : 0):0} ms");
        }
    }

    /// <summary>
    /// 1 銘柄の連続する基準日の分析。価格の行列は必要な範囲を 1 回だけ作り、基準日ごとに窓をずらす。
    /// 候補統計の入れ物は前の基準日のものを使い回す（呼び出し側は次の基準日を分析する前に結果を使い終えること）。
    /// </summary>
    private sealed class SlidingAnalyzer
    {
        private readonly IReadOnlyList<OpenQuote> _quotes;
        private readonly EngineParameters _parameters;
        private readonly Lazy<PriceMatrix> _matrix;
        private readonly Stopwatch _matrixTime = new();
        private CandidateTable? _table;

        public SlidingAnalyzer(IReadOnlyList<OpenQuote> quotes, IReadOnlyList<DateOnly> reportDates, EngineParameters parameters)
        {
            _quotes = quotes;
            _parameters = parameters;
            var firstDay = reportDates.Min().AddDays(-parameters.MaxPeriod);
            var days = reportDates.Max().DayNumber - firstDay.DayNumber + 1;
            _matrix = new Lazy<PriceMatrix>(() =>
            {
                _matrixTime.Start();
                var matrix = PriceMatrix.BuildRange(firstDay, days, quotes);
                _matrixTime.Stop();
                return matrix;
            });
        }

        /// <summary>基準日の 0:00 より前の 365 日で分析する。先頭か末尾で足が大きく欠けていれば null。</summary>
        public async Task<CandidateTable?> AnalyzeAsync(DateOnly reportDate, Stats stats, CancellationToken cancellationToken)
        {
            var startUtc = Jst.StartOfDayUtc(reportDate.AddDays(-_parameters.MaxPeriod));
            var endUtc = Jst.StartOfDayUtc(reportDate);
            var lo = LowerBound(_quotes, startUtc);
            var hi = LowerBound(_quotes, endUtc);
            if (hi <= lo || _quotes[lo].TimeUtc > startUtc + CoverageTolerance || _quotes[hi - 1].TimeUtc < endUtc - CoverageTolerance)
            {
                return null;
            }

            var started = Stopwatch.GetTimestamp();
            var matrixBefore = _matrixTime.Elapsed;
            _table = await Task.Run(
                () => AnomalyEngine.Compute(_matrix.Value.Window(reportDate, _parameters.MaxPeriod, excludeReportDate: true), _parameters, reuse: _table),
                cancellationToken);
            stats.Matrix += _matrixTime.Elapsed - matrixBefore;
            stats.Compute += Stopwatch.GetElapsedTime(started) - (_matrixTime.Elapsed - matrixBefore);
            stats.Analyses++;
            return _table;
        }
    }

    /// <summary>時刻順の足で、<paramref name="timeUtc"/> 以降の最初の位置。</summary>
    private static int LowerBound(IReadOnlyList<OpenQuote> quotes, DateTime timeUtc)
    {
        int lo = 0, hi = quotes.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (quotes[mid].TimeUtc < timeUtc)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
