using AnomalyStudio.Core.Modes;
using AnomalyStudio.Core.Symbols;
using AnomalyStudio.Core.Trading;

namespace AnomalyStudio.ViewModels;

/// <summary>
/// ウォークフォワード・バックテスト。取引日ごとに、評価サイクルで決まる時点の分析結果から選んだポイントで評価する
/// （毎日 = 前日までのデータ、毎週 = 直前の日曜の分析、毎月 = 前月末までのデータ）。
/// 分析を何度も計算するため、条件を変えても自動では再計算せず「実行」で計算する。
/// </summary>
public sealed partial class BacktestViewModel(AnomalyWorkspace workspace, AppSettings settings, NavigationService navigation)
    : BacktestViewModelBase(workspace, settings)
{
    private const string ReadyMessage =
        "取引日ごとに、その時点の分析結果（毎日＝前日まで・毎週＝直前の日曜・毎月＝前月末までのデータ）で選んだポイントで評価します。「実行」で開始";

    protected override string ExportName => "walkforward";

    public override void Start()
    {
        ClearResults();
        Message = ReadyMessage;
    }

    /// <summary>
    /// 今の銘柄・モードをエントリー画面の抽出条件に反映して移動する（銘柄が複数なら複合モード）。
    /// 取引時間帯・ポイント数・曜日はもともと共通。確認は画面が行う。
    /// </summary>
    [RelayCommand]
    private void ReflectToEntries() => navigation.Navigate(NavigationService.Entries, new EntryConditions(Conditions.SymbolIds, SelectedMode));

    /// <summary>エントリーに反映しても、そのままでは出ない理由（確認ダイアログに出す）。問題がなければ空。</summary>
    public string ReflectWarning
    {
        get
        {
            var mode = Workspace.Modes.All.FirstOrDefault(m => m.Name == SelectedMode);
            return mode is null ? "モードを選んでください。"
                : !mode.Enabled ? $"モード「{mode.Name}」は無効なので、エントリーには出ません。データ抽出設定で有効にしてください。"
                : Conditions.SymbolIds.Count >= 2 && mode.EffectiveCompositeMetric is null
                    ? $"モード「{mode.Name}」は複合モードに対応していないので、エントリーには出ません。"
                : string.Empty;
        }
    }

    /// <summary>最適化確認の「バックテストへコピー」から開いたとき。条件を反映し、計算は「実行」で行う。</summary>
    public void Start(BacktestConditions conditions)
    {
        ApplyConditions(conditions);
        Start();
        Message = $"最適化確認の条件（{SymbolListLabel} · {SelectedMode}）を反映しました。「実行」で計算します";
    }

    protected override void OnConditionChanged(bool cycleOnly)
    {
        if (IsRunning)
        {
            CancelRunCommand.Execute(null);
        }

        ClearResults();
        Message = "条件を変更しました。「実行」で計算します";
    }

    protected override async Task<BacktestComputation> ComputeTradesAsync(
        SymbolProfile? symbol,
        ModeDefinition mode,
        DateOnly from,
        DateOnly to,
        BacktestCycle cycle,
        IProgress<WalkForwardProgress> progress,
        CancellationToken cancellationToken)
    {
        var result = symbol is null
            ? await Workspace.WalkForwardCompositeAsync(SelectedSymbols, mode, from, to, cycle, progress, cancellationToken)
            : await Workspace.WalkForwardAsync(symbol, mode, from, to, cycle, progress, cancellationToken);
        var used = result.Trades.Select(t => t.ReportDate).Distinct().Count();
        var parts = new List<string> { $"{used} 時点の分析で評価（うち新規計算 {result.ComputedAnalyses} 回）" };
        if (result.UnavailableReportDates.Count > 0)
        {
            parts.Add($"データ不足で分析できなかった時点 {result.UnavailableReportDates.Count} 件（{result.UnavailableReportDates[0]:yyyy/MM/dd} ほか。その期間は取引なし）");
        }

        // 市場が閉まっている時間（土曜の日中など）は足がないのが当然なので数えない
        var noData = result.Trades.Count(t => t.Trade.Status == TradeStatus.NoData && t.Trade.IsInMarketHours);
        if (noData > 0)
        {
            parts.Add($"データなしの取引 {noData:N0} 件");
        }

        var pending = result.Trades.Count(t => t.Trade.Status == TradeStatus.Pending);
        if (pending > 0)
        {
            parts.Add($"未確定 {pending:N0} 件");
        }

        return new BacktestComputation(result.Trades, string.Join(" · ", parts));
    }
}
