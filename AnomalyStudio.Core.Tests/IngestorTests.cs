using AnomalyStudio.Core.MarketData;
using AnomalyStudio.Core.Storage;
using AnomalyStudio.Core.Symbols;

namespace AnomalyStudio.Core.Tests;

[TestClass]
public sealed class IngestorTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup() => _root = Path.Combine(Path.GetTempPath(), "anomaly-tests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task IngestAll_ContinuesOtherSymbols_WhenOneFails_AndLimitsConcurrency()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        var source = new FlakySource(failing: "GBP/USD");
        var ingestor = new MarketDataIngestor(source, db);
        var symbols = new[] { "USD/JPY", "GBP/USD", "EUR/USD", "AUD/USD", "XAU/USD" }.Select(pair => SymbolCatalog.Create(pair)).ToList();
        var from = DateTime.UtcNow.AddHours(-30);
        var messages = new List<string>();

        var outcomes = await ingestor.IngestAllAsync(symbols, from, new Progress<IngestProgress>(p => messages.Add(p.Message)), concurrency: 2);

        Assert.AreEqual(5, outcomes.Count);
        var failed = outcomes.Single(o => o.SymbolId == "GBPUSD");
        Assert.IsInstanceOfType<HttpRequestException>(failed.Error);
        Assert.AreEqual(0, failed.AddedBars);
        foreach (var ok in outcomes.Where(o => o.SymbolId != "GBPUSD"))
        {
            Assert.IsNull(ok.Error, ok.SymbolId);
            Assert.IsGreaterThan(0, ok.AddedBars, ok.SymbolId);
            Assert.IsGreaterThan(0, (await db.GetMarketCoverageAsync(ok.SymbolId)).Count, ok.SymbolId);
        }

        Assert.IsLessThanOrEqualTo(2, source.MaxConcurrentSymbols, "同時に取得する銘柄数は上限まで");
    }

    [TestMethod]
    public async Task IngestAll_Cancellation_IsNotSwallowed()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        var source = new FlakySource(failing: null);
        var ingestor = new MarketDataIngestor(source, db);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ingestor.IngestAllAsync([SymbolCatalog.Create("USD/JPY")], DateTime.UtcNow.AddHours(-2), cancellationToken: cts.Token));
    }

    /// <summary>要求された範囲の毎時 0 分に足を返す。指定した銘柄は通信エラーにする。同時に取得している銘柄数を数える。</summary>
    private sealed class FlakySource(string? failing) : IMarketDataSource
    {
        private readonly HashSet<string> _active = [];

        public int MaxConcurrentSymbols { get; private set; }

        public async Task<IReadOnlyList<SideBar>> FetchAsync(
            string instrument, OfferSide side, DateTime fromUtc, DateTime toUtc, IProgress<DateTime>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_active)
            {
                _active.Add(instrument);
                MaxConcurrentSymbols = Math.Max(MaxConcurrentSymbols, _active.Count);
            }

            try
            {
                await Task.Delay(30, cancellationToken);
                if (instrument == failing)
                {
                    throw new HttpRequestException("接続できません");
                }

                var price = side == OfferSide.Bid ? 100.0 : 100.01;
                var bars = new List<SideBar>();
                for (var t = new DateTime(fromUtc.Year, fromUtc.Month, fromUtc.Day, fromUtc.Hour, 0, 0, DateTimeKind.Utc); t < toUtc; t = t.AddHours(1))
                {
                    if (t >= fromUtc)
                    {
                        bars.Add(new SideBar(t, price, price, price, price, 1));
                    }
                }

                return bars;
            }
            finally
            {
                lock (_active)
                {
                    _active.Remove(instrument);
                }
            }
        }
    }
}
