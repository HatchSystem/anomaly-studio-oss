using System.Collections.ObjectModel;
using AnomalyStudio.Core;
using AnomalyStudio.Core.Analysis;
using AnomalyStudio.Core.Backtesting;
using AnomalyStudio.Core.Symbols;
using AnomalyStudio.Core.Trading;

namespace AnomalyStudio.ViewModels;

/// <summary>
/// エントリー一覧の 1 行。<see cref="Key"/> が同じ行は、毎分の更新で作り直さずに状態と結果だけを書き換える
/// （作り直すと一覧が描き直されてちらつく）。
/// </summary>
public sealed partial class EntryRowViewModel : ObservableObject
{
    /// <summary>行を見分けるキー（銘柄・モード・方向・Entry 時刻・保有時間）。</summary>
    public required string Key { get; init; }
    /// <summary>ポイントの順位（#1 など。複合モードでは選んだ銘柄を通したモードごとの順位）。</summary>
    public required string Rank { get; init; }
    public required string Pair { get; init; }
    public required string Date { get; init; }
    public required string Slot { get; init; }
    public required bool IsLong { get; init; }
    public bool IsShort => !IsLong;
    public required string Mode { get; init; }
    public required double WinRate { get; init; }
    public string WinRateText => double.IsNaN(WinRate) ? "—" : $"{WinRate:0.0}%";
    public required string AveragePips { get; init; }
    public required bool AveragePositive { get; init; }
    public bool AverageNegative => !AveragePositive;
    public required bool IsOdd { get; init; }

    // 時間の経過で変わる項目（毎分の更新でこの行のまま書き換える）
    [ObservableProperty]
    public partial string Result { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultNone))]
    public partial bool ResultPositive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultNone))]
    public partial bool ResultNegative { get; set; }

    public bool ResultNone => !ResultPositive && !ResultNegative;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    public partial bool IsDone { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    public partial bool IsNext { get; set; }

    [ObservableProperty]
    public partial string NextLabel { get; set; } = string.Empty;

    public bool IsWaiting => !IsDone && !IsNext;

    /// <summary>同じ行の新しい状態を写す。</summary>
    public void CopyStateFrom(EntryRowViewModel other)
    {
        Result = other.Result;
        ResultPositive = other.ResultPositive;
        ResultNegative = other.ResultNegative;
        IsDone = other.IsDone;
        IsNext = other.IsNext;
        NextLabel = other.NextLabel;
    }
}

/// <summary>
/// バックテストの画面から写す条件（「エントリーに反映」）。銘柄が複数なら複合モード、1 つなら銘柄別で出す。
/// 取引時間帯・ポイント数・曜日は設定に保存して共通なので含めない。
/// </summary>
public sealed record EntryConditions(IReadOnlyList<string> SymbolIds, string Mode);

/// <summary>エントリー一覧で並べ替えられる列。</summary>
public enum EntrySortKey
{
    Rank,
    Pair,
    Time,
    Direction,
    Mode,
    WinRate,
    Average,
    Result,
}

/// <summary>並べ替えできる列見出し。並べ替えに使っている列だけ、向きの矢印を出す。</summary>
public sealed partial class EntrySortHeaderViewModel(EntrySortKey key, string label) : ObservableObject
{
    public EntrySortKey Key => key;

    public string Label => label;

    /// <summary>最初に押したときの向き。数値の列は大きい順、それ以外は小さい順（昇順）。</summary>
    public bool DescendingFirst => key is EntrySortKey.WinRate or EntrySortKey.Average or EntrySortKey.Result;

    [ObservableProperty]
    public partial bool IsAscending { get; set; }

    [ObservableProperty]
    public partial bool IsDescending { get; set; }
}

/// <summary>エントリーの表示用の書式（エントリー一覧とダッシュボードで共通）。</summary>
public static class EntryFormatter
{
    public static string Units(PointInfo info, double priceDiff) =>
        double.IsFinite(priceDiff) ? $"{Format.Signed(info.Symbol.ToUnits(priceDiff))} {info.Symbol.UnitLabel}" : "—";

    public static string ResultText(EntryItem item) => item.Trade.Status switch
    {
        TradeStatus.Settled => Units(item.Info, item.Trade.Net),
        TradeStatus.Pending => "未確定",
        _ => "—",
    };

    /// <summary>次（進行中または直後）のエントリーの位置。すべて終了していれば -1。</summary>
    public static int NextIndex(IReadOnlyList<EntryItem> items, DateTime nowJst) =>
        items.ToList().FindIndex(i => i.CloseJst > nowJst);
}

/// <summary>
/// エントリー一覧。1 日単位（既定は今日）で、前後の日へ移動できる。どの日も最新の分析結果のポイントを当てはめ、ポイントがなければ何も出さない。
/// 表示中の日は記憶しない（画面を開くたびに今日）。
/// 銘柄は複数選べる。複合モードでは、選んだ銘柄をまとめた順位で選んだポイント（複合ポイント）を、
/// 最適化確認・バックテストと同じ条件（取引時間帯・ポイント数・曜日）で絞り込んで出す。
/// </summary>
public sealed partial class EntriesViewModel : ClockedViewModel
{
    private const string All = "すべて";
    private readonly AnomalyWorkspace _workspace;
    private readonly AppSettings _settings;
    private readonly bool _initialized;
    private IReadOnlyList<EntryItem> _items = [];

    /// <summary>「次」の表示用（表示中の日に関係なく、現在時刻の前後 24 時間）。</summary>
    private IReadOnlyList<EntryItem> _upcoming = [];
    private EntryItem? _next;

    /// <summary>最後に読み込んだ分（JST）。分が変わったら読み直して行の状態を判定し直す。</summary>
    private DateTime _loadedMinute;

    /// <summary>読み込みがこれより長くかかるときだけ「読み込み中」を出す（短い読み込みで表示が点滅しないように）。</summary>
    private static readonly TimeSpan LoadingIndicatorDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>今の一覧を読み込んだ日と読み込みの条件（ページを使い回すので、開き直したときにそのまま使えるか判断する）。</summary>
    private (DateOnly Day, string Source)? _loadedFor;

    /// <summary>最後にスクロールしたアクティブな行。変わったときだけスクロールし、利用者のスクロールを毎分戻さない。</summary>
    private string _scrolledKey = string.Empty;

    public EntriesViewModel(AnomalyWorkspace workspace, AppSettings settings, ClockService clock)
        : base(clock)
    {
        _workspace = workspace;
        _settings = settings;
        ModeOptions = [All, .. workspace.Modes.Enabled.Select(m => m.Name)];

        // 銘柄・複合モード・方向・モードは前回の選択を使う（選択肢から消えていれば「すべて」）
        var saved = settings.EntriesSymbols;
        var enabled = workspace.Symbols.Enabled;
        if (saved is not null && !enabled.Any(s => saved.Contains(s.Id)))
        {
            saved = null;
        }

        SymbolOptions = [.. enabled.Select(s => new SymbolOptionViewModel(s.Id, saved is null || saved.Contains(s.Id), OnSymbolChanged))];
        Weekdays = [.. WeekdayOptionViewModel.TradingDays
            .Select(d => new WeekdayOptionViewModel(Format.Weekday(d).ToString(), d, settings.BacktestWeekdays.Contains(d), OnWeekdayChanged))];
        IsComposite = settings.EntriesComposite;
        SelectedSide = SideOptions.Contains(settings.EntriesSide) ? settings.EntriesSide : All;
        SelectedMode = ModeOptions.Contains(settings.EntriesMode) ? settings.EntriesMode : All;
        UpdateDateLabel();
        UpdateConditionLabel();
        _initialized = true;
    }

    /// <summary>銘柄の選択肢（有効な銘柄）。</summary>
    public IReadOnlyList<SymbolOptionViewModel> SymbolOptions { get; }

    private List<string> SelectedSymbolIds => [.. SymbolOptions.Where(s => s.IsChecked).Select(s => s.Id)];

    /// <summary>選んだ銘柄（選択肢の順）。</summary>
    private List<SymbolProfile> SelectedSymbols =>
        [.. SelectedSymbolIds.Select(_workspace.Symbols.Find).OfType<SymbolProfile>()];

    /// <summary>銘柄ボタンの表示。すべてなら「すべて」、1 つならその銘柄、それ以外は銘柄数（幅を変えない）。</summary>
    public string SymbolLabel
    {
        get
        {
            var ids = SelectedSymbolIds;
            return ids.Count == SymbolOptions.Count && ids.Count > 1 ? All
                : ids.Count switch
                {
                    0 => "未選択",
                    1 => ids[0],
                    _ => $"{ids.Count}銘柄",
                };
        }
    }

    /// <summary>選んだ銘柄の一覧（ツールチップ）。</summary>
    public string SymbolListLabel => SelectedSymbolIds is { Count: > 0 } ids ? string.Join("・", ids) : "未選択";

    /// <summary>複合モード: 選んだ銘柄をまとめた順位で選び、最適化確認と同じ条件で絞り込む。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotComposite))]
    public partial bool IsComposite { get; set; }

    /// <summary>順位の絞り込みは銘柄別のときだけ（複合モードはポイント数で絞る）。</summary>
    public bool IsNotComposite => !IsComposite;

    // 取引時間帯・曜日・ポイント数は最適化確認・バックテストの画面と共通（設定に保存）。
    // 時間帯と曜日は銘柄別・複合モードの両方で、ポイント数は複合モードで使う
    public IReadOnlyList<string> StartHourOptions { get; } = [.. Enumerable.Range(0, 24).Select(BacktestViewModelBase.HourLabel)];

    public IReadOnlyList<string> EndHourOptions { get; } = [.. Enumerable.Range(1, 24).Select(BacktestViewModelBase.HourLabel)];

    public IReadOnlyList<string> PointCountOptions { get; } = [.. new[] { 5, 10, 20, 30, 50 }.Select(BacktestViewModelBase.CountLabel)];

    /// <summary>取引時間帯の開始（終了以降を選んだら終了を 1 時間後へずらす）。</summary>
    public string StartHourLabel
    {
        get => BacktestViewModelBase.HourLabel(_settings.BacktestStartHour);
        set
        {
            if (BacktestViewModelBase.ParseNumber(value) is { } hour && hour != _settings.BacktestStartHour)
            {
                _settings.SetBacktestStartHour(hour);
                OnConditionChanged();
            }
        }
    }

    /// <summary>取引時間帯の終了（開始以前を選んだら開始を 1 時間前へずらす）。</summary>
    public string EndHourLabel
    {
        get => BacktestViewModelBase.HourLabel(_settings.BacktestEndHour);
        set
        {
            if (BacktestViewModelBase.ParseNumber(value) is { } hour && hour != _settings.BacktestEndHour)
            {
                _settings.SetBacktestEndHour(hour);
                OnConditionChanged();
            }
        }
    }

    /// <summary>複合モードで使うポイント数（モードごとに、時間帯に収まるポイントを順位の高い順に）。</summary>
    public string PointCountLabel
    {
        get => BacktestViewModelBase.CountLabel(_settings.BacktestMaxPoints);
        set
        {
            if (BacktestViewModelBase.ParseNumber(value) is { } count && count != _settings.BacktestMaxPoints)
            {
                _settings.BacktestMaxPoints = count;
                OnConditionChanged();
            }
        }
    }

    /// <summary>取引日の曜日（月〜土。土曜は早朝だけ市場が開いている）。</summary>
    public IReadOnlyList<WeekdayOptionViewModel> Weekdays { get; }

    /// <summary>ほかの画面で変えた値を曜日のボタンへ写している間（写すたびに読み直さないように）。</summary>
    private bool _syncingWeekdays;

    private void OnWeekdayChanged()
    {
        if (!_initialized || _syncingWeekdays)
        {
            return;
        }

        _settings.BacktestWeekdays.Clear();
        _settings.BacktestWeekdays.UnionWith(Weekdays.Where(w => w.IsChecked).Select(w => w.Day));
        _settings.NotifyBacktestWeekdaysChanged();
        OnConditionChanged();
    }

    [RelayCommand]
    private void SelectAllWeekdays()
    {
        var target = !Weekdays.All(w => w.IsChecked);
        foreach (var w in Weekdays)
        {
            w.IsChecked = target;
        }
    }

    /// <summary>最適化確認・バックテストの画面で変えた取引時間帯・曜日・ポイント数を、この画面の表示へ写す。</summary>
    private void SyncConditions()
    {
        _syncingWeekdays = true;
        foreach (var w in Weekdays)
        {
            w.IsChecked = _settings.BacktestWeekdays.Contains(w.Day);
        }

        _syncingWeekdays = false;
        OnPropertyChanged(nameof(StartHourLabel));
        OnPropertyChanged(nameof(EndHourLabel));
        OnPropertyChanged(nameof(PointCountLabel));
        UpdateConditionLabel();
    }

    /// <summary>
    /// 取引時間帯・曜日・ポイント数を変えたとき。複合モードはポイント数を時間帯に収まるものから選ぶので読み直し、
    /// 銘柄別は読み込み済みのエントリーを絞り込み直すだけ。
    /// </summary>
    private async void OnConditionChanged()
    {
        OnPropertyChanged(nameof(StartHourLabel));
        OnPropertyChanged(nameof(EndHourLabel));
        OnPropertyChanged(nameof(PointCountLabel));
        UpdateConditionLabel();
        if (IsComposite)
        {
            _scrolledKey = string.Empty;
            await LoadWithIndicatorAsync();
            return;
        }

        RefreshAndScroll();
    }

    /// <summary>取引時間帯・ポイント数・曜日（読み込みの条件の比較と、曜日が対象外の日の案内に使う）。</summary>
    private string ConditionLabel { get; set; } = string.Empty;

    private PointFilter ConditionFilter => new(_settings.BacktestStartHour, _settings.BacktestEndHour, _settings.BacktestMaxPoints);

    private void UpdateConditionLabel()
    {
        var f = ConditionFilter;
        var days = Weekdays.Where(w => _settings.BacktestWeekdays.Contains(w.Day)).Select(w => w.Label).ToList();
        var dayLabel = days.Count == Weekdays.Count ? "月〜土" : days.Count == 0 ? "曜日なし" : string.Concat(days);
        ConditionLabel = $"{f.StartHour}:00–{f.EndHour}:00 · 上位{f.MaxPoints} · {dayLabel}";
    }

    [RelayCommand]
    private void SelectAllSymbols()
    {
        var target = !SymbolOptions.All(s => s.IsChecked);
        foreach (var s in SymbolOptions)
        {
            s.IsChecked = target;
        }
    }

    /// <summary>銘柄を変えたら記憶する。複合モードは銘柄の組み合わせで順位が変わるので読み直し、銘柄別は絞り込み直すだけ。</summary>
    private async void OnSymbolChanged()
    {
        OnPropertyChanged(nameof(SymbolLabel));
        OnPropertyChanged(nameof(SymbolListLabel));
        if (!_initialized)
        {
            return;
        }

        var ids = SelectedSymbolIds;
        _settings.SetEntriesSymbols(ids.Count == SymbolOptions.Count ? null : ids);
        if (_applying)
        {
            return;
        }

        if (IsComposite)
        {
            _scrolledKey = string.Empty;
            await LoadWithIndicatorAsync();
            return;
        }

        RefreshAndScroll();
    }

    public IReadOnlyList<string> SideOptions { get; } = [All, "LONG", "SHORT"];

    public IReadOnlyList<string> ModeOptions { get; }

    /// <summary>順位（銘柄 × モードごとのポイントの順位 = モードの SQL の並び）の上位 N 件に絞る。複合モードでは使わない。</summary>
    public IReadOnlyList<string> RankOptions { get; } = [All, "上位5", "上位10", "上位20", "上位30", "上位50"];

    // 列見出しの並べ替え。押すたびに「その列で並べる → 逆順 → 既定（Entry 時刻順）」と切り替える（記憶しない）
    public EntrySortHeaderViewModel RankHeader { get; } = new(EntrySortKey.Rank, "順位");
    public EntrySortHeaderViewModel PairHeader { get; } = new(EntrySortKey.Pair, "銘柄");
    public EntrySortHeaderViewModel TimeHeader { get; } = new(EntrySortKey.Time, "日付・時間帯");
    public EntrySortHeaderViewModel DirectionHeader { get; } = new(EntrySortKey.Direction, "方向");
    public EntrySortHeaderViewModel ModeHeader { get; } = new(EntrySortKey.Mode, "モード");
    public EntrySortHeaderViewModel WinRateHeader { get; } = new(EntrySortKey.WinRate, "平均勝率");
    public EntrySortHeaderViewModel AverageHeader { get; } = new(EntrySortKey.Average, "平均（1回）");
    public EntrySortHeaderViewModel ResultHeader { get; } = new(EntrySortKey.Result, "結果");

    private IEnumerable<EntrySortHeaderViewModel> SortHeaders =>
        [RankHeader, PairHeader, TimeHeader, DirectionHeader, ModeHeader, WinRateHeader, AverageHeader, ResultHeader];

    /// <summary>並べ替えに使っている列（null は既定の Entry 時刻順）と向き。</summary>
    private (EntrySortKey Key, bool Descending)? _sort;

    [RelayCommand]
    private void Sort(EntrySortHeaderViewModel header)
    {
        _sort = _sort is not { } current || current.Key != header.Key
            ? (header.Key, header.DescendingFirst)
            : current.Descending != header.DescendingFirst
                ? null
                : (header.Key, !current.Descending);
        foreach (var h in SortHeaders)
        {
            h.IsAscending = _sort is { } s && s.Key == h.Key && !s.Descending;
            h.IsDescending = _sort is { } d && d.Key == h.Key && d.Descending;
        }

        RefreshAndScroll();
    }

    /// <summary>
    /// 表示する行を並べ替える。値がないもの（未確定の結果、統計のないポイント）は向きによらず末尾に置き、
    /// 同じ値は Entry 時刻 → 銘柄 → モードの順（既定の並び）にする。
    /// </summary>
    private List<EntryItem> Sort(List<EntryItem> items)
    {
        if (_sort is not { } sort)
        {
            return items;
        }

        Func<EntryItem, IComparable?> key = sort.Key switch
        {
            EntrySortKey.Rank => i => i.Info.Point.Rank,
            EntrySortKey.Pair => i => i.Info.Symbol.Id,
            EntrySortKey.Time => i => i.EntryJst,
            EntrySortKey.Direction => i => (int)i.Info.Point.Direction,
            EntrySortKey.Mode => i => i.Info.Mode.Name,
            EntrySortKey.WinRate => i => i.Info.Summary?.WinRateAvg,
            EntrySortKey.Average => i => i.Info.Summary is { } summary ? i.Info.Symbol.ToUnits(summary.MaxProfitBase) : null,
            _ => i => i.Trade.Status == TradeStatus.Settled ? i.Info.Symbol.ToUnits(i.Trade.Net) : null,
        };
        var ordered = items.OrderBy(i => key(i) is null);
        ordered = sort.Descending ? ordered.ThenByDescending(key) : ordered.ThenBy(key);
        return [.. ordered.ThenBy(i => i.EntryJst).ThenBy(i => i.Info.Symbol.Id, StringComparer.Ordinal).ThenBy(i => i.Info.Mode.Name, StringComparer.Ordinal)];
    }

    /// <summary>一覧の行。行の顔ぶれが変わるときは、組み立て終えた一覧で 1 回だけ差し替える。</summary>
    [ObservableProperty]
    public partial ObservableCollection<EntryRowViewModel> Rows { get; set; } = [];

    /// <summary>表示中の日（JST）。</summary>
    [ObservableProperty]
    public partial DateOnly SelectedDate { get; set; } = Jst.Today;

    [ObservableProperty]
    public partial string DateLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsToday { get; set; }

    [ObservableProperty]
    public partial string SelectedSide { get; set; } = All;

    [ObservableProperty]
    public partial string SelectedMode { get; set; } = All;

    [ObservableProperty]
    public partial string SelectedRank { get; set; } = "上位10";

    [ObservableProperty]
    public partial int Count { get; set; }

    [ObservableProperty]
    public partial string NextPair { get; set; } = "—";

    [ObservableProperty]
    public partial string NextPrefix { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NextCountdown { get; set; } = "--:--";

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    /// <summary>画面を開いたときと日を移動したときの読み込み中（1 分ごとの読み直しでは出さない）。</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string EmptyMessage { get; set; } = string.Empty;

    // その日のサマリー（結果が確定した取引。値幅は銘柄の表示単位、複数の銘柄なら各銘柄の表示単位で合算した pips）
    [ObservableProperty]
    public partial string SumTrades { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SumWinLoss { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SumWinRate { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SumAverage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SumTotal { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool SumTotalPositive { get; set; }

    [ObservableProperty]
    public partial bool SumTotalNegative { get; set; }

    [ObservableProperty]
    public partial bool SumTotalNone { get; set; }

    /// <summary>アクティブな行（次回・進行中）を一覧で見える位置へスクロールしてほしいとき。</summary>
    public event EventHandler<EntryRowViewModel>? ScrollRequested;

    public async void Start()
    {
        _workspace.Changed += OnWorkspaceChanged;

        // 表示する日は記憶しない（開くたびに今日）。日が変わると読み直しは OnSelectedDateChanged が行う
        if (SelectedDate != Jst.Today)
        {
            Activate();
            SelectedDate = Jst.Today;
            return;
        }

        // 最適化確認・バックテストの画面で条件を変えていることがあるので、開くたびに表示を合わせる
        SyncConditions();
        if (_loadedFor == (SelectedDate, LoadSource))
        {
            // 描画済みの一覧をそのまま出し、最新の状態は裏で読み直してその場で反映する
            Activate();
            await LoadAsync();
            return;
        }

        await LoadWithIndicatorAsync();
        Activate();
    }

    public void Stop()
    {
        _workspace.Changed -= OnWorkspaceChanged;
        Deactivate();
    }

    [RelayCommand]
    private void PreviousDay() => SelectedDate = SelectedDate.AddDays(-1);

    [RelayCommand]
    private void NextDay() => SelectedDate = SelectedDate.AddDays(1);

    [RelayCommand]
    private void Today() => SelectedDate = Jst.Today;

    async partial void OnSelectedDateChanged(DateOnly value)
    {
        UpdateDateLabel();
        _scrolledKey = string.Empty;
        await LoadWithIndicatorAsync();
    }

    private async void OnWorkspaceChanged(object? sender, EventArgs e) => await LoadAsync();

    /// <summary>
    /// 読み込んで一覧を差し替える。すぐ終わるときは今の一覧を出したまま差し替え、
    /// <see cref="LoadingIndicatorDelay"/> より長くかかるときだけ一覧を空けて「読み込み中」を出す。
    /// </summary>
    private async Task LoadWithIndicatorAsync()
    {
        var load = LoadAsync();
        if (await Task.WhenAny(load, Task.Delay(LoadingIndicatorDelay)) != load)
        {
            IsLoading = true;
            Rows = [];
            IsEmpty = false;
        }

        try
        {
            await load;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>読み込みの通し番号。毎分の読み直しと分析結果の更新が重なったとき、古い読み込みの結果で新しい結果を上書きしない。</summary>
    private int _loadVersion;

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        var day = SelectedDate;
        _loadedMinute = Format.StartOfMinute(Jst.Now);
        var source = LoadSource;
        IReadOnlyList<EntryItem> items, upcoming;
        if (IsComposite)
        {
            var points = await CompositePointsAsync();
            items = await _workspace.GetEntriesForDayAsync(day, points);
            upcoming = await _workspace.GetEntriesAsync(Jst.Now, points);
        }
        else
        {
            items = await _workspace.GetEntriesForDayAsync(day);
            upcoming = await _workspace.GetEntriesAsync(Jst.Now);
        }

        // 読み込み中に別の日や条件へ切り替えていたり、新しい読み込みが始まっていたら、そちらに任せる
        if (version != _loadVersion || day != SelectedDate || source != LoadSource)
        {
            return;
        }

        (_items, _upcoming) = (items, upcoming);
        _loadedFor = (day, source);
        Refresh();
    }

    /// <summary>
    /// 読み込む対象。銘柄別は有効な銘柄のポイントをすべて読んで絞り込むだけなので「銘柄別」、
    /// 複合モードは銘柄の組み合わせと条件でポイントが変わるので、それらを含める。
    /// </summary>
    private string LoadSource =>
        IsComposite ? $"複合|{string.Join(",", SelectedSymbolIds)}|{ConditionLabel}" : "銘柄別";

    /// <summary>
    /// 複合モードのポイント: 選んだ銘柄だけで作った複合ポイントを、モードごとに取引時間帯とポイント数で絞る
    /// （最適化確認の複合ポイントと同じ選び方。曜日は取引日で絞るので <see cref="Filter"/> で行う）。
    /// </summary>
    private async Task<IReadOnlyList<PointInfo>> CompositePointsAsync()
    {
        var symbols = SelectedSymbols;
        if (symbols.Count == 0)
        {
            return [];
        }

        var filter = ConditionFilter;
        var points = await _workspace.GetCompositePointsAsync(symbols);
        return [.. points.GroupBy(p => p.Mode.Name).SelectMany(g =>
        {
            var allowed = filter.Select(g.Select(p => p.Point)).ToHashSet();
            return g.Where(p => allowed.Contains(p.Point));
        })];
    }

    /// <summary>銘柄別と複合モードは別のポイントなので、切り替えたら読み直す。</summary>
    async partial void OnIsCompositeChanged(bool value)
    {
        if (!_initialized)
        {
            return;
        }

        _settings.EntriesComposite = value;
        if (_applying)
        {
            return;
        }

        _scrolledKey = string.Empty;
        await LoadWithIndicatorAsync();
    }

    partial void OnSelectedSideChanged(string value)
    {
        if (_initialized)
        {
            _settings.EntriesSide = value;
            if (!_applying)
            {
                RefreshAndScroll();
            }
        }
    }

    partial void OnSelectedModeChanged(string value)
    {
        if (_initialized)
        {
            _settings.EntriesMode = value;
            if (!_applying)
            {
                RefreshAndScroll();
            }
        }
    }

    partial void OnSelectedRankChanged(string value)
    {
        if (!_applying)
        {
            RefreshAndScroll();
        }
    }

    /// <summary>条件を写している間（写し終えてから画面を開くときに 1 回だけ読み直す）。</summary>
    private bool _applying;

    /// <summary>
    /// バックテストの画面の条件を反映する（このあと <see cref="Start"/> で読み直す）。
    /// 銘柄が複数なら複合モード、1 つなら銘柄別。バックテストは両方向を評価するので方向は「すべて」、
    /// 銘柄別の順位はバックテストのポイント数に合わせる。有効でないモードはエントリーに出ないので「すべて」にする。
    /// </summary>
    public void ApplyConditions(EntryConditions conditions)
    {
        _applying = true;
        try
        {
            if (conditions.SymbolIds.Any(id => SymbolOptions.Any(o => o.Id == id)))
            {
                foreach (var option in SymbolOptions)
                {
                    option.IsChecked = conditions.SymbolIds.Contains(option.Id);
                }
            }

            IsComposite = SelectedSymbolIds.Count >= 2;
            SelectedSide = All;
            SelectedMode = ModeOptions.Contains(conditions.Mode) ? conditions.Mode : All;
            var rank = BacktestViewModelBase.CountLabel(_settings.BacktestMaxPoints);
            SelectedRank = RankOptions.Contains(rank) ? rank : All;
            _scrolledKey = string.Empty;
        }
        finally
        {
            _applying = false;
        }
    }

    private void RefreshAndScroll()
    {
        _scrolledKey = string.Empty;
        Refresh();
    }

    /// <summary>表示中のエントリーのうち、結果が確定した取引の集計（バックテストの合計行と同じ項目）。</summary>
    private void UpdateSummary(IReadOnlyList<EntryItem> visible)
    {
        var settled = visible.Where(i => i.Trade.Status == TradeStatus.Settled).ToList();
        var symbols = visible.Select(i => i.Info.Symbol).DistinctBy(s => s.Id).ToList();
        var unit = symbols.Count == 1 ? symbols[0].UnitLabel : "pips";
        var wins = settled.Count(i => i.Trade.IsWin);
        var total = settled.Sum(i => i.Info.Symbol.ToUnits(i.Trade.Net));
        SumTrades = $"確定 {settled.Count} / {visible.Count} 件";
        SumWinLoss = settled.Count == 0 ? string.Empty : $"{wins}勝 {settled.Count - wins}敗";
        SumWinRate = settled.Count == 0 ? "—" : Format.Percent((double)wins / settled.Count);
        SumAverage = settled.Count == 0 ? "—" : $"{Format.Signed(total / settled.Count)} {unit}";
        SumTotal = settled.Count == 0 ? "—" : $"{Format.Signed(total)} {unit}";
        SumTotalPositive = settled.Count > 0 && total > 0;
        SumTotalNegative = settled.Count > 0 && total <= 0;
        SumTotalNone = settled.Count == 0;
    }

    private int MaxRank => int.TryParse(SelectedRank.Replace("上位", string.Empty), out var n) ? n : int.MaxValue;

    private void UpdateDateLabel()
    {
        IsToday = SelectedDate == Jst.Today;
        DateLabel = Format.LongDate(SelectedDate.ToDateTime(TimeOnly.MinValue));
    }

    /// <summary>
    /// 表示する行の絞り込み。どちらも取引日の曜日で絞る。
    /// 複合モードのポイントは選んだ銘柄だけで作り、時間帯とポイント数で選んであるので、銘柄・時間帯・順位では絞らない。
    /// 銘柄別は選んだ銘柄で絞り、銘柄 × モードごとに、時間帯（Entry から Close まで収まるもの）に収まるポイントを
    /// 順位の高い順に「順位」の件数まで使う（バックテスト・最適化確認のポイント数と同じ選び方）。
    /// </summary>
    private List<EntryItem> Filter(IReadOnlyList<EntryItem> items)
    {
        var weekdays = _settings.BacktestWeekdays;
        var allowed = IsComposite ? null : SelectedPoints(items);
        return [.. items
            .Where(i => weekdays.Contains(i.Trade.TradeDate.DayOfWeek))
            .Where(i => allowed is null || allowed.Contains(i.Info))
            .Where(i => SelectedSide == All || (SelectedSide == "LONG") == (i.Info.Point.Direction == TradeDirection.Long))
            .Where(i => SelectedMode == All || i.Info.Mode.Name == SelectedMode)];
    }

    /// <summary>銘柄別で使うポイント: 選んだ銘柄の、銘柄 × モードごとに時間帯に収まるポイントを順位の高い順に「順位」の件数まで。</summary>
    private HashSet<PointInfo> SelectedPoints(IReadOnlyList<EntryItem> items)
    {
        var symbols = SelectedSymbolIds.ToHashSet();
        var filter = ConditionFilter with { MaxPoints = MaxRank };
        return [.. items
            .Select(i => i.Info)
            .Distinct()
            .Where(p => symbols.Contains(p.Symbol.Id))
            .GroupBy(p => (p.Symbol.Id, p.Mode.Name))
            .SelectMany(g =>
            {
                var points = filter.Select(g.Select(p => p.Point)).ToHashSet();
                return g.Where(p => points.Contains(p.Point));
            })];
    }

    /// <summary>
    /// 毎秒: 時計と「次」のカウントダウンだけを更新する。
    /// エントリーは分単位なので、分が変わったとき（00 秒）に読み直して、結果と行の状態（完了・次回・進行中）を判定し直す。
    /// </summary>
    protected override void Update(DateTime localNow)
    {
        var now = Jst.Now;
        if (IsLoading)
        {
            return;
        }

        if (Format.StartOfMinute(now) != _loadedMinute)
        {
            _ = LoadAsync();
        }

        UpdateCountdown(now);
    }

    private void UpdateCountdown(DateTime now)
    {
        if (_next is null)
        {
            (NextPair, NextPrefix, NextCountdown) = ("—", string.Empty, "--:--");
            return;
        }

        NextPair = _next.Info.Symbol.Id;
        var started = _next.EntryJst <= now;
        NextPrefix = started ? string.Empty : "あと ";
        NextCountdown = started ? "進行中" : Format.Countdown(_next.EntryJst - now);
    }

    /// <summary>読み込み・絞り込みの変更のたびに、一覧と行の状態を作り直す。</summary>
    private void Refresh()
    {
        var now = Jst.Now;
        UpdateDateLabel();
        var upcoming = Filter(_upcoming);
        var nextIndex = EntryFormatter.NextIndex(upcoming, now);
        _next = nextIndex >= 0 ? upcoming[nextIndex] : null;
        UpdateCountdown(now);

        // 絞り込み済みの行を先に組み立ててから、一覧に反映する
        var visible = Sort(Filter(_items));
        var built = new List<EntryRowViewModel>(visible.Count);
        EntryRowViewModel? active = null;
        var activeKey = string.Empty;
        for (var k = 0; k < visible.Count; k++)
        {
            var item = visible[k];
            var summary = item.Info.Summary;
            var settled = item.Trade.Status == TradeStatus.Settled;
            var isNext = _next is not null && item.Info == _next.Info && item.EntryJst == _next.EntryJst;
            var row = new EntryRowViewModel
            {
                Key = $"{item.Info.Symbol.Id}|{item.Info.Mode.Name}|{item.Info.Point.Direction}|{item.EntryJst:o}|{item.Info.Point.HoldMinutes}",
                Rank = $"#{item.Info.Point.Rank}",
                Pair = item.Info.Symbol.Id,
                Date = Format.MonthDay(item.EntryJst) + " ",
                Slot = $"{Format.HourMinute(item.EntryJst)} – {Format.HourMinute(item.CloseJst)}",
                IsLong = item.Info.Point.Direction == TradeDirection.Long,
                Mode = item.Info.Mode.Name,
                WinRate = summary is null ? double.NaN : summary.WinRateAvg * 100,
                AveragePips = summary is null ? "—" : EntryFormatter.Units(item.Info, summary.MaxProfitBase),
                AveragePositive = summary?.MaxProfitBase > 0,
                Result = EntryFormatter.ResultText(item),
                ResultPositive = settled && item.Trade.Net > 0,
                ResultNegative = settled && item.Trade.Net <= 0,
                IsDone = item.CloseJst <= now,
                IsNext = isNext,
                NextLabel = item.EntryJst <= now ? "進行中" : "次回",
                IsOdd = k % 2 == 1,
            };
            built.Add(row);
            if (isNext)
            {
                active = row;
                activeKey = $"{row.Key}|{item.EntryJst <= now}";
            }
        }

        // 行の顔ぶれが同じ（毎分の更新など）なら状態と結果だけ書き換え、違えば組み立てた一覧で 1 回だけ差し替える
        if (built.Count == Rows.Count && built.Select(r => r.Key).SequenceEqual(Rows.Select(r => r.Key)))
        {
            for (var k = 0; k < built.Count; k++)
            {
                Rows[k].CopyStateFrom(built[k]);
            }

            active = active is null ? null : Rows[built.IndexOf(active)];
        }
        else
        {
            // 差し替えると一覧は先頭に戻るので、アクティブな行へスクロールし直す
            Rows = new ObservableCollection<EntryRowViewModel>(built);
            _scrolledKey = string.Empty;
        }

        Count = Rows.Count;
        UpdateSummary(visible);
        IsEmpty = Rows.Count == 0;
        EmptyMessage = !_workspace.HasAnalysis
            ? _workspace.NeedsReanalysis
                ? "アプリの更新で計算方式が変わったため、分析結果を使えません。データ抽出設定の「分析を実行」で分析し直してください"
                : "分析結果がありません。データ抽出設定の「分析を実行」で、Dukascopy から取り込んで分析してください"
            : SymbolOptions.All(s => !s.IsChecked) ? "銘柄を選んでください"
            : IsComposite && !_workspace.Modes.Enabled.Any(m => m.EffectiveCompositeMetric is not null)
                ? "複合モードに対応した有効なモード（勝率重視・利益効率など、銘柄をまたいで比べられる指標のモード）がありません"
            : SelectedDate.DayOfWeek == DayOfWeek.Sunday ? "日曜は FX 市場が開いていないので、エントリーはありません"
            : !_settings.BacktestWeekdays.Contains(SelectedDate.DayOfWeek)
                ? "この日の曜日は選んだ曜日に含まれていません"
            : _items.Count == 0 ? "この日のエントリーポイントはありません" : "条件に合うエントリーがありません";

        if (active is not null && activeKey != _scrolledKey)
        {
            _scrolledKey = activeKey;
            ScrollRequested?.Invoke(this, active);
        }
    }
}
