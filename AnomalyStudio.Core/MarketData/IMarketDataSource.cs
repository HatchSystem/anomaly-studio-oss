namespace AnomalyStudio.Core.MarketData;

public enum OfferSide
{
    Bid,
    Ask,
}

/// <summary>1 分足の履歴データの取得元。</summary>
public interface IMarketDataSource
{
    /// <summary>
    /// <paramref name="fromUtc"/> 以降、<paramref name="toUtc"/> 未満の 1 分足を時刻順に返す。
    /// 休場中の時刻は含まれない。
    /// </summary>
    Task<IReadOnlyList<SideBar>> FetchAsync(
        string instrument,
        OfferSide side,
        DateTime fromUtc,
        DateTime toUtc,
        IProgress<DateTime>? progress = null,
        CancellationToken cancellationToken = default);
}
