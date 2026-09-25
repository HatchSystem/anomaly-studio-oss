using AnomalyStudio.Core.Analysis;
using AnomalyStudio.Core.MarketData;
using AnomalyStudio.Core.Storage;
using AnomalyStudio.Core.Trading;

namespace AnomalyStudio.Core.Tests;

[TestClass]
public sealed class QuoteCacheTests
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

    private static MinuteBar Bar(DateTime utc, double bid) =>
        new(utc, bid, bid, bid, bid, 1, bid + 0.1, bid + 0.1, bid + 0.1, bid + 0.1, 1);

    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>9/1 0:00 から 10 日分、毎時 0 分の足（価格は時刻の通し番号）。</summary>
    private static List<MinuteBar> HourlyBars(int days) =>
        [.. Enumerable.Range(0, days * 24).Select(h => Bar(T0.AddHours(h), h))];

    [TestMethod]
    public async Task Get_ReadsOnce_ThenSlicesFromMemory()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        await db.SaveBarsAsync("USDJPY", HourlyBars(10));
        using var cache = new QuoteCache(db);

        var first = await cache.GetAsync("USDJPY", T0.AddDays(2), T0.AddDays(5));
        Assert.AreEqual(3 * 24, first.Count);
        Assert.AreEqual(48.0, first[0].BidOpen);
        Assert.AreEqual(1, cache.Loads);

        // 範囲内の要求は読まずに切り出す（境界は from 以上 to 未満）
        var inner = await cache.GetAsync("USDJPY", T0.AddDays(3), T0.AddDays(4));
        Assert.AreEqual(24, inner.Count);
        Assert.AreEqual(72.0, inner[0].BidOpen);
        Assert.AreEqual(95.0, inner[^1].BidOpen);
        Assert.AreEqual(1, cache.Loads);

        // 隣接・重複する範囲は合わせて読み直し、その後は両方の範囲を切り出せる
        var wider = await cache.GetAsync("USDJPY", T0, T0.AddDays(3));
        Assert.AreEqual(3 * 24, wider.Count);
        Assert.AreEqual(2, cache.Loads);
        Assert.AreEqual(5 * 24, cache.CachedQuoteCount);
        Assert.AreEqual(4 * 24, (await cache.GetAsync("USDJPY", T0.AddDays(1), T0.AddDays(5))).Count);
        Assert.AreEqual(2, cache.Loads);
    }

    [TestMethod]
    public async Task Get_RangeLongerThanMax_KeepsOnlyRequestedRange()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        await db.SaveBarsAsync("USDJPY", HourlyBars(3));
        using var cache = new QuoteCache(db);

        await cache.GetAsync("USDJPY", T0, T0.AddDays(1));
        // 保持中の範囲と合わせると 400 日を超えるので、要求の範囲だけを持ち直す
        var far = await cache.GetAsync("USDJPY", T0.AddDays(1), T0.AddDays(1) + QuoteCache.MaxRange);
        Assert.AreEqual(2 * 24, far.Count);
        Assert.AreEqual(2 * 24, cache.CachedQuoteCount);
        Assert.AreEqual(2, cache.Loads);

        // 最初の範囲は捨てたので読み直す
        await cache.GetAsync("USDJPY", T0, T0.AddDays(1));
        Assert.AreEqual(3, cache.Loads);
    }

    [TestMethod]
    public async Task SavingBars_InvalidatesThatSymbolOnly()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        await db.SaveBarsAsync("USDJPY", HourlyBars(2));
        await db.SaveBarsAsync("EURUSD", HourlyBars(2));
        using var cache = new QuoteCache(db);

        await cache.GetAsync("USDJPY", T0, T0.AddDays(2));
        await cache.GetAsync("EURUSD", T0, T0.AddDays(2));
        Assert.AreEqual(2, cache.Loads);

        await db.SaveBarsAsync("USDJPY", [Bar(T0.AddHours(1), 999)]);
        var usd = await cache.GetAsync("USDJPY", T0, T0.AddDays(2));
        Assert.AreEqual(999.0, usd[1].BidOpen, "取込後は読み直して新しい値になる");
        Assert.AreEqual(3, cache.Loads);

        await cache.GetAsync("EURUSD", T0, T0.AddDays(2));
        Assert.AreEqual(3, cache.Loads, "他の銘柄は保持したまま");

        await db.ResetAsync();
        Assert.AreEqual(0, (await cache.GetAsync("EURUSD", T0, T0.AddDays(2))).Count);
        Assert.AreEqual(4, cache.Loads);
    }

    [TestMethod]
    public async Task Evaluator_UsesCachedSliceWithoutCopying()
    {
        await using var db = new AnalysisDatabase(new AppPaths(_root));
        var day = new DateOnly(2026, 9, 1);
        var entryUtc = Jst.ToUtc(day.ToDateTime(new TimeOnly(9, 0)));
        await db.SaveBarsAsync("USDJPY", [Bar(entryUtc, 100), Bar(entryUtc.AddMinutes(5), 101)]);
        using var cache = new QuoteCache(db);

        var (from, to) = TradeEvaluator.RequiredRange(day, day);
        var evaluator = new TradeEvaluator(await cache.GetAsync("USDJPY", from, to), entryUtc.AddMinutes(10), 0.5);
        var trade = evaluator.Evaluate(day, new EntryPoint(1, TradeDirection.Long, 540, 5, 0));
        Assert.AreEqual(TradeStatus.Settled, trade.Status);
        Assert.AreEqual(1.0 - 0.1, trade.Net, 1e-9);
    }
}
