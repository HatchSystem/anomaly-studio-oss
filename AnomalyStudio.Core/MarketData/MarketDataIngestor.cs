using System.Text.Json;
using AnomalyStudio.Core.Diagnostics;
using AnomalyStudio.Core.Storage;
using AnomalyStudio.Core.Symbols;

namespace AnomalyStudio.Core.MarketData;

/// <summary>取込の進捗（0〜1）と、処理中の説明。</summary>
public readonly record struct IngestProgress(double Fraction, string Message);

public sealed record IngestResult(string SymbolId, int AddedBars, DateTime? LastBarUtc);

/// <summary>複数銘柄の取込での 1 銘柄の結果。<see cref="Error"/> があればその銘柄は取り込めなかった（他の銘柄は続けている）。</summary>
public sealed record IngestOutcome(string SymbolId, int AddedBars, DateTime? LastBarUtc, Exception? Error);

/// <summary>
/// 未取得の 1 分足だけを取得して保存する（差分取込。新しい側と、バックテスト用の過去側）。BID と ASK を別々に取得し、同じ時刻の足だけを結合して保存する。
/// 形成中の足を保存しないよう、取得の終端は現在時刻の 1 分前に切り捨てる。
/// </summary>
public sealed class MarketDataIngestor(IMarketDataSource source, AnalysisDatabase database)
{
    /// <summary>
    /// 複数銘柄を取り込むときに同時に取得する銘柄数。Dukascopy はブラウザー以外の要求を制限（429）するので、
    /// 銘柄ごとの BID / ASK の並行と合わせて同時 4 要求までにとどめる。
    /// </summary>
    public const int DefaultConcurrency = 2;

    /// <summary>
    /// 保存済みの続きから現在までを取得する。未取得の銘柄は <paramref name="initialFromUtc"/> から取得する。
    /// </summary>
    public async Task<IngestResult> IngestAsync(
        SymbolProfile symbol,
        DateTime initialFromUtc,
        IProgress<IngestProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var latest = await database.GetLatestBarTimeAsync(symbol.Id);
        var from = latest?.AddMinutes(1) ?? initialFromUtc;
        var now = DateTime.UtcNow;
        var to = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc).AddMinutes(-1);
        if (from >= to)
        {
            progress?.Report(new IngestProgress(1, $"{symbol.Id}: 最新です"));
            return new IngestResult(symbol.Id, 0, latest);
        }

        var added = await FetchAndSaveAsync(symbol, from, to, progress, cancellationToken);
        return new IngestResult(symbol.Id, added.Count, added.Count > 0 ? added[^1].TimeUtc : latest);
    }

    /// <summary>
    /// 複数の銘柄を <paramref name="concurrency"/> 銘柄ずつ並行して取り込む。
    /// 通信や保存の失敗は銘柄ごとに <see cref="IngestOutcome.Error"/> に入れて他の銘柄を続ける（中止は例外のまま伝える）。
    /// 進捗は全銘柄の平均で、説明は最後に進んだ銘柄のもの。
    /// </summary>
    public async Task<IReadOnlyList<IngestOutcome>> IngestAllAsync(
        IReadOnlyList<SymbolProfile> symbols,
        DateTime initialFromUtc,
        IProgress<IngestProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int concurrency = DefaultConcurrency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);
        var fractions = new double[symbols.Count];
        var outcomes = new IngestOutcome[symbols.Count];
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        var tasks = symbols.Select(async (symbol, i) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var result = await IngestAsync(symbol, initialFromUtc, new Progress<IngestProgress>(p =>
                {
                    fractions[i] = p.Fraction;
                    progress?.Report(new IngestProgress(fractions.Sum() / symbols.Count, p.Message));
                }), cancellationToken);
                outcomes[i] = new IngestOutcome(result.SymbolId, result.AddedBars, result.LastBarUtc, null);
            }
            catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
            {
                outcomes[i] = new IngestOutcome(symbol.Id, 0, null, ex);
                fractions[i] = 1;
                progress?.Report(new IngestProgress(fractions.Sum() / symbols.Count, $"{symbol.Id}: 取り込めませんでした（{ex.Message}）"));
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks);
        return outcomes;
    }

    /// <summary>1 銘柄の失敗として扱い、他の銘柄を続けてよい例外（利用者の中止は含めない）。</summary>
    private static bool IsRecoverable(Exception ex, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && ex is HttpRequestException or IOException or DuckDB.NET.Data.DuckDBException or JsonException or OperationCanceledException;

    /// <summary>
    /// 保存済みの最古の足より前が <paramref name="fromUtc"/> まで足りなければ、その差分だけを取得する（過去側の差分取込）。
    /// 未取得の銘柄は <see cref="IngestAsync"/> と同じく現在まで取得する。
    /// </summary>
    public async Task<int> BackfillAsync(
        SymbolProfile symbol,
        DateTime fromUtc,
        IProgress<IngestProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var (_, first, _) = await database.GetMarketCoverageAsync(symbol.Id);
        if (first is null)
        {
            return (await IngestAsync(symbol, fromUtc, progress, cancellationToken)).AddedBars;
        }

        // 週末・年末年始は足がないため、最古の足が数日遅れていても取得済みとみなす
        if (first.Value <= fromUtc.Add(BackfillTolerance))
        {
            progress?.Report(new IngestProgress(1, $"{symbol.Id}: 取得済みです"));
            return 0;
        }

        return (await FetchAndSaveAsync(symbol, fromUtc, first.Value, progress, cancellationToken)).Count;
    }

    /// <summary>過去側の取込で、最古の足がこの幅だけ遅れていても取得済みとみなす。</summary>
    public static readonly TimeSpan BackfillTolerance = TimeSpan.FromDays(3);

    private async Task<List<MinuteBar>> FetchAndSaveAsync(
        SymbolProfile symbol, DateTime from, DateTime to, IProgress<IngestProgress>? progress, CancellationToken cancellationToken)
    {
        var span = (to - from).TotalMinutes;
        double bidDone = 0, askDone = 0;
        void Report(OfferSide side, DateTime reached)
        {
            var fraction = Math.Clamp((reached - from).TotalMinutes / span, 0, 1);
            if (side == OfferSide.Bid)
            {
                bidDone = fraction;
            }
            else
            {
                askDone = fraction;
            }

            progress?.Report(new IngestProgress((bidDone + askDone) / 2, $"{symbol.Id}: {Jst.FromUtc(reached):yyyy/MM/dd} まで取得"));
        }

        progress?.Report(new IngestProgress(0, $"{symbol.Id}: {Jst.FromUtc(from):yyyy/MM/dd} から取得開始"));
        IReadOnlyList<SideBar> bids, asks;
        using (var timing = Timing.Start("ingest", "fetch"))
        {
            var bidTask = source.FetchAsync(symbol.DukascopyInstrument, OfferSide.Bid, from, to,
                new Progress<DateTime>(t => Report(OfferSide.Bid, t)), cancellationToken);
            var askTask = source.FetchAsync(symbol.DukascopyInstrument, OfferSide.Ask, from, to,
                new Progress<DateTime>(t => Report(OfferSide.Ask, t)), cancellationToken);
            await Task.WhenAll(bidTask, askTask);
            (bids, asks) = (bidTask.Result, askTask.Result);
            timing.Detail = $"{symbol.Id} BID {bids.Count:N0} / ASK {asks.Count:N0} 本（{(to - from).TotalDays:0} 日）";
        }

        var askByTime = asks.ToDictionary(b => b.TimeUtc);
        var joined = bids
            .Where(b => askByTime.ContainsKey(b.TimeUtc))
            .Select(b => MinuteBar.Join(b, askByTime[b.TimeUtc]))
            .ToList();

        if (joined.Count > 0)
        {
            await database.SaveBarsAsync(symbol.Id, joined);
        }

        progress?.Report(new IngestProgress(1, $"{symbol.Id}: {joined.Count:N0} 本を保存"));
        return joined;
    }
}
