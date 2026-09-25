using AnomalyStudio.Core;
using Microsoft.UI.Dispatching;

namespace AnomalyStudio.Services;

/// <summary>経済指標カレンダーの取得元。時刻はすべて JST。</summary>
public interface IEconomicCalendarService
{
    /// <summary>取得状況（"09:12 更新"、"取得中…" など）。</summary>
    string StatusText { get; }

    /// <summary>データまたは取得状況が変わったとき（UI スレッドで通知）。</summary>
    event EventHandler? Changed;

    /// <summary>自動更新を始める。</summary>
    void Start(DispatcherQueue dispatcherQueue);

    /// <summary>
    /// <paramref name="from"/> 以降、<paramref name="to"/> 未満の指標を時刻順に返す。
    /// 未取得の月は取得を始め、終わったら <see cref="Changed"/> で知らせる。
    /// </summary>
    IReadOnlyList<EconomicEvent> GetEvents(DateTime from, DateTime to);

    /// <summary>範囲を含む月を取得し直す。</summary>
    Task RefreshAsync(DateTime from, DateTime to);

    IReadOnlyList<Country> GetCountries();
}
