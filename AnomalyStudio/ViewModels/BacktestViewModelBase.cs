using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using AnomalyStudio.Core;
using AnomalyStudio.Core.Analysis;
using AnomalyStudio.Core.Modes;
using AnomalyStudio.Core.Symbols;
using AnomalyStudio.Core.Trading;

namespace AnomalyStudio.ViewModels;

/// <summary>
/// 損益の表示単位。銘柄ごとはその銘柄の単位（pips / USD）。
/// 複合ポイントは各取引をその銘柄の表示単位に換算して合算し、pips と表記する（XAUUSD などは 1 単位 = 表示単位 1）。
/// </summary>
public sealed record BacktestUnits(string Label, Func<BacktestTrade, double> Value, Func<double, string> Format)
{
    public static BacktestUnits For(SymbolProfile symbol) => new(symbol.UnitLabel, t => symbol.ToUnits(t.Trade.Net), Helpers.Format.Signed);

    public static BacktestUnits Composite(Func<string, SymbolProfile?> find) =>
        new("pips", t => find(t.SymbolId) is { } s ? s.ToUnits(t.Trade.Net) : double.NaN, Helpers.Format.Signed);
}

/// <summary>評価サイクル 1 期間分の集計。値幅は表示単位（pips / USD / %）。</summary>
public sealed record BacktestPeriod(string Label, DateOnly Start, int Trades, int Wins, double Average, double Total)
{
    public int Losses => Trades - Wins;
    public double WinRate => Trades == 0 ? 0 : (double)Wins / Trades;
}

/// <summary>取引数・勝敗・平均・合計の表示（期間の行とポイントの行で共通）。値幅は表示単位。</summary>
public abstract class BacktestStatsRowViewModel(int trades, int wins, double average, double total, Func<double, string> format) : ObservableObject
{
    public string Trades => trades.ToString(CultureInfo.InvariantCulture);
    public string Wins => wins.ToString(CultureInfo.InvariantCulture);
    public string Losses => (trades - wins).ToString(CultureInfo.InvariantCulture);
    public string WinRate => trades == 0 ? "—" : Format.Percent((double)wins / trades);
    public double WinRatePercent => trades == 0 ? 0 : (double)wins / trades * 100;
    public string AveragePips => trades == 0 ? "—" : format(average);
    public bool AveragePositive => average > 0;
    public bool AverageNegative => !AveragePositive;
    public string TotalPips => format(total);
    public bool TotalPositive => total > 0;
    public bool TotalNegative => !TotalPositive;
}

/// <summary>評価サイクル 1 期間の行。展開すると、その期間のポイントごとの成績を Entry 時刻順に出す。</summary>
/// <param name="showSymbol">ポイントに銘柄を付けて出す（複合ポイント）。</param>
public sealed partial class BacktestRowViewModel(
    BacktestPeriod row, bool isOdd, IReadOnlyList<BacktestTrade> trades, BacktestUnits units, bool showSymbol)
    : BacktestStatsRowViewModel(row.Trades, row.Wins, row.Average, row.Total, units.Format)
{
    public string Label => row.Label;
    public bool IsOdd => isOdd;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>ポイントごとの行（初めて展開したときに作る）。</summary>
    [ObservableProperty]
    public partial IReadOnlyList<BacktestPointRowViewModel> Points { get; set; } = [];

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && Points.Count == 0)
        {
            Points = BacktestPointRowViewModel.Build(trades, units, showSymbol);
        }
    }
}

/// <summary>期間内の 1 ポイントの成績。結果が確定した取引だけを集計し、休場などで取引がない日は数えない。</summary>
public sealed class BacktestPointRowViewModel(
    EntryPoint point, string? symbolId, int trades, int wins, double average, double total, Func<double, string> format)
    : BacktestStatsRowViewModel(trades, wins, average, total, format)
{
    public string Rank => $"#{point.Rank}";
    public string Direction => CandidateGrid.DirectionName(point.Direction);
    public bool IsLong => point.Direction == TradeDirection.Long;
    public bool IsShort => !IsLong;
    public string Time =>
        $"{(symbolId is null ? string.Empty : symbolId + "  ")}{Clock(point.EntryMinute)} → {Clock(point.CloseMinute)}（{point.HoldMinutes}分）";

    public static IReadOnlyList<BacktestPointRowViewModel> Build(IReadOnlyList<BacktestTrade> trades, BacktestUnits units, bool showSymbol) =>
        [.. trades
            .GroupBy(t => (t.SymbolId, t.Trade.Point.Direction, t.Trade.Point.EntryMinute, t.Trade.Point.HoldMinutes))
            .OrderBy(g => g.Key.EntryMinute).ThenBy(g => g.Key.HoldMinutes).ThenBy(g => g.Key.Direction).ThenBy(g => g.Key.SymbolId, StringComparer.Ordinal)
            .Select(g =>
            {
                var settled = g.Where(t => t.Trade.Status == TradeStatus.Settled).ToList();
                var total = settled.Sum(units.Value);
                return new BacktestPointRowViewModel(
                    g.First().Trade.Point, showSymbol ? g.Key.SymbolId : null, settled.Count, settled.Count(t => t.Trade.IsWin),
                    settled.Count == 0 ? 0 : total / settled.Count, total, units.Format);
            })];

    private static string Clock(int minute) => $"{minute / 60:00}:{minute % 60:00}";
}

/// <summary>銘柄の選択肢（複数選ぶと、選んだ銘柄の複合ポイントで評価する）。</summary>
public sealed partial class SymbolOptionViewModel(string id, bool isChecked, Action changed) : ObservableObject
{
    public string Id => id;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked;

    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>曜日フィルターの 1 項目。</summary>
public sealed partial class WeekdayOptionViewModel(string label, DayOfWeek day, bool isChecked, Action changed) : ObservableObject
{
    /// <summary>曜日の選択肢（JST の取引日）。土曜は早朝（NY クローズまで）だけ市場が開いている。日曜は開いていない。</summary>
    public static IReadOnlyList<DayOfWeek> TradingDays { get; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday];

    public string Label => label;

    public DayOfWeek Day => day;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked;

    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>取引の計算結果と、画面に出す説明。</summary>
public sealed record BacktestComputation(IReadOnlyList<BacktestTrade> Trades, string Message);

/// <summary>
/// 画面ごとの条件（最適化確認からバックテストへ写すもの）。取引時間帯・ポイント数・曜日は設定に保存して両方の画面で共通なので含めない。
/// </summary>
public sealed record BacktestConditions(
    IReadOnlyList<string> SymbolIds, string Mode, BacktestCycle Cycle, DateTimeOffset? StartDate, DateTimeOffset? EndDate);

/// <summary>
/// バックテスト系画面（ウォークフォワード・バックテスト、最適化確認）の共通部分。
/// 条件の選択、評価サイクルごとの集計、実行中ダイアログの状態、CSV 保存を持ち、取引の計算方法だけを派生クラスが決める。
/// </summary>
public abstract partial class BacktestViewModelBase : ObservableObject
{
    private readonly bool _initialized;
    private readonly AppSettings _settings;
    private IReadOnlyList<BacktestTrade> _trades = [];

    /// <summary>取引時間帯とポイント数で絞り込んだ取引（集計・展開・CSV の対象）。</summary>
    private IReadOnlyList<BacktestTrade> _filtered = [];
    private CancellationTokenSource? _cts;

    protected BacktestViewModelBase(AnomalyWorkspace workspace, AppSettings settings)
    {
        Workspace = workspace;
        _settings = settings;
        var enabled = workspace.Symbols.Enabled;
        SymbolOptions = [.. enabled.Select((s, i) => new SymbolOptionViewModel(s.Id, i == 0, OnSymbolChanged))];
        Weekdays = [.. WeekdayOptionViewModel.TradingDays
            .Select(d => new WeekdayOptionViewModel(
                Format.Weekday(d).ToString(),
                d, settings.BacktestWeekdays.Contains(d), OnWeekdayChanged))];
        Modes = [.. workspace.Modes.All.Select(m => m.Name)];
        SelectedMode = workspace.Modes.Enabled.FirstOrDefault()?.Name ?? Modes.FirstOrDefault() ?? string.Empty;
        // 既定の検証期間は直近 1 年（終了は今日。取引日は JST なので PC のタイムゾーンによらず JST の今日）
        var today = Jst.Today.ToDateTime(TimeOnly.MinValue);
        EndDate = today;
        StartDate = today.AddYears(-1);
        _initialized = true;
    }

    protected AnomalyWorkspace Workspace { get; }

    /// <summary>CSV のファイル名の先頭。</summary>
    protected abstract string ExportName { get; }

    /// <summary>銘柄の選択肢。1 つなら銘柄ごとのポイント、複数なら選んだ銘柄をまとめた複合ポイントで評価する。</summary>
    public IReadOnlyList<SymbolOptionViewModel> SymbolOptions { get; }

    /// <summary>
    /// 銘柄ボタンの表示（例 USDJPY）。複数選ぶと銘柄を並べずに「（複数選択中）」とし、ボタンの幅を変えない
    /// （並べると右側の実行ボタンなどがウィンドウの外へ押し出される）。
    /// </summary>
    public string SymbolLabel
    {
        get
        {
            var ids = SelectedSymbolIds;
            return ids.Count switch
            {
                0 => "未選択",
                1 => ids[0],
                _ => "（複数選択中）",
            };
        }
    }

    /// <summary>選んだ銘柄の一覧（ツールチップと実行中の条件に出す）。</summary>
    public string SymbolListLabel
    {
        get
        {
            var ids = SelectedSymbolIds;
            return ids.Count == 0 ? "未選択" : ids.Count == 1 ? ids[0] : $"複合: {string.Join("・", ids)}";
        }
    }

    private List<string> SelectedSymbolIds => [.. SymbolOptions.Where(s => s.IsChecked).Select(s => s.Id)];

    /// <summary>選んだ銘柄（選択肢の順）。</summary>
    protected IReadOnlyList<SymbolProfile> SelectedSymbols =>
        [.. SymbolOptions.Where(o => o.IsChecked).Select(o => Workspace.Symbols.Find(o.Id)).OfType<SymbolProfile>()];

    private void OnSymbolChanged()
    {
        OnPropertyChanged(nameof(SymbolLabel));
        OnPropertyChanged(nameof(SymbolListLabel));
        NotifyConditionChanged(cycleOnly: false);
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

    [RelayCommand]
    private void SelectAllWeekdays()
    {
        var target = !Weekdays.All(w => w.IsChecked);
        foreach (var w in Weekdays)
        {
            w.IsChecked = target;
        }
    }

    public IReadOnlyList<string> Modes { get; }

    public ObservableCollection<BacktestRowViewModel> Rows { get; } = [];

    public IReadOnlyList<string> StartHourOptions { get; } = [.. Enumerable.Range(0, 24).Select(HourLabel)];

    public IReadOnlyList<string> EndHourOptions { get; } = [.. Enumerable.Range(1, 24).Select(HourLabel)];

    public IReadOnlyList<string> PointCountOptions { get; } = [.. new[] { 5, 10, 20, 30, 50 }.Select(CountLabel)];

    /// <summary>取引時間帯の開始。終了以降を選んだら終了を 1 時間後へずらす。</summary>
    public string StartHourLabel
    {
        get => HourLabel(_settings.BacktestStartHour);
        set
        {
            if (ParseNumber(value) is not { } hour || hour == _settings.BacktestStartHour)
            {
                return;
            }

            _settings.SetBacktestStartHour(hour);
            OnFilterChanged();
        }
    }

    /// <summary>取引時間帯の終了。開始以前を選んだら開始を 1 時間前へずらす。</summary>
    public string EndHourLabel
    {
        get => HourLabel(_settings.BacktestEndHour);
        set
        {
            if (ParseNumber(value) is not { } hour || hour == _settings.BacktestEndHour)
            {
                return;
            }

            _settings.SetBacktestEndHour(hour);
            OnFilterChanged();
        }
    }

    public string PointCountLabel
    {
        get => CountLabel(_settings.BacktestMaxPoints);
        set
        {
            if (ParseNumber(value) is not { } count || count == _settings.BacktestMaxPoints)
            {
                return;
            }

            _settings.BacktestMaxPoints = count;
            OnFilterChanged();
        }
    }

    /// <summary>集計する取引日の曜日（月〜土。土曜は早朝だけ取引がある）。設定に保存し、両方の画面で共通。</summary>
    public IReadOnlyList<WeekdayOptionViewModel> Weekdays { get; }

    private void OnWeekdayChanged()
    {
        _settings.BacktestWeekdays.Clear();
        _settings.BacktestWeekdays.UnionWith(Weekdays.Where(w => w.IsChecked).Select(w => w.Day));
        _settings.NotifyBacktestWeekdaysChanged();
        if (_initialized)
        {
            Aggregate();
        }
    }

    /// <summary>取引時間帯とポイント数（設定に保存し、両方の画面で共通）。</summary>
    protected PointFilter Filter => new(_settings.BacktestStartHour, _settings.BacktestEndHour, _settings.BacktestMaxPoints);

    private string FilterLabel
    {
        get
        {
            var days = Weekdays.Where(w => w.IsChecked).Select(w => w.Label).ToList();
            var dayLabel = days.Count == Weekdays.Count ? string.Empty : $" · {(days.Count == 0 ? "曜日なし" : string.Concat(days))}";
            return $"{HourLabel(Filter.StartHour)}–{HourLabel(Filter.EndHour)} · {CountLabel(Filter.MaxPoints)}{dayLabel}";
        }
    }


    [ObservableProperty]
    public partial string SelectedMode { get; set; }

    [ObservableProperty]
    public partial BacktestCycle Cycle { get; set; } = BacktestCycle.Weekly;

    [ObservableProperty]
    public partial DateTimeOffset? StartDate { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? EndDate { get; set; }

    [ObservableProperty]
    public partial string PeriodHeader { get; set; } = "週";

    [ObservableProperty]
    public partial string UnitLabel { get; set; } = "pips";

    [ObservableProperty]
    public partial string RangeLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SumTrades { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SumWins { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SumLosses { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SumWinRate { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SumAverage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SumTotal { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool SumTotalPositive { get; set; }

    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    // 実行中ダイアログ
    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial double RunPercent { get; set; }

    [ObservableProperty]
    public partial string RunPercentLabel { get; set; } = "0%";

    [ObservableProperty]
    public partial string RunStep { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RunEta { get; set; } = string.Empty;

    public string RunCondition =>
        $"{SymbolListLabel} · {SelectedMode} · {CycleLabel} · {Format.Slash(From.ToDateTime(TimeOnly.MinValue))} – {Format.Slash(To.ToDateTime(TimeOnly.MinValue))}";

    public bool IsDaily
    {
        get => Cycle == BacktestCycle.Daily;
        set { if (value) { Cycle = BacktestCycle.Daily; } }
    }

    public bool IsWeekly
    {
        get => Cycle == BacktestCycle.Weekly;
        set { if (value) { Cycle = BacktestCycle.Weekly; } }
    }

    public bool IsMonthly
    {
        get => Cycle == BacktestCycle.Monthly;
        set { if (value) { Cycle = BacktestCycle.Monthly; } }
    }

    private string CycleLabel => Cycle switch
    {
        BacktestCycle.Daily => "毎日",
        BacktestCycle.Weekly => "毎週",
        _ => "毎月",
    };

    private DateOnly To => EndDate?.Date is { } e ? DateOnly.FromDateTime(e) : Jst.Today.AddDays(-1);

    private DateOnly From => StartDate?.Date is { } s && DateOnly.FromDateTime(s) <= To ? DateOnly.FromDateTime(s) : To.AddMonths(-3);

    /// <summary>選んだ銘柄が 1 つならその銘柄（複合や未選択は null）。</summary>
    protected SymbolProfile? Symbol => SelectedSymbols is [var only] ? only : null;

    /// <summary>複数の銘柄を選んだ（選んだ銘柄をまとめた順位で選ぶ複合ポイントで評価する）。</summary>
    protected bool IsComposite => SelectedSymbols.Count >= 2;

    /// <summary>損益の表示単位（複合ポイントは各銘柄の表示単位で合算した pips）。</summary>
    private BacktestUnits Units =>
        IsComposite ? BacktestUnits.Composite(Workspace.Symbols.Find)
        : Symbol is { } symbol ? BacktestUnits.For(symbol)
        : new BacktestUnits("pips", _ => 0, Format.Signed);

    /// <summary>画面を開いたとき。</summary>
    public abstract void Start();

    /// <summary>今の画面ごとの条件（銘柄・モード・評価サイクル・検証期間）。</summary>
    public BacktestConditions Conditions => new(SelectedSymbolIds, SelectedMode, Cycle, StartDate, EndDate);

    /// <summary>
    /// 別の画面の条件を反映する。選択肢にない銘柄・モードは無視する（今の選択を残す）。
    /// 条件が変わるたびに <see cref="OnConditionChanged"/> が呼ばれる。
    /// </summary>
    public void ApplyConditions(BacktestConditions conditions)
    {
        if (conditions.SymbolIds.Any(id => SymbolOptions.Any(o => o.Id == id)))
        {
            foreach (var option in SymbolOptions)
            {
                option.IsChecked = conditions.SymbolIds.Contains(option.Id);
            }
        }

        if (Modes.Contains(conditions.Mode))
        {
            SelectedMode = conditions.Mode;
        }

        Cycle = conditions.Cycle;
        StartDate = conditions.StartDate;
        EndDate = conditions.EndDate;
    }

    /// <summary>条件が変わったとき。<paramref name="cycleOnly"/> は評価サイクルだけが変わった場合。</summary>
    protected abstract void OnConditionChanged(bool cycleOnly);

    /// <summary>取引を計算する。<paramref name="symbol"/> が null なら複合ポイント（<see cref="SelectedSymbols"/> の銘柄）。</summary>
    protected abstract Task<BacktestComputation> ComputeTradesAsync(
        SymbolProfile? symbol,
        ModeDefinition mode,
        DateOnly from,
        DateOnly to,
        BacktestCycle cycle,
        IProgress<WalkForwardProgress> progress,
        CancellationToken cancellationToken);


    partial void OnSelectedModeChanged(string value) => NotifyConditionChanged(cycleOnly: false);

    partial void OnCycleChanged(BacktestCycle value)
    {
        OnPropertyChanged(nameof(IsDaily));
        OnPropertyChanged(nameof(IsWeekly));
        OnPropertyChanged(nameof(IsMonthly));
        NotifyConditionChanged(cycleOnly: true);
    }

    partial void OnStartDateChanged(DateTimeOffset? value) => NotifyConditionChanged(cycleOnly: false);

    partial void OnEndDateChanged(DateTimeOffset? value) => NotifyConditionChanged(cycleOnly: false);

    /// <summary>絞り込みは計算済みの取引に当てはめるだけなので、計算し直さずに集計し直す。</summary>
    private void OnFilterChanged()
    {
        OnPropertyChanged(nameof(StartHourLabel));
        OnPropertyChanged(nameof(EndHourLabel));
        OnPropertyChanged(nameof(PointCountLabel));
        Aggregate();
    }

    internal static string HourLabel(int hour) => $"{hour}:00";

    internal static string CountLabel(int count) => $"上位{count}";

    /// <summary>"8:00" → 8、"上位10" → 10。</summary>
    internal static int? ParseNumber(string? label) =>
        int.TryParse(new string([.. (label ?? string.Empty).Split(':')[0].Where(char.IsAsciiDigit)]), CultureInfo.InvariantCulture, out var n)
            ? n
            : null;

    private void NotifyConditionChanged(bool cycleOnly)
    {
        if (_initialized)
        {
            OnPropertyChanged(nameof(RunCondition));
            OnConditionChanged(cycleOnly);
        }
    }

    [RelayCommand]
    private Task RunAsync() => ComputeAsync(showProgress: true);

    [RelayCommand]
    private void CancelRun()
    {
        _cts?.Cancel();
        IsRunning = false;
        Message = "バックテストを中止しました";
    }

    protected async Task ComputeAsync(bool showProgress)
    {
        var symbol = Symbol;
        var composite = IsComposite;
        var mode = Workspace.Modes.All.FirstOrDefault(m => m.Name == SelectedMode);
        if ((symbol is null && !composite) || mode is null)
        {
            Message = "銘柄とモードを選んでください";
            return;
        }

        if (composite && mode.EffectiveCompositeMetric is null)
        {
            Message = $"モード「{mode.Name}」は複数の銘柄をまとめた評価（複合ポイント）に対応していません（勝率重視・利益効率など、銘柄をまたいで比べられる指標のモードだけ）";
            ClearResults();
            return;
        }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var started = DateTime.Now;
        IsRunning = showProgress;
        RunPercent = 0;
        RunPercentLabel = "0%";
        RunStep = composite ? $"{string.Join("・", SelectedSymbols.Select(s => s.Id))} の 1 分足を読み込み中" : $"{symbol!.Id} の 1 分足を読み込み中";
        RunEta = string.Empty;
        var progress = new Progress<WalkForwardProgress>(p =>
        {
            if (_cts != cts)
            {
                return;
            }

            RunPercent = p.Fraction * 100;
            RunPercentLabel = $"{RunPercent:0}%";
            RunStep = p.Message;
            RunEta = RemainingLabel(DateTime.Now - started, p.Fraction);
        });

        try
        {
            var result = await ComputeTradesAsync(composite ? null : symbol, mode, From, To, Cycle, progress, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            _trades = result.Trades;
            Message = result.Message;
            Aggregate();
        }
        catch (OperationCanceledException)
        {
            // 中止のメッセージは CancelRun が出す
        }
        catch (Exception ex) when (ex is InsufficientDataException or HttpRequestException or IOException
                                       or ModeSqlException or DuckDB.NET.Data.DuckDBException)
        {
            if (_cts == cts)
            {
                Message = ex.Message;
                ClearResults();
            }
        }
        finally
        {
            if (_cts == cts)
            {
                IsRunning = false;
            }
        }
    }

    private static string RemainingLabel(TimeSpan elapsed, double fraction)
    {
        if (fraction < 0.05 || fraction >= 1)
        {
            return string.Empty;
        }

        var remaining = elapsed * ((1 - fraction) / fraction);
        return remaining.TotalMinutes >= 1
            ? $"残り約 {Math.Ceiling(remaining.TotalMinutes):0} 分"
            : $"残り約 {Math.Max(1, Math.Ceiling(remaining.TotalSeconds)):0} 秒";
    }

    protected void ClearResults()
    {
        _trades = [];
        Aggregate();
    }

    protected void Aggregate()
    {
        var units = Units;
        UnitLabel = units.Label;
        PeriodHeader = Cycle switch
        {
            BacktestCycle.Daily => "日",
            BacktestCycle.Weekly => "週",
            _ => "月",
        };

        // 時間帯とポイント数は基準日ごとの順位で選び、そのあと取引日の曜日で絞る（曜日で順位は変えない）
        var weekdays = _settings.BacktestWeekdays;
        // 市場が閉まっている時間（土曜の日中・月曜の開場前）の取引は、足がなく取引できないので除く
        _filtered = [.. Filter.Apply(_trades).Where(t => weekdays.Contains(t.Trade.TradeDate.DayOfWeek) && t.Trade.IsInMarketHours)];
        var settled = _filtered.Where(t => t.Trade.Status == TradeStatus.Settled).ToList();
        var periods = settled
            .GroupBy(t => WalkForwardSchedule.PeriodStart(t.Trade.TradeDate, Cycle))
            .OrderByDescending(g => g.Key)
            .Select(g =>
            {
                var total = g.Sum(units.Value);
                return new BacktestPeriod(PeriodLabel(g.Key), g.Key, g.Count(), g.Count(t => t.Trade.IsWin), total / g.Count(), total);
            })
            .ToList();
        var tradesByPeriod = _filtered.ToLookup(t => WalkForwardSchedule.PeriodStart(t.Trade.TradeDate, Cycle));

        Rows.Clear();
        for (var i = 0; i < periods.Count; i++)
        {
            Rows.Add(new BacktestRowViewModel(periods[i], i % 2 == 1, [.. tradesByPeriod[periods[i].Start]], units, IsComposite));
        }

        RangeLabel = $"{periods.Count}期間 · {FilterLabel}";
        var trades = settled.Count;
        var wins = settled.Count(t => t.Trade.IsWin);
        var sum = settled.Sum(units.Value);
        SumTrades = trades.ToString(CultureInfo.InvariantCulture);
        SumWins = wins.ToString(CultureInfo.InvariantCulture);
        SumLosses = (trades - wins).ToString(CultureInfo.InvariantCulture);
        SumWinRate = trades == 0 ? "—" : Format.Percent((double)wins / trades);
        SumAverage = trades == 0 ? "—" : units.Format(sum / trades);
        SumTotal = units.Format(sum);
        SumTotalPositive = sum > 0;

        // 安定性の指標（取引順の損益列から）と、無作為に選んだ場合との比較
        _statistics = BacktestStatistics.Compute(settled.OrderBy(t => t.Trade.EntryJst).ThenBy(t => t.SymbolId, StringComparer.Ordinal).Select(units.Value));
        StatsLabel = StatsText(_statistics, units);
        _ = UpdateBaselineAsync(settled, units);
    }

    private BacktestStatistics _statistics = BacktestStatistics.Empty;
    private BaselineResult? _baseline;
    private CancellationTokenSource? _baselineCts;

    /// <summary>
    /// 安定性の指標の表示: 平均損益の t 値（±2 を超えると偶然でない目安）、平均の 95% 信頼区間、プロフィットファクター、最大ドローダウン、最大連敗。
    /// </summary>
    private static string StatsText(BacktestStatistics s, BacktestUnits units)
    {
        if (s.Trades == 0)
        {
            return string.Empty;
        }

        var t = double.IsNaN(s.TStat) ? "—" : s.TStat.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
        var ci = double.IsNaN(s.MeanCiHalfWidth) ? "—" : "±" + s.MeanCiHalfWidth.ToString("0.0", CultureInfo.InvariantCulture);
        var pf = double.IsNaN(s.ProfitFactor) ? "—" : double.IsPositiveInfinity(s.ProfitFactor) ? "∞" : s.ProfitFactor.ToString("0.00", CultureInfo.InvariantCulture);
        return $"平均の t 値 {t} · 平均の 95% 信頼区間 {ci} {units.Label} · プロフィットファクター {pf}"
               + $" · 最大ドローダウン {s.MaxDrawdown.ToString("#,0.0", CultureInfo.InvariantCulture)} {units.Label} · 最大連敗 {s.MaxConsecutiveLosses}";
    }

    /// <summary>
    /// ランダム基準: 同じ取引日・件数・取引時間帯で無作為に選んだポイントの成績（固定の種で 200 試行）と実績を比べる。
    /// 172,800 候補から上位を選ぶと偶然でも良く見えるので、無作為より良いかを常に添える。計算は集計の後に非同期で行う。
    /// </summary>
    private async Task UpdateBaselineAsync(IReadOnlyList<BacktestTrade> settled, BacktestUnits units)
    {
        _baselineCts?.Cancel();
        _baseline = null;
        if (settled.Count == 0)
        {
            BaselineLabel = string.Empty;
            return;
        }

        var cts = _baselineCts = new CancellationTokenSource();
        BaselineLabel = "ランダム基準を計算中…";
        try
        {
            // 保有時間を限るモード（勝率重視BO）は、無作為のポイントも同じ保有時間から選ぶ
            var holds = Workspace.Modes.All.FirstOrDefault(m => m.Name == SelectedMode) is { } mode ? ModeDefinition.ParseBuiltIn(mode.Sql)?.Holds : null;
            var result = await Workspace.ComputeBaselineAsync(settled, Filter, holds, cts.Token);
            if (cts.IsCancellationRequested || _baselineCts != cts)
            {
                return;
            }

            _baseline = result;
            BaselineLabel = result is null ? string.Empty : BaselineText(result, units);
        }
        catch (OperationCanceledException)
        {
            // 条件が変わって計算し直す
        }
        catch (Exception ex) when (ex is IOException or DuckDB.NET.Data.DuckDBException)
        {
            if (_baselineCts == cts)
            {
                BaselineLabel = "ランダム基準を計算できませんでした: " + ex.Message;
            }
        }
    }

    private static string BaselineText(BaselineResult b, BacktestUnits units) =>
        $"ランダム基準（同じ日・件数・時間帯で無作為に選んだ {b.Trials} 試行）: 合計の平均 {units.Format(b.MeanTotal)} {units.Label}"
        + $" · 実績（{units.Format(b.ActualTotal)}）が上回った試行 {Format.Percent(b.BeatRatio)}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStats))]
    public partial string StatsLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BaselineLabel { get; set; } = string.Empty;

    /// <summary>確定した取引があり、安定性の指標を表示できる。</summary>
    public bool HasStats => StatsLabel.Length > 0;

    private string PeriodLabel(DateOnly start) => Cycle switch
    {
        BacktestCycle.Daily => $"{start.Month}月{start.Day}日（{Format.Weekday(start.ToDateTime(TimeOnly.MinValue))}）",
        BacktestCycle.Weekly => $"{JapaneseDate(start)}-{JapaneseDate(start.AddDays(6))}",
        _ => $"{start.Year}年{start.Month}月",
    };

    /// <summary>2026年09月21日 の形式。</summary>
    private static string JapaneseDate(DateOnly d) => d.ToString("yyyy年MM月dd日", CultureInfo.InvariantCulture);

    /// <summary>取引ごとの明細を CSV で保存する。</summary>
    [RelayCommand]
    private void SaveCsv()
    {
        var units = Units;
        if ((Symbol is null && !IsComposite) || _filtered.Count == 0)
        {
            Message = "保存する結果がありません";
            return;
        }

        var sb = new StringBuilder("日付,分析基準日,銘柄,モード,方向,Entry,Close,保有分,状態,Entry BID,raw,spread,net,net(" + units.Label + "),勝敗\n");
        foreach (var trade in _filtered.OrderBy(t => t.Trade.EntryJst))
        {
            var (reportDate, t, symbolId) = trade;
            sb.AppendLine(string.Join(',',
                t.TradeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                reportDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                symbolId,
                SelectedMode,
                CandidateGrid.DirectionName(t.Point.Direction),
                t.EntryJst.ToString("HH:mm", CultureInfo.InvariantCulture),
                t.CloseJst.ToString("HH:mm", CultureInfo.InvariantCulture),
                t.Point.HoldMinutes,
                t.Status,
                Number(t.EntryPrice),
                Number(t.Raw),
                Number(t.Spread),
                Number(t.Net),
                Number(t.Status == TradeStatus.Settled ? units.Value(trade) : double.NaN),
                t.Status == TradeStatus.Settled ? (t.IsWin ? "勝" : "負") : string.Empty));
        }

        ReportSaved(Export($"{ExportName}_{SymbolFilePart}_{SelectedMode}", sb.ToString()));
    }

    /// <summary>評価サイクルごとの集計を CSV で保存する。</summary>
    [RelayCommand]
    private void SaveReport()
    {
        if (Rows.Count == 0)
        {
            Message = "保存する結果がありません";
            return;
        }

        var sb = new StringBuilder($"{PeriodHeader},取引数,勝ち,負け,勝率,平均({UnitLabel}),合計({UnitLabel})\n");
        foreach (var r in Rows)
        {
            sb.AppendLine(string.Join(',', r.Label, r.Trades, r.Wins, r.Losses, r.WinRate, r.AveragePips, r.TotalPips));
        }

        sb.AppendLine(string.Join(',', "合計", SumTrades, SumWins, SumLosses, SumWinRate, SumAverage, SumTotal));

        // 安定性の指標とランダム基準（表示と同じ値。値なしは空欄）
        var s = _statistics;
        sb.AppendLine();
        sb.AppendLine("指標,値");
        sb.AppendLine(string.Join(',', "平均の t 値", Number(s.TStat)));
        sb.AppendLine(string.Join(',', $"平均の 95% 信頼区間 半幅({UnitLabel})", Number(s.MeanCiHalfWidth)));
        sb.AppendLine(string.Join(',', "プロフィットファクター", double.IsPositiveInfinity(s.ProfitFactor) ? "inf" : Number(s.ProfitFactor)));
        sb.AppendLine(string.Join(',', $"最大ドローダウン({UnitLabel})", Number(s.MaxDrawdown)));
        sb.AppendLine(string.Join(',', "最大連敗", s.MaxConsecutiveLosses));
        if (_baseline is { } b)
        {
            sb.AppendLine(string.Join(',', $"ランダム基準 {b.Trials} 試行の合計の平均({UnitLabel})", Number(b.MeanTotal)));
            sb.AppendLine(string.Join(',', "実績が上回った試行の割合", Number(b.BeatRatio)));
        }

        ReportSaved(Export($"{ExportName}_report_{SymbolFilePart}_{SelectedMode}_{CycleLabel}", sb.ToString()));
    }

    /// <summary>ファイル名の銘柄部分（複合は銘柄を - でつなぐ）。</summary>
    private string SymbolFilePart => string.Join("-", SelectedSymbols.Select(s => s.Id));

    /// <summary>保存したファイル（画面が「ファイルを開く」「保存先を開く」を尋ねる）。</summary>
    public event EventHandler<string>? FileSaved;

    private void ReportSaved(string path)
    {
        Message = "保存しました: " + path;
        FileSaved?.Invoke(this, path);
    }

    private static string Number(double v) => double.IsFinite(v) ? v.ToString("0.#####", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>ドキュメント\AnomalyStudio に BOM 付き UTF-8（Excel で文字化けしない形式）で保存する。</summary>
    private static string Export(string name, string content)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AnomalyStudio");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }
}
