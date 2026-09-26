using AnomalyStudio.Core;
using AnomalyStudio.Core.Analysis;
using AnomalyStudio.Core.Diagnostics;
using AnomalyStudio.Core.MarketData;
using AnomalyStudio.Core.Modes;
using AnomalyStudio.Core.Storage;
using AnomalyStudio.Core.Symbols;
using AnomalyStudio.Core.Trading;

namespace AnomalyStudio.Services;

/// <summary>
/// 銘柄ごとの取込状況と最新の分析結果。<see cref="LatestRun"/> は今の計算方式で計算したものだけで、
/// 更新で計算方式が変わる前の結果しかないときは null にして <see cref="NeedsReanalysis"/> を立てる（古い計算の結果を画面に出さない）。
/// </summary>
public sealed record SymbolStatus(
    SymbolProfile Symbol, long BarCount, DateTime? FirstBarUtc, DateTime? LastBarUtc, AnalysisRun? LatestRun, bool NeedsReanalysis = false);

/// <summary>
/// 取込・分析・ポイント抽出・エントリー展開をまとめる。画面はこのクラスを通して Core を使う。
/// UI スレッドから呼び出す前提で、完了後の状態更新と <see cref="Changed"/> は UI スレッドで起きる。
/// </summary>
public sealed partial class AnomalyWorkspace : ObservableObject
{
    private readonly AnalysisDatabase _database;
    private readonly MarketDataIngestor _ingestor;
    private readonly AnalysisRunner _runner;
    private readonly PointService _points;
    private readonly QuoteCache _quotes;
    private readonly AppSettings _settings;
    private readonly ILogger<AnomalyWorkspace> _logger;
    private CancellationTokenSource? _cts;

    public AnomalyWorkspace(
        AppPaths paths,
        AnalysisDatabase database,
        IMarketDataSource source,
        SymbolRepository symbols,
        ModeRepository modes,
        AppSettings settings,
        ILogger<AnomalyWorkspace> logger)
    {
        Paths = paths;
        _database = database;
        _settings = settings;
        _logger = logger;
        Symbols = symbols;
        Modes = modes;
        _ingestor = new MarketDataIngestor(source, database);
        _runner = new AnalysisRunner(database);
        _points = new PointService(database);
        _quotes = new QuoteCache(database);
        database.Compacted += sizes => _logger.LogInformation("分析結果の DB を作り直して縮めました: {Before:N0} → {After:N0} バイト", sizes.Before, sizes.After);

        // イベントハンドラーで例外が漏れるとプロセスごと落ちるので、ログに残して画面のメッセージにする
        modes.Changed += (_, _) => _ = GuardAsync(ReloadPointsAsync, "ポイントの読み直し");
        symbols.Changed += (_, _) => _ = GuardAsync(RefreshAsync, "銘柄の読み直し");
    }

    /// <summary>UI のイベントから呼ぶ非同期処理。例外はログと <see cref="LastMessage"/> に残し、伝播させない。</summary>
    private async Task GuardAsync(Func<Task> action, string what)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{What}に失敗しました", what);
            LastMessage = $"{what}に失敗しました: {ex.Message}";
            LastFailed = true;
        }
    }

    public AppPaths Paths { get; }

    public SymbolRepository Symbols { get; }

    public ModeRepository Modes { get; }

    /// <summary>取込・分析・ポイントのいずれかが更新された。</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<SymbolStatus> Statuses { get; private set; } = [];

    /// <summary>有効な銘柄 × 有効なモードのポイントと、有効なモードの複合ポイント（最新の run から抽出）。</summary>
    public IReadOnlyList<PointInfo> Points { get; private set; } = [];

    public bool HasAnalysis => Statuses.Any(s => s.LatestRun is not null);

    /// <summary>計算方式が変わる前の分析結果しかない銘柄がある（再分析が必要）。</summary>
    public bool NeedsReanalysis => Statuses.Any(s => s.Symbol.Enabled && s.NeedsReanalysis);

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string BusyMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double BusyFraction { get; set; }

    [ObservableProperty]
    public partial string LastMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool LastFailed { get; set; }

    /// <summary>保存済みの取込状況・分析結果を読み込み、ポイントを抽出する。</summary>
    public async Task RefreshAsync()
    {
        using var timing = Timing.Start("ui", "refresh");
        var statuses = new List<SymbolStatus>();
        foreach (var symbol in Symbols.All)
        {
            var (count, first, last) = await _database.GetMarketCoverageAsync(symbol.Id);
            var latest = await _database.GetLatestRunAsync(symbol.Id);
            var current = latest is { IsCurrent: true } ? latest : null;
            statuses.Add(new SymbolStatus(symbol, count, first, last, current, NeedsReanalysis: latest is not null && current is null));
        }

        Statuses = statuses;
        await ReloadPointsAsync();
    }

    private async Task ReloadPointsAsync()
    {
        using var timing = Timing.Start("ui", "reload-points");
        var points = new List<PointInfo>();
        foreach (var status in Statuses.Where(s => s.Symbol.Enabled && s.LatestRun is not null))
        {
            foreach (var mode in Modes.Enabled)
            {
                try
                {
                    var selected = await _points.GetPointsAsync(status.LatestRun!.RunId, mode);
                    var summaries = (await _database.GetCandidateSummariesAsync(status.LatestRun.RunId, selected))
                        .ToDictionary(s => (s.Direction, s.EntryMinute, s.HoldMinutes));
                    points.AddRange(selected.Select(p => new PointInfo(
                        status.Symbol, mode, p, summaries.GetValueOrDefault((p.Direction, p.EntryMinute, p.HoldMinutes)))));
                }
                catch (Exception ex) when (ex is ModeSqlException or DuckDB.NET.Data.DuckDBException)
                {
                    // 不正なモード SQL は他のモードの表示を妨げない
                    _logger.LogWarning(ex, "モード {Mode} の SQL を実行できません", mode.Name);
                    LastMessage = $"モード「{mode.Name}」の SQL を実行できません: {ex.Message}";
                    LastFailed = true;
                }
            }
        }

        // 複合ポイント: 有効な銘柄の最新の分析をまとめ、銘柄をまたいで選ぶ（対応する指標があるモードだけ）
        _compositeCache.Clear();
        points.AddRange(await BuildCompositePointsAsync(LatestRuns()));

        Points = points;
        timing.Detail = $"{points.Count} ポイント";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>銘柄の組み合わせごとの複合ポイント（分析結果やモードが変わって読み直すまで使い回す）。</summary>
    private readonly Dictionary<string, IReadOnlyList<PointInfo>> _compositeCache = [];

    /// <summary>
    /// 選んだ銘柄だけで作る複合ポイント（有効なモードのうち複合に対応するもの）。
    /// 最適化確認の複合ポイントと同じく、各銘柄の最新の分析から銘柄をまたいで選ぶ。
    /// </summary>
    public async Task<IReadOnlyList<PointInfo>> GetCompositePointsAsync(IReadOnlyList<SymbolProfile> symbols)
    {
        var key = string.Join(",", symbols.Select(s => s.Id).Order(StringComparer.Ordinal));
        if (!_compositeCache.TryGetValue(key, out var points))
        {
            var latest = Statuses.Where(s => s.LatestRun is not null && symbols.Any(x => x.Id == s.Symbol.Id)).ToList();
            points = await BuildCompositePointsAsync(latest);
            _compositeCache[key] = points;
        }

        return points;
    }

    private async Task<List<PointInfo>> BuildCompositePointsAsync(IReadOnlyList<SymbolStatus> latest)
    {
        var points = new List<PointInfo>();
        if (latest.Count == 0)
        {
            return points;
        }

        foreach (var mode in Modes.Enabled.Where(m => m.EffectiveCompositeMetric is not null))
        {
            try
            {
                var selected = await SelectCompositeAsync(mode, latest);
                foreach (var group in selected.GroupBy(p => p.SymbolId))
                {
                    var status = latest.First(s => s.Symbol.Id == group.Key);
                    var summaries = (await _database.GetCandidateSummariesAsync(status.LatestRun!.RunId, group.Select(p => p.Point)))
                        .ToDictionary(s => (s.Direction, s.EntryMinute, s.HoldMinutes));
                    points.AddRange(group.Select(p => new PointInfo(
                        status.Symbol, mode, p.Point, summaries.GetValueOrDefault((p.Point.Direction, p.Point.EntryMinute, p.Point.HoldMinutes)), IsComposite: true)));
                }
            }
            catch (Exception ex) when (ex is ModeSqlException or DuckDB.NET.Data.DuckDBException)
            {
                LastMessage = $"モード「{mode.Name}」の複合ポイントを作れません: {ex.Message}";
                LastFailed = true;
            }
        }

        return points;
    }

    /// <summary>有効で、分析結果がある銘柄。</summary>
    private List<SymbolStatus> LatestRuns() => [.. Statuses.Where(s => s.Symbol.Enabled && s.LatestRun is not null)];

    /// <summary>各銘柄の最新の分析から、モードの複合ポイントを選ぶ。</summary>
    private async Task<IReadOnlyList<SymbolPoint>> SelectCompositeAsync(ModeDefinition mode, IReadOnlyList<SymbolStatus> latest)
    {
        var metric = mode.EffectiveCompositeMetric!.Value;
        var candidates = new Dictionary<string, IReadOnlyList<PointCandidate>>();
        foreach (var status in latest)
        {
            candidates[status.Symbol.Id] = await _database.QueryCompositeCandidatesAsync(status.LatestRun!.RunId, mode.Sql, metric);
        }

        return CompositePointSelector.Select(candidates);
    }

    /// <summary>初回の取込の開始（365 日評価 + 1 日の余裕）。</summary>
    private static DateTime InitialIngestFromUtc => PriceMatrix.RequiredRange(Jst.Today, 365).FromUtc.AddDays(-1);

    /// <summary>有効な銘柄の未取得の 1 分足を取り込む（数銘柄ずつ並行。1 銘柄が失敗しても他の銘柄は続ける）。</summary>
    public Task UpdateMarketDataAsync() => RunBusyAsync("データを取り込み中", async ct =>
    {
        var outcomes = await IngestAllAsync(Symbols.Enabled, ct, weight: 1.0, offset: 0);
        return IngestSummary(outcomes, "のデータを更新しました");
    });

    /// <summary>取込のあと、有効な銘柄を基準日（今日）で分析する。取込や分析に失敗した銘柄があっても他の銘柄は続ける。</summary>
    public Task RunAnalysisAsync() => RunBusyAsync("分析を実行中", async ct =>
    {
        const double ingestWeight = 0.5;
        var symbols = Symbols.Enabled;
        var reportDate = Jst.Today;
        _runner.RunsToKeep = _settings.RunsToKeep;

        var outcomes = await IngestAllAsync(symbols, ct, weight: ingestWeight, offset: 0);
        var failures = outcomes.Where(o => o.Error is not null).Select(o => $"{o.SymbolId}（{o.Error!.Message}）").ToList();
        var analyzed = new List<string>();
        for (var i = 0; i < symbols.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (symbol, index) = (symbols[i], i);
            BusyMessage = $"{symbol.Id}: 172,800 候補を検証中";
            try
            {
                await _runner.RunAsync(symbol, reportDate, new Progress<double>(f => BusyFraction = ingestWeight + ((1 - ingestWeight) * (index + f) / symbols.Count)), ct);
                analyzed.Add(symbol.Id);
            }
            catch (Exception ex) when (ex is InsufficientDataException or IOException or DuckDB.NET.Data.DuckDBException)
            {
                _logger.LogWarning(ex, "{Symbol} の分析が失敗しました", symbol.Id);
                failures.Add($"{symbol.Id}（{ex.Message}）");
            }
        }

        var message = analyzed.Count > 0
            ? $"{reportDate:yyyy/MM/dd} 基準で {string.Join("・", analyzed)} を分析しました"
            : "分析できた銘柄がありません";
        return failures.Count == 0
            ? new BusyOutcome(message)
            : new BusyOutcome($"{message}。失敗: {string.Join("、", failures)}", Failed: true);
    });

    /// <summary>有効な銘柄を数銘柄ずつ並行して取り込む。進捗は <paramref name="offset"/> から <paramref name="weight"/> の幅に割り当てる。</summary>
    private Task<IReadOnlyList<IngestOutcome>> IngestAllAsync(IReadOnlyList<SymbolProfile> symbols, CancellationToken ct, double weight, double offset) =>
        _ingestor.IngestAllAsync(symbols, InitialIngestFromUtc, new Progress<IngestProgress>(p =>
        {
            BusyMessage = p.Message;
            BusyFraction = offset + (weight * p.Fraction);
        }), ct);

    private static BusyOutcome IngestSummary(IReadOnlyList<IngestOutcome> outcomes, string suffix)
    {
        var ok = outcomes.Where(o => o.Error is null).Select(o => o.SymbolId).ToList();
        var failures = outcomes.Where(o => o.Error is not null).Select(o => $"{o.SymbolId}（{o.Error!.Message}）").ToList();
        var message = ok.Count > 0 ? $"{string.Join("・", ok)} {suffix}" : "取り込めた銘柄がありません";
        return failures.Count == 0
            ? new BusyOutcome(message)
            : new BusyOutcome($"{message}。失敗: {string.Join("、", failures)}", Failed: true);
    }

    /// <summary>実行の結果の説明と、失敗として表示するか。</summary>
    private sealed record BusyOutcome(string Message, bool Failed = false)
    {
        public static implicit operator BusyOutcome(string message) => new(message);
    }

    private async Task RunBusyAsync(string title, Func<CancellationToken, Task<BusyOutcome>> action)
    {
        if (IsBusy)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        IsBusy = true;
        BusyMessage = title;
        BusyFraction = 0;
        try
        {
            var outcome = await action(_cts.Token);
            LastMessage = outcome.Message;
            LastFailed = outcome.Failed;
            if (outcome.Failed)
            {
                _logger.LogWarning("{Title}: {Message}", title, outcome.Message);
            }
        }
        catch (OperationCanceledException)
        {
            LastMessage = "中止しました";
            LastFailed = true;
        }
        catch (Exception ex) when (ex is InsufficientDataException or HttpRequestException or IOException or DuckDB.NET.Data.DuckDBException)
        {
            _logger.LogWarning(ex, "{Title}が失敗しました", title);
            LastMessage = ex.Message;
            LastFailed = true;
        }
        catch (Exception ex)
        {
            // 想定外の例外でもアプリを落とさず、原因をログに残す
            _logger.LogError(ex, "{Title}で予期しないエラーが起きました", title);
            LastMessage = $"予期しないエラー: {ex.Message}（詳細は logs フォルダー）";
            LastFailed = true;
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }

        await RefreshAsync();
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>実行中のウォークフォワード・バックテストの数（取込・分析の <see cref="IsBusy"/> とは別に数える）。</summary>
    private int _backtests;

    /// <summary>取込・分析・ウォークフォワードのどれかを実行中（アプリの再起動で中断しない）。</summary>
    public bool IsWorking => IsBusy || Volatile.Read(ref _backtests) > 0;

    /// <summary>アプリを終了・再起動する前に、分析結果の DB への書き込みを反映する。</summary>
    public Task PrepareForExitAsync() => _database.CheckpointAsync();

    private async Task<T> TrackBacktestAsync<T>(Func<Task<T>> action)
    {
        Interlocked.Increment(ref _backtests);
        try
        {
            return await action();
        }
        finally
        {
            Interlocked.Decrement(ref _backtests);
        }
    }

    /// <summary>データの初期化（1 分足・分析結果・バックテスト用のポイントを消す）。取込や分析の実行中はしない。</summary>
    public Task ResetDataAsync() => RunBusyAsync("データを初期化中", async _ =>
    {
        await _database.ResetAsync();
        return "データを初期化しました（1 分足・分析結果・バックテスト用のポイントを削除）。「分析を実行」で取り込み直せます";
    });

    /// <summary>現在時刻の前後 24 時間に重なるエントリーを、結果付きで時刻順に返す。<paramref name="composite"/> は複合ポイントのエントリー。</summary>
    public Task<IReadOnlyList<EntryItem>> GetEntriesAsync(DateTime nowJst, bool composite = false) =>
        GetEntriesAsync(nowJst, [.. Points.Where(p => p.IsComposite == composite)]);

    /// <summary>現在時刻の前後 24 時間に重なる、指定したポイントのエントリーを、結果付きで時刻順に返す。</summary>
    public async Task<IReadOnlyList<EntryItem>> GetEntriesAsync(DateTime nowJst, IReadOnlyList<PointInfo> points)
    {
        using var timing = Timing.Start("ui", "entries-upcoming");
        var today = DateOnly.FromDateTime(nowJst);
        var items = new List<EntryItem>();
        foreach (var group in points.GroupBy(p => p.Symbol.Id))
        {
            var symbol = group.First().Symbol;
            var evaluator = await CreateEvaluatorAsync(symbol, today.AddDays(-1), today.AddDays(1));
            foreach (var info in group)
            {
                foreach (var day in new[] { today.AddDays(-1), today, today.AddDays(1) })
                {
                    var trade = evaluator.Evaluate(day, info.Point);
                    if (trade.IsInMarketHours && trade.CloseJst > nowJst.AddHours(-24) && trade.EntryJst < nowJst.AddHours(24))
                    {
                        items.Add(new EntryItem(info, trade));
                    }
                }
            }
        }

        timing.Detail = $"{items.Count} 件";
        return [.. items.OrderBy(i => i.EntryJst).ThenBy(i => i.Info.Symbol.Id).ThenBy(i => i.Info.Mode.Name)];
    }

    /// <summary>
    /// 指定日（JST、Entry の日付）のエントリーを、結果付きで時刻順に返す。過去・未来の日も最新の分析結果のポイントを当てはめる。
    /// ポイントがなければ空。<paramref name="composite"/> は複合ポイントのエントリー。
    /// </summary>
    public Task<IReadOnlyList<EntryItem>> GetEntriesForDayAsync(DateOnly day, bool composite = false) =>
        GetEntriesForDayAsync(day, [.. Points.Where(p => p.IsComposite == composite)]);

    /// <summary>指定日（JST、Entry の日付）に、指定したポイントを展開したエントリーを、結果付きで時刻順に返す。</summary>
    public async Task<IReadOnlyList<EntryItem>> GetEntriesForDayAsync(DateOnly day, IReadOnlyList<PointInfo> points)
    {
        using var timing = Timing.Start("ui", "entries-day");
        var items = new List<EntryItem>();
        foreach (var group in points.GroupBy(p => p.Symbol.Id))
        {
            var evaluator = await CreateEvaluatorAsync(group.First().Symbol, day, day);
            items.AddRange(group.Select(info => new EntryItem(info, evaluator.Evaluate(day, info.Point))).Where(i => i.Trade.IsInMarketHours));
        }

        timing.Detail = $"{day:yyyy/MM/dd} {items.Count} 件";
        return [.. items.OrderBy(i => i.EntryJst).ThenBy(i => i.Info.Symbol.Id).ThenBy(i => i.Info.Mode.Name)];
    }

    /// <summary>最適化確認: 最新の分析結果のポイントを、過去の各日に当てはめて結果を計算する。</summary>
    public async Task<IReadOnlyList<BacktestTrade>> CheckOptimizationAsync(SymbolProfile symbol, ModeDefinition mode, DateOnly from, DateOnly to)
    {
        var status = Statuses.FirstOrDefault(s => s.Symbol.Id == symbol.Id);
        if (status?.LatestRun is not { } run)
        {
            return [];
        }

        var points = await _points.GetPointsAsync(run.RunId, mode);
        var evaluator = await CreateEvaluatorAsync(symbol, from, to);
        return await Task.Run(() => evaluator.EvaluateRange(from, to, points).Select(t => new BacktestTrade(run.ReportDate, t, symbol.Id)).ToList());
    }

    /// <summary>
    /// 最適化確認（複合ポイント）: 選んだ銘柄の最新の分析から選んだ複合ポイントを、過去の各日に当てはめる。
    /// 分析基準日は銘柄ごとに違いうるので、最も新しい基準日を代表として使う。分析結果がなければ空。
    /// </summary>
    public async Task<(IReadOnlyList<BacktestTrade> Trades, DateOnly? ReportDate)> CheckOptimizationCompositeAsync(
        IReadOnlyList<SymbolProfile> symbols, ModeDefinition mode, DateOnly from, DateOnly to)
    {
        var latest = Statuses.Where(s => s.LatestRun is not null && symbols.Any(x => x.Id == s.Symbol.Id)).ToList();
        if (latest.Count == 0)
        {
            return ([], null);
        }

        var selected = await SelectCompositeAsync(mode, latest);
        var reportDate = latest.Max(s => s.LatestRun!.ReportDate);
        var trades = new List<BacktestTrade>();
        foreach (var group in selected.GroupBy(p => p.SymbolId))
        {
            var symbol = latest.First(s => s.Symbol.Id == group.Key).Symbol;
            var evaluator = await CreateEvaluatorAsync(symbol, from, to);
            var points = group.Select(p => p.Point).ToList();
            trades.AddRange(await Task.Run(() => evaluator.EvaluateRange(from, to, points).Select(t => new BacktestTrade(reportDate, t, symbol.Id)).ToList()));
        }

        return ([.. trades.OrderBy(t => t.Trade.EntryJst)], reportDate);
    }

    /// <summary>
    /// ウォークフォワード・バックテスト（複合ポイント）: 選んだ銘柄それぞれの足りない過去の 1 分足を取り込んでから、
    /// 基準日ごとに選んだ銘柄の分析から選んだ複合ポイントで評価する。
    /// </summary>
    public Task<WalkForwardResult> WalkForwardCompositeAsync(
        IReadOnlyList<SymbolProfile> symbols,
        ModeDefinition mode,
        DateOnly from,
        DateOnly to,
        BacktestCycle cycle,
        IProgress<WalkForwardProgress> progress,
        CancellationToken cancellationToken) =>
        TrackBacktestAsync(() => WalkForwardCompositeCoreAsync(symbols, mode, from, to, cycle, progress, cancellationToken));

    private async Task<WalkForwardResult> WalkForwardCompositeCoreAsync(
        IReadOnlyList<SymbolProfile> symbols,
        ModeDefinition mode,
        DateOnly from,
        DateOnly to,
        BacktestCycle cycle,
        IProgress<WalkForwardProgress> progress,
        CancellationToken cancellationToken)
    {
        const double ingestWeight = 0.2;
        var (fromUtc, _) = WalkForwardBacktester.RequiredRange(from, to, cycle, new EngineParameters().MaxPeriod);
        var added = 0;
        for (var i = 0; i < symbols.Count; i++)
        {
            var index = i;
            added += await _ingestor.BackfillAsync(symbols[i], fromUtc,
                new Progress<IngestProgress>(p => progress.Report(new WalkForwardProgress(
                    ingestWeight * (index + p.Fraction) / symbols.Count, p.Message))),
                cancellationToken);
        }

        if (added > 0)
        {
            await RefreshAsync();
        }

        var until = symbols.ToDictionary(s => s.Id, s => Statuses.FirstOrDefault(x => x.Symbol.Id == s.Id)?.LastBarUtc ?? DateTime.MinValue);
        return await new WalkForwardBacktester(_database).RunCompositeAsync(
            symbols, mode, from, to, cycle, until,
            new Progress<WalkForwardProgress>(p => progress.Report(p with { Fraction = ingestWeight + ((1 - ingestWeight) * p.Fraction) })),
            cancellationToken);
    }

    /// <summary>
    /// ウォークフォワード・バックテスト: 足りない過去の 1 分足を取り込んでから、取引日ごとに評価サイクルで決まる時点の分析結果で評価する。
    /// </summary>
    public Task<WalkForwardResult> WalkForwardAsync(
        SymbolProfile symbol,
        ModeDefinition mode,
        DateOnly from,
        DateOnly to,
        BacktestCycle cycle,
        IProgress<WalkForwardProgress> progress,
        CancellationToken cancellationToken) =>
        TrackBacktestAsync(() => WalkForwardCoreAsync(symbol, mode, from, to, cycle, progress, cancellationToken));

    private async Task<WalkForwardResult> WalkForwardCoreAsync(
        SymbolProfile symbol,
        ModeDefinition mode,
        DateOnly from,
        DateOnly to,
        BacktestCycle cycle,
        IProgress<WalkForwardProgress> progress,
        CancellationToken cancellationToken)
    {
        // 進捗は取込を 0〜20%、分析と評価を 20〜100% に割り当てる
        const double ingestWeight = 0.2;
        var (fromUtc, _) = WalkForwardBacktester.RequiredRange(from, to, cycle, new EngineParameters().MaxPeriod);
        var added = await _ingestor.BackfillAsync(symbol, fromUtc,
            new Progress<IngestProgress>(p => progress.Report(new WalkForwardProgress(ingestWeight * p.Fraction, p.Message))), cancellationToken);
        if (added > 0)
        {
            await RefreshAsync();
        }

        var until = Statuses.FirstOrDefault(s => s.Symbol.Id == symbol.Id)?.LastBarUtc ?? DateTime.MinValue;
        return await new WalkForwardBacktester(_database).RunAsync(
            symbol, mode, Modes.Enabled, from, to, cycle, until,
            new Progress<WalkForwardProgress>(p => progress.Report(p with { Fraction = ingestWeight + ((1 - ingestWeight) * p.Fraction) })),
            cancellationToken);
    }

    /// <summary>モードの SQL の文法チェック（分析結果がなくてもできる）。誤りは ModeSqlException か DuckDB の例外。</summary>
    public Task ValidateModeSqlAsync(string sql) => _database.ValidateModeSqlAsync(sql);

    /// <summary>テスト抽出できる（有効な銘柄に分析結果がある）。</summary>
    public bool CanTestModeSql => Statuses.Any(s => s.Symbol.Enabled && s.LatestRun is not null);

    /// <summary>モードの SQL を銘柄ごとの最新 run で試し、抽出されるポイント数を返す。</summary>
    public async Task<string> TestModeSqlAsync(string sql)
    {
        var results = new List<string>();
        foreach (var status in Statuses.Where(s => s.Symbol.Enabled && s.LatestRun is not null))
        {
            var candidates = await _database.QueryPointCandidatesAsync(status.LatestRun!.RunId, sql);
            results.Add($"{status.Symbol.Id}: {PointSelector.SelectNonOverlapping(candidates).Count} 件（候補 {candidates.Count:N0}）");
        }

        return results.Count == 0 ? "分析結果がありません。先に分析を実行してください" : string.Join(" / ", results);
    }

    public async Task RemoveSymbolDataAsync(string symbolId) => await _database.DeleteMarketDataAsync(symbolId);

    /// <summary>
    /// ランダム基準: 確定した取引と同じ取引日・件数・取引時間帯で、ポイントを無作為に選んだ場合の合計損益の分布（表示単位）。
    /// 複数の銘柄が混ざる（複合ポイント）ときは、ポイントごとに銘柄も無作為に選ぶ。取引がなければ null。
    /// <paramref name="holds"/> はモードが限る保有時間（勝率重視BO）。null は既定の 3〜15 分。
    /// </summary>
    public async Task<BaselineResult?> ComputeBaselineAsync(
        IReadOnlyList<BacktestTrade> settledTrades, PointFilter filter, IReadOnlyList<int>? holds, CancellationToken cancellationToken)
    {
        var profiles = settledTrades.Select(t => t.SymbolId).Distinct()
            .Select(Symbols.Find).OfType<SymbolProfile>().ToDictionary(s => s.Id);
        var settled = settledTrades.Where(t => t.Trade.Status == TradeStatus.Settled && profiles.ContainsKey(t.SymbolId)).ToList();
        if (settled.Count == 0)
        {
            return null;
        }

        var from = settled.Min(t => t.Trade.TradeDate);
        var to = settled.Max(t => t.Trade.TradeDate);
        var evaluators = new Dictionary<string, TradeEvaluator>();
        foreach (var symbol in profiles.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            evaluators[symbol.Id] = await CreateEvaluatorAsync(symbol, from, to);
        }

        var days = settled.GroupBy(t => t.Trade.TradeDate).OrderBy(g => g.Key).Select(g => (g.Key, g.Count())).ToList();
        var actual = settled.Sum(t => profiles[t.SymbolId].ToUnits(t.Trade.Net));
        var symbolIds = profiles.Keys.Order(StringComparer.Ordinal).ToList();
        using var timing = Timing.Start("ui", "random-baseline");
        return await Task.Run(() => RandomBaseline.Run(
            days,
            filter,
            symbolIds,
            (symbolId, day, point) =>
            {
                var result = evaluators[symbolId].Evaluate(day, point);
                return result.Status == TradeStatus.Settled ? profiles[symbolId].ToUnits(result.Net) : null;
            },
            actual,
            holds: holds,
            cancellationToken: cancellationToken), cancellationToken);
    }

    /// <summary>期間の足を（キャッシュから）取り、その銘柄の評価器を作る。足は写さないので毎分の読み直しでも軽い。</summary>
    private async Task<TradeEvaluator> CreateEvaluatorAsync(SymbolProfile symbol, DateOnly from, DateOnly to)
    {
        var (fromUtc, toUtc) = TradeEvaluator.RequiredRange(from, to);
        var quotes = await _quotes.GetAsync(symbol.Id, fromUtc, toUtc);
        var until = Statuses.FirstOrDefault(s => s.Symbol.Id == symbol.Id)?.LastBarUtc ?? DateTime.MinValue;
        return new TradeEvaluator(quotes, until, symbol.FallbackSpread);
    }
}
