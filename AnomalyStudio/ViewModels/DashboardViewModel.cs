using AnomalyStudio.Core;
using AnomalyStudio.Core.Analysis;
using AnomalyStudio.Core.Trading;

namespace AnomalyStudio.ViewModels;

public sealed partial class DashboardViewModel(
    AnomalyWorkspace workspace, IEconomicCalendarService calendar, AppSettings settings, ClockService clock)
    : ClockedViewModel(clock)
{
    private int _entryOffset;
    private int _eventOffset;
    private IReadOnlyList<EntryItem> _entries = [];
    /// <summary>最後に読み込んだ分（JST）。エントリーは分単位なので、分が変わったら読み直す。</summary>
    private DateTime _loadedMinute;

    public async void Start()
    {
        workspace.Changed += OnWorkspaceChanged;
        await LoadEntriesAsync();
        Activate();
    }

    public void Stop()
    {
        workspace.Changed -= OnWorkspaceChanged;
        Deactivate();
    }

    private async void OnWorkspaceChanged(object? sender, EventArgs e) => await LoadEntriesAsync();

    /// <summary>読み込みの通し番号。毎分の読み直しと分析結果の更新が重なったとき、古い読み込みの結果で新しい結果を上書きしない。</summary>
    private int _loadVersion;

    private async Task LoadEntriesAsync()
    {
        var version = ++_loadVersion;
        _loadedMinute = Format.StartOfMinute(Jst.Now);
        var entries = await workspace.GetEntriesAsync(Jst.Now, ShowComposite);
        if (version != _loadVersion)
        {
            return;
        }

        _entries = entries;
        UpdateEntry(Jst.Now);
    }

    [ObservableProperty]
    public partial bool HasEntries { get; set; }

    [ObservableProperty]
    public partial string DateText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DstLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsDst { get; set; }

    [ObservableProperty]
    public partial string DstDescription { get; set; } = string.Empty;

    // 次のエントリー
    /// <summary>複合ポイント（全銘柄をまとめた順位で選んだポイント）のエントリーを出す。記憶しない（既定は銘柄別）。</summary>
    [ObservableProperty]
    public partial bool ShowComposite { get; set; }

    public string EntryKindLabel => ShowComposite ? "複合" : "エントリー";

    [RelayCommand]
    private void ToggleComposite() => ShowComposite = !ShowComposite;

    async partial void OnShowCompositeChanged(bool value)
    {
        OnPropertyChanged(nameof(EntryKindLabel));
        _entryOffset = 0;
        await LoadEntriesAsync();
    }

    [ObservableProperty]
    public partial string EntryPair { get; set; } = "—";

    [ObservableProperty]
    public partial string EntrySlot { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool EntryIsLong { get; set; }

    [ObservableProperty]
    public partial bool EntryIsShort { get; set; }

    [ObservableProperty]
    public partial bool EntryHasResult { get; set; }

    [ObservableProperty]
    public partial bool EntryResultPositive { get; set; }

    [ObservableProperty]
    public partial bool EntryResultNegative { get; set; }

    [ObservableProperty]
    public partial string EntryResult { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EntryPosition { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousEntryCommand))]
    public partial bool CanPreviousEntry { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextEntryCommand))]
    public partial bool CanNextEntry { get; set; }

    // 次の経済指標
    [ObservableProperty]
    public partial string EventClock { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EventCountry { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EventName { get; set; } = "—";

    [ObservableProperty]
    public partial string EventStarsOn { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EventStarsOff { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EventTiming { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousEventCommand))]
    public partial bool CanPreviousEvent { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextEventCommand))]
    public partial bool CanNextEvent { get; set; }

    [RelayCommand(CanExecute = nameof(CanPreviousEntry))]
    private void PreviousEntry() => Step(ref _entryOffset, -1);

    [RelayCommand(CanExecute = nameof(CanNextEntry))]
    private void NextEntry() => Step(ref _entryOffset, 1);

    [RelayCommand(CanExecute = nameof(CanPreviousEvent))]
    private void PreviousEvent() => Step(ref _eventOffset, -1);

    [RelayCommand(CanExecute = nameof(CanNextEvent))]
    private void NextEvent() => Step(ref _eventOffset, 1);

    private void Step(ref int offset, int delta)
    {
        offset += delta;
        UpdateEntry(Jst.Now);
        UpdateEvent(Jst.Now);
    }

    protected override void Update(DateTime now)
    {
        DateText = Format.LongDate(now);
        IsDst = Format.IsUsDaylightSaving(now);
        DstLabel = IsDst ? "夏時間" : "冬時間";
        DstDescription = IsDst
            ? "米国夏時間 · NY市場は日本時間で1時間早まります"
            : "米国標準時間（冬時間）";
        if (Format.StartOfMinute(Jst.Now) != _loadedMinute)
        {
            _ = LoadEntriesAsync();
        }

        UpdateEntry(Jst.Now);
        UpdateEvent(Jst.Now);
    }

    private void UpdateEntry(DateTime now)
    {
        var entries = _entries;
        HasEntries = entries.Count > 0;
        if (entries.Count == 0)
        {
            EntryPair = "—";
            EntrySlot = workspace.HasAnalysis ? "エントリーはありません"
                : workspace.NeedsReanalysis ? "再分析が必要です（データ抽出設定で分析を実行）"
                : "分析結果がありません（データ抽出設定で分析を実行）";
            (EntryIsLong, EntryIsShort, EntryHasResult, EntryPosition) = (false, false, false, string.Empty);
            CanPreviousEntry = CanNextEntry = false;
            return;
        }

        var next = EntryFormatter.NextIndex(entries, now);
        var baseIndex = next == -1 ? entries.Count - 1 : next;
        _entryOffset = Math.Clamp(_entryOffset, -baseIndex, entries.Count - 1 - baseIndex);
        var index = baseIndex + _entryOffset;
        var e = entries[index];

        var prefix = Format.DayPrefix(e.EntryJst, now);
        EntryPair = e.Info.Symbol.Id;
        EntrySlot = $"{(prefix.Length > 0 ? prefix + " " : string.Empty)}{Format.HourMinute(e.EntryJst)} – {Format.HourMinute(e.CloseJst)}";
        EntryIsLong = e.Info.Point.Direction == TradeDirection.Long;
        EntryIsShort = !EntryIsLong;
        EntryHasResult = e.Trade.Status == TradeStatus.Settled;
        EntryResult = EntryFormatter.ResultText(e);
        EntryResultPositive = EntryHasResult && e.Trade.Net > 0;
        EntryResultNegative = EntryHasResult && e.Trade.Net <= 0;
        EntryPosition = $"{e.Info.Mode.Name} · {index + 1}/{entries.Count}";
        CanPreviousEntry = index > 0;
        CanNextEntry = index < entries.Count - 1;
    }

    /// <summary>カレンダーの絞り込み（国・重要度）に合う、時刻の決まった指標を前後にたどる。時刻は JST。</summary>
    private void UpdateEvent(DateTime now)
    {
        var events = calendar.GetEvents(now.Date.AddDays(-7), now.Date.AddDays(15))
            .Where(e => !e.IsHoliday && !e.TimeUndecided
                        && CalendarViewModel.Matches(e, settings.CalendarMinStars, settings.CalendarCountries))
            .ToList();
        if (events.Count == 0)
        {
            (EventClock, EventCountry, EventName, EventStarsOn, EventStarsOff, EventTiming) =
                (string.Empty, string.Empty, "—", string.Empty, string.Empty, string.Empty);
            CanPreviousEvent = CanNextEvent = false;
            return;
        }

        var next = events.FindIndex(e => e.Time > now);
        var baseIndex = next == -1 ? events.Count - 1 : next;
        _eventOffset = Math.Clamp(_eventOffset, -baseIndex, events.Count - 1 - baseIndex);
        var index = baseIndex + _eventOffset;
        var ev = events[index];

        var prefix = Format.DayPrefix(ev.Time, now);
        EventClock = $"{(prefix.Length > 0 ? prefix + " " : string.Empty)}{Format.HourMinute(ev.Time)}";
        EventCountry = string.Join("·", ev.Countries);
        EventName = ev.Name;
        EventStarsOn = Format.StarsOn(ev.Importance);
        EventStarsOff = Format.StarsOff(ev.Importance);
        EventTiming = ev.Time > now ? $"あと {Format.LongCountdown(ev.Time - now)}" : $"結果 {(ev.Actual.Length > 0 ? ev.Actual : "—")}";
        CanPreviousEvent = index > 0;
        CanNextEvent = index < events.Count - 1;
    }
}
