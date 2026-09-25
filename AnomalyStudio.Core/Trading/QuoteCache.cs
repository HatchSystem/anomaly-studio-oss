using AnomalyStudio.Core.Analysis;
using AnomalyStudio.Core.Diagnostics;
using AnomalyStudio.Core.Storage;

namespace AnomalyStudio.Core.Trading;

/// <summary>
/// 画面の読み直し用の 1 分足キャッシュ。銘柄ごとに読み込んだ範囲をメモリに持ち、範囲内の要求は Parquet を読まずに切り出して返す。
/// エントリー画面とダッシュボードは毎分読み直すので、これがないと毎分すべての銘柄の Parquet を読むことになる。
/// 取込・削除で足が変わると（<see cref="AnalysisDatabase.MarketDataChanged"/>）その銘柄の分を捨てる。
/// 保持する範囲は銘柄ごとに 1 つ（要求と隣接・重複する範囲を合わせて最長 <see cref="MaxRange"/>。超えるときは要求の範囲だけを持ち直す）。
/// </summary>
public sealed class QuoteCache : IDisposable
{
    /// <summary>1 銘柄あたりに保持する最長の範囲（400 日 ≈ 37 万本 ≈ 9 MB）。最適化確認の 1 年分の評価がそのまま入る。</summary>
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(400);

    private readonly AnalysisDatabase _database;
    private readonly Dictionary<string, Cached> _cache = [];
    private readonly Dictionary<string, int> _versions = [];
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly object _lock = new();

    private sealed record Cached(DateTime FromUtc, DateTime ToUtc, OpenQuote[] Quotes);

    public QuoteCache(AnalysisDatabase database)
    {
        _database = database;
        _database.MarketDataChanged += Invalidate;
    }

    /// <summary>Parquet を読んだ回数（計測用）。</summary>
    public int Loads { get; private set; }

    /// <summary>保持している足の本数（計測用）。</summary>
    public int CachedQuoteCount
    {
        get
        {
            lock (_lock)
            {
                return _cache.Values.Sum(c => c.Quotes.Length);
            }
        }
    }

    /// <summary><paramref name="fromUtc"/> 以降 <paramref name="toUtc"/> 未満の足を時刻順に返す（キャッシュの切り出しなので写さない）。</summary>
    public async Task<IReadOnlyList<OpenQuote>> GetAsync(string symbolId, DateTime fromUtc, DateTime toUtc)
    {
        if (TrySlice(symbolId, fromUtc, toUtc, out var slice))
        {
            return slice;
        }

        await _loadGate.WaitAsync();
        try
        {
            if (TrySlice(symbolId, fromUtc, toUtc, out slice))
            {
                return slice;
            }

            // 隣接・重複する範囲と合わせて読む（最長 MaxRange）。離れていたり長すぎれば要求の範囲だけ
            var (loadFrom, loadTo) = (fromUtc, toUtc);
            int version;
            lock (_lock)
            {
                version = _versions.GetValueOrDefault(symbolId);
                if (_cache.TryGetValue(symbolId, out var existing))
                {
                    var unionFrom = existing.FromUtc < fromUtc ? existing.FromUtc : fromUtc;
                    var unionTo = existing.ToUtc > toUtc ? existing.ToUtc : toUtc;
                    if (unionTo - unionFrom <= MaxRange)
                    {
                        (loadFrom, loadTo) = (unionFrom, unionTo);
                    }
                }
            }

            using var timing = Timing.Start("storage", "quote-cache-load");
            var quotes = await _database.LoadOpenQuotesAsync(symbolId, loadFrom, loadTo);
            var loaded = new Cached(loadFrom, loadTo, [.. quotes]);
            lock (_lock)
            {
                Loads++;
                // 読んでいる間に取込で足が変わっていたら、古い内容を持たない（次の要求で読み直す）
                if (_versions.GetValueOrDefault(symbolId) == version)
                {
                    _cache[symbolId] = loaded;
                }
            }

            timing.Detail = $"{symbolId} {loaded.Quotes.Length:N0} 本を保持（{(loadTo - loadFrom).TotalDays:0} 日）";
            return Slice(loaded, fromUtc, toUtc);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>銘柄（null ならすべて）の保持分を捨てる。</summary>
    public void Invalidate(string? symbolId)
    {
        lock (_lock)
        {
            if (symbolId is null)
            {
                foreach (var key in _cache.Keys.Concat(_versions.Keys).Distinct().ToList())
                {
                    _versions[key] = _versions.GetValueOrDefault(key) + 1;
                }

                _cache.Clear();
            }
            else
            {
                _versions[symbolId] = _versions.GetValueOrDefault(symbolId) + 1;
                _cache.Remove(symbolId);
            }
        }
    }

    private bool TrySlice(string symbolId, DateTime fromUtc, DateTime toUtc, out IReadOnlyList<OpenQuote> slice)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(symbolId, out var cached) && cached.FromUtc <= fromUtc && toUtc <= cached.ToUtc)
            {
                slice = Slice(cached, fromUtc, toUtc);
                return true;
            }
        }

        slice = [];
        return false;
    }

    private static IReadOnlyList<OpenQuote> Slice(Cached cached, DateTime fromUtc, DateTime toUtc)
    {
        var lo = LowerBound(cached.Quotes, fromUtc);
        var hi = LowerBound(cached.Quotes, toUtc);
        return new ArraySegment<OpenQuote>(cached.Quotes, lo, hi - lo);
    }

    /// <summary>時刻順の足で、<paramref name="timeUtc"/> 以降の最初の位置。</summary>
    private static int LowerBound(OpenQuote[] quotes, DateTime timeUtc)
    {
        int lo = 0, hi = quotes.Length;
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

    public void Dispose()
    {
        _database.MarketDataChanged -= Invalidate;
        _loadGate.Dispose();
    }
}
