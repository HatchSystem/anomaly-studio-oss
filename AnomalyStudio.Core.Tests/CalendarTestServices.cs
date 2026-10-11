global using AnomalyStudio.Core.EconomicCalendar;
global using AnomalyStudio.Helpers;
global using AnomalyStudio.Models;

namespace AnomalyStudio.Services;

// CalendarViewModel の実ソースを、ディスパッチャーや外部取得なしで実行するための境界。
public sealed class ClockService
{
    public event EventHandler<DateTime>? Tick;

    public DateTime Now => Core.Jst.Now;

    public void RaiseTick() => Tick?.Invoke(this, Now);
}

public interface IEconomicCalendarService
{
    string StatusText { get; }
    event EventHandler? Changed;
    IReadOnlyList<EconomicEvent> GetEvents(DateTime from, DateTime to);
    Task RefreshAsync(DateTime from, DateTime to);
    IReadOnlyList<Country> GetCountries();
}
