using System.Net.Http;
using System.Text.Json;
using AnomalyStudio.Core;
using Microsoft.UI.Dispatching;

namespace AnomalyStudio.Services;

/// <summary>
/// GMO クリック証券の経済指標カレンダーを月単位で取得・保存して返す。
/// 画面は保存済みのデータを同期で読み、未取得・未確定の月は裏で取得して <see cref="Changed"/> で知らせる。
/// 前月〜翌月は設定の「経済指標の更新間隔」に従って取り直す。すべて UI スレッドから呼ぶ。
/// </summary>
public sealed class EconomicCalendarService(EconomicCalendarStore store, AppSettings settings) : IEconomicCalendarService
{
    private readonly Dictionary<(int Year, int Month), IReadOnlyList<EconomicEvent>> _months = [];

    /// <summary>この起動中に自動取得が不要になった月（確定済み・取得済み・取得失敗）。失敗の再試行は手動更新か定期更新で行う。</summary>
    private readonly HashSet<(int Year, int Month)> _settled = [];

    private readonly HashSet<(int Year, int Month)> _inFlight = [];
    private DispatcherQueueTimer? _timer;
    private DateTime _lastRefresh = DateTime.MinValue;

    public string StatusText { get; private set; } = string.Empty;

    public event EventHandler? Changed;

    public void Start(DispatcherQueue dispatcherQueue)
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(1);
        _timer.Tick += async (_, _) =>
        {
            if (IsRefreshDue(Jst.Now))
            {
                await RefreshAroundNowAsync();
            }
        };
        _timer.Start();
        _ = RefreshAroundNowAsync();
    }

    public IReadOnlyList<EconomicEvent> GetEvents(DateTime from, DateTime to)
    {
        var result = new List<EconomicEvent>();
        foreach (var key in Months(from, to))
        {
            result.AddRange(GetMonth(key).Where(e => e.Time >= from && e.Time < to));
        }

        return result;
    }

    public Task RefreshAsync(DateTime from, DateTime to) => FetchAsync(Months(from, to));

    public IReadOnlyList<Country> GetCountries() => [.. CalendarCountries.All.Select(c => new Country(c.Code, c.Name))];

    private bool IsRefreshDue(DateTime now) =>
        settings.CalendarRefresh switch
        {
            "1時間ごと" => now - _lastRefresh >= TimeSpan.FromHours(1),
            "起動時 + 毎日 06:00" => now >= now.Date.AddHours(6) && _lastRefresh < now.Date.AddHours(6),
            _ => false,
        };

    /// <summary>前月（未確定なら）・当月・翌月を取り直す。</summary>
    private Task RefreshAroundNowAsync()
    {
        _lastRefresh = Jst.Now;
        var month = new DateTime(_lastRefresh.Year, _lastRefresh.Month, 1);
        var previous = month.AddMonths(-1);
        var targets = Months(month, month.AddMonths(2));
        return FetchAsync(store.IsComplete(previous.Year, previous.Month) ? targets : targets.Prepend((previous.Year, previous.Month)));
    }

    private IReadOnlyList<EconomicEvent> GetMonth((int Year, int Month) key)
    {
        if (!_months.TryGetValue(key, out var events))
        {
            events = store.Load(key.Year, key.Month) ?? [];
            _months[key] = events;
        }

        if (!_settled.Contains(key) && !_inFlight.Contains(key))
        {
            if (new DateOnly(key.Year, key.Month, 1) < ClickSecCalendarSource.FirstMonth || store.IsComplete(key.Year, key.Month))
            {
                _settled.Add(key);
            }
            else
            {
                _ = FetchAsync([key]);
            }
        }

        return events;
    }

    private async Task FetchAsync(IEnumerable<(int Year, int Month)> months)
    {
        // 提供開始より前の月は要求しない。取得中の月は重ねて要求しない
        var targets = months
            .Where(k => new DateOnly(k.Year, k.Month, 1) >= ClickSecCalendarSource.FirstMonth && _inFlight.Add(k))
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        // GetEvents の途中から呼ばれるので、画面が一覧を組み立て終えてから通知する
        await Task.Yield();
        SetStatus("取得中…");
        var failed = false;
        foreach (var key in targets)
        {
            try
            {
                var events = await store.FetchAsync(key.Year, key.Month);
                if (events.Count > 0)
                {
                    _months[key] = events;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
            {
                failed = true;
            }
            finally
            {
                _settled.Add(key);
                _inFlight.Remove(key);
            }
        }

        SetStatus(failed
            ? $"取得に失敗しました（{Format.HourMinute(Jst.Now)}）· 保存済みのデータを表示しています"
            : $"{Format.HourMinute(Jst.Now)} 更新");
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static IEnumerable<(int Year, int Month)> Months(DateTime from, DateTime to)
    {
        for (var m = new DateTime(from.Year, from.Month, 1); m < to; m = m.AddMonths(1))
        {
            yield return (m.Year, m.Month);
        }
    }
}
