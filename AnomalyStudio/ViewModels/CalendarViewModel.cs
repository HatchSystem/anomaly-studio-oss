using System.Collections.ObjectModel;
using AnomalyStudio.Core;

namespace AnomalyStudio.ViewModels;

public sealed class CalendarRowViewModel
{
    public required string Time { get; init; }
    public required string Country { get; init; }
    public required string Name { get; init; }
    public required string StarsOn { get; init; }
    public required string StarsOff { get; init; }
    public required string Previous { get; init; }
    public required string Forecast { get; init; }
    public required string Actual { get; init; }
    public required bool IsUp { get; init; }
    public required bool IsDown { get; init; }
    public bool IsFlat => !IsUp && !IsDown;
    public required bool IsNext { get; init; }
    public required bool IsOdd { get; init; }
    public required bool IsHoliday { get; init; }
    public bool IsEvent => !IsHoliday;
}

public sealed class CalendarGroupViewModel
{
    public required string Label { get; init; }
    public required bool IsToday { get; init; }
    public required IReadOnlyList<CalendarRowViewModel> Rows { get; init; }
}

public sealed partial class CountryOptionViewModel(Country country, bool isChecked, Action changed) : ObservableObject
{
    public string Code => country.Code;

    public string Name => country.Name;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked;

    partial void OnIsCheckedChanged(bool value) => changed();
}

public sealed partial class CalendarViewModel : ClockedViewModel
{
    /// <summary>重要度の絞り込み。添字が最小の ★ の数（0 は重要度のない要人発言なども含めたすべて）。</summary>
    public static IReadOnlyList<string> ImportanceLabels { get; } =
        ["すべて", "★ 以上", "★★ 以上", "★★★ 以上", "★★★★ 以上", "★★★★★ のみ"];

    private readonly IEconomicCalendarService _data;
    private readonly AppSettings _settings;
    private string _signature = string.Empty;
    private DateTime _from;
    private DateTime _to;

    /// <summary>
    /// 画面を開いたとき・週次／月次や期間を切り替えたときだけ、一覧を作ったあとにスクロールする
    /// （次の指標があればその行、なければ先頭）。分ごとの更新では利用者の位置を動かさない。
    /// </summary>
    private bool _scrollPending = true;

    public CalendarViewModel(IEconomicCalendarService data, AppSettings settings, ClockService clock)
        : base(clock)
    {
        _data = data;
        _settings = settings;
        Countries = data.GetCountries()
            .Select(c => new CountryOptionViewModel(c, settings.CalendarCountries.Contains(c.Code), OnCountryChanged))
            .ToList();
        SelectedImportance = ImportanceOptions[Math.Clamp(settings.CalendarMinStars, 0, 5)];
        StatusText = data.StatusText;
        UpdateCountryLabel();
    }

    public IReadOnlyList<string> ImportanceOptions => ImportanceLabels;

    public IReadOnlyList<CountryOptionViewModel> Countries { get; }

    public ObservableCollection<CalendarGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    public partial bool IsWeekly { get; set; } = true;

    public bool IsMonthly => !IsWeekly;

    [ObservableProperty]
    public partial int PeriodOffset { get; set; }

    [ObservableProperty]
    public partial string RangeLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedImportance { get; set; }

    [ObservableProperty]
    public partial string CountryLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NextName { get; set; } = "—";

    [ObservableProperty]
    public partial string NextTiming { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    /// <summary>取得状況（"09:12 更新" など）。</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; }

    private int MinStars => Math.Max(0, ImportanceLabels.ToList().IndexOf(SelectedImportance));

    /// <summary>絞り込みに合う指標か。休場日は重要度に関係なく国だけで絞り込む。</summary>
    public static bool Matches(EconomicEvent e, int minStars, IReadOnlySet<string> countries) =>
        (e.IsHoliday || e.Importance >= minStars) && e.Countries.Any(countries.Contains);

    /// <summary>次の指標の行（日付グループの位置, グループ内の行の位置）へスクロールしてほしいとき。(-1, -1) は先頭。</summary>
    public event EventHandler<(int Group, int Row)>? ScrollToNextRequested;

    public void Start()
    {
        _data.Changed += OnDataChanged;
        Activate();
    }

    public void Stop()
    {
        _data.Changed -= OnDataChanged;
        Deactivate();
    }

    private void OnDataChanged(object? sender, EventArgs e)
    {
        StatusText = _data.StatusText;
        Refresh();
    }

    partial void OnIsWeeklyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMonthly));
        _scrollPending = true;
        PeriodOffset = 0;
        Refresh();
    }

    partial void OnPeriodOffsetChanged(int value)
    {
        _scrollPending = true;
        Refresh();
    }

    partial void OnSelectedImportanceChanged(string value)
    {
        // 画面を閉じるときなどに選択が空になった値は保存しない（前回の重要度を覚えておく）
        if (!ImportanceLabels.Contains(value))
        {
            return;
        }

        _settings.CalendarMinStars = MinStars;
        Refresh();
    }

    [RelayCommand]
    private void ShowWeekly() => IsWeekly = true;

    [RelayCommand]
    private void ShowMonthly() => IsWeekly = false;

    [RelayCommand]
    private void Previous() => PeriodOffset--;

    [RelayCommand]
    private void Next() => PeriodOffset++;

    [RelayCommand]
    private void Today() => PeriodOffset = 0;

    /// <summary>表示中の期間を取得し直す。</summary>
    [RelayCommand]
    private Task ReloadAsync() => _data.RefreshAsync(_from, _to);

    [RelayCommand]
    private void SelectAllCountries()
    {
        var target = !Countries.All(c => c.IsChecked);
        foreach (var c in Countries)
        {
            c.IsChecked = target;
        }
    }

    private void OnCountryChanged()
    {
        _settings.CalendarCountries.Clear();
        foreach (var c in Countries.Where(c => c.IsChecked))
        {
            _settings.CalendarCountries.Add(c.Code);
        }

        _settings.NotifyCalendarFilterChanged();
        UpdateCountryLabel();
        Refresh();
    }

    private void UpdateCountryLabel()
    {
        var count = Countries.Count(c => c.IsChecked);
        CountryLabel = count == Countries.Count ? "すべて" : count == 0 ? "未選択" : $"{count}か国";
    }

    private void Refresh()
    {
        _signature = string.Empty;
        Update(DateTime.Now);
    }

    /// <summary>経済指標の時刻は JST なので、端末の時計ではなく JST の現在時刻で判定する。</summary>
    protected override void Update(DateTime localNow)
    {
        var now = Jst.Now;
        var minStars = MinStars;
        var countries = _settings.CalendarCountries;
        var upcoming = _data.GetEvents(now, now.Date.AddDays(32))
            .FirstOrDefault(e => e.Time > now && !e.IsHoliday && !e.TimeUndecided && Matches(e, minStars, countries));
        NextName = upcoming?.Name ?? "—";
        NextTiming = upcoming is null ? string.Empty : $"あと {Format.LongCountdown(upcoming.Time - now)}";

        if (IsWeekly)
        {
            _from = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7) + PeriodOffset * 7);
            _to = _from.AddDays(7);
            RangeLabel = $"{_from.Year}年{_from.Month}月{_from.Day}日 – {_to.AddDays(-1).Month}月{_to.AddDays(-1).Day}日";
        }
        else
        {
            _from = new DateTime(now.Year, now.Month, 1).AddMonths(PeriodOffset);
            _to = _from.AddMonths(1);
            RangeLabel = $"{_from.Year}年{_from.Month}月";
        }

        var signature = $"{_from:yyyyMMdd}|{now:yyyyMMdd}|{upcoming?.Time:yyyyMMddHHmm}|{minStars}|{string.Join(',', countries.Order())}";
        if (signature == _signature)
        {
            return;
        }

        _signature = signature;
        Groups.Clear();
        var index = 0;
        var visible = _data.GetEvents(_from, _to).Where(e => Matches(e, minStars, countries));

        (int Group, int Row)? scrollTarget = null;
        foreach (var day in visible.GroupBy(e => e.Time.Date))
        {
            var rows = day.Select(e =>
            {
                var hasActual = ClickSecCalendarParser.TryParseValue(e.Actual, out var actual);
                var hasForecast = ClickSecCalendarParser.TryParseValue(e.Forecast, out var forecast);
                return new CalendarRowViewModel
                {
                    Time = e.IsHoliday ? "休場" : e.TimeUndecided ? "未定" : Format.HourMinute(e.Time),
                    Country = string.Join("·", e.Countries),
                    Name = e.Name,
                    StarsOn = e.IsHoliday ? string.Empty : Format.StarsOn(e.Importance),
                    StarsOff = e.IsHoliday ? string.Empty : Format.StarsOff(e.Importance),
                    Previous = ValueText(e, e.Previous),
                    Forecast = ValueText(e, e.Forecast),
                    Actual = ValueText(e, e.Actual),
                    IsUp = hasActual && hasForecast && actual > forecast,
                    IsDown = hasActual && hasForecast && actual < forecast,
                    IsNext = e == upcoming,
                    IsOdd = index++ % 2 == 1,
                    IsHoliday = e.IsHoliday,
                };
            }).ToList();

            Groups.Add(new CalendarGroupViewModel { Label = Format.DayLabel(day.Key), IsToday = day.Key == now.Date, Rows = rows });
            if (scrollTarget is null && rows.FindIndex(r => r.IsNext) is var row and >= 0)
            {
                scrollTarget = (Groups.Count - 1, row);
            }
        }

        // 画面が購読する前（ViewModel の作成時の初期化）に作った一覧では数えない
        if (_scrollPending && ScrollToNextRequested is not null)
        {
            _scrollPending = false;
            ScrollToNextRequested.Invoke(this, scrollTarget ?? (-1, -1));
        }

        IsEmpty = Groups.Count == 0;
    }

    /// <summary>休場日以外で値がないもの（未発表・該当なし）は "—"。</summary>
    private static string ValueText(EconomicEvent e, string value) => value.Length > 0 || e.IsHoliday ? value : "—";
}
