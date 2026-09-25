using AnomalyStudio.Core.Modes;
using AnomalyStudio.Core.Symbols;

namespace AnomalyStudio.ViewModels;

/// <summary>
/// 最適化確認。最新の分析結果のポイントを、指定期間の各日に当てはめた結果を評価サイクルごとに集計する。
/// 分析期間（基準日の直近 365 日）と重なる期間は、ポイントの選定に使ったデータそのものでの成績になる。
/// </summary>
public sealed partial class OptimizationCheckViewModel(AnomalyWorkspace workspace, AppSettings settings, NavigationService navigation)
    : BacktestViewModelBase(workspace, settings)
{
    protected override string ExportName => "optimization_check";

    /// <summary>
    /// 今の条件（銘柄・モード・評価サイクル・検証期間）をウォークフォワード・バックテストの画面へ写して移動する。
    /// 取引時間帯・ポイント数・曜日はもともと両方の画面で共通。確認は画面が行う。
    /// </summary>
    [RelayCommand]
    private void CopyToBacktest() => navigation.Navigate(NavigationService.Backtest, Conditions);

    public override async void Start() => await ComputeAsync(showProgress: false);

    protected override async void OnConditionChanged(bool cycleOnly)
    {
        if (cycleOnly)
        {
            Aggregate();
        }
        else
        {
            await ComputeAsync(showProgress: false);
        }
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
        if (symbol is null)
        {
            var symbols = SelectedSymbols;
            progress.Report(new WalkForwardProgress(0.3, $"{string.Join("・", symbols.Select(s => s.Id))} の取引を評価中"));
            var (composite, reportDate) = await Workspace.CheckOptimizationCompositeAsync(symbols, mode, from, to);
            return reportDate is not { } date
                ? new BacktestComputation([], "分析結果がありません。データ抽出設定で分析を実行してください")
                : new BacktestComputation(composite,
                    $"選んだ銘柄の最新の分析結果（{date:yyyy/MM/dd} 基準まで）から選んだ複合ポイントを過去の各日に当てはめた結果です（損益は各銘柄の表示単位で合算）。" +
                    $"{date.AddDays(-365):yyyy/MM/dd} 〜 {date.AddDays(-1):yyyy/MM/dd} はポイントの選定に使った期間です");
        }

        var run = Workspace.Statuses.FirstOrDefault(s => s.Symbol.Id == symbol.Id)?.LatestRun;
        if (run is null)
        {
            return new BacktestComputation([], $"{symbol.Id} の分析結果がありません。データ抽出設定で分析を実行してください");
        }

        progress.Report(new WalkForwardProgress(0.3, $"{symbol.Id} の取引を評価中"));
        var trades = await Workspace.CheckOptimizationAsync(symbol, mode, from, to);
        return new BacktestComputation(trades,
            $"{run.ReportDate:yyyy/MM/dd} 基準の分析結果（最新）のポイントを過去の各日に当てはめた結果です。" +
            $"{run.ReportDate.AddDays(-365):yyyy/MM/dd} 〜 {run.ReportDate.AddDays(-1):yyyy/MM/dd} はポイントの選定に使った期間です");
    }
}
