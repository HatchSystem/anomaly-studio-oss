using Microsoft.UI.Dispatching;

namespace AnomalyStudio.ViewModels;

public sealed partial class SplashViewModel(
    AnomalyWorkspace workspace, AnalysisScheduler scheduler, IEconomicCalendarService calendar, AppSettings settings, IUpdateService updates)
    : ObservableObject
{
    public string Version { get; } = $"Version {ProductInfo.Version}";

    [ObservableProperty]
    public partial string StepText { get; set; } = "設定を読み込み中…";

    /// <summary>保存済みの分析結果を読み込み、ポイントを抽出する。取込は画面表示後にバックグラウンドで行う。</summary>
    public async Task LoadAsync()
    {
        StepText = "分析結果を読み込み中…";
        await Task.WhenAll(workspace.RefreshAsync(), Task.Delay(600));
        StepText = "エントリーポイントを展開中…";
        await Task.Delay(300);
        scheduler.Start(DispatcherQueue.GetForCurrentThread());
        calendar.Start(DispatcherQueue.GetForCurrentThread());
    }

    /// <summary>取込済みの銘柄があれば、前回からの差分を取り込んでエントリーの結果を確定させる。アプリの更新も確認する。</summary>
    public void StartBackgroundUpdate()
    {
        if (settings.UpdateOnStartup && workspace.Statuses.Any(s => s.Symbol.Enabled && s.BarCount > 0))
        {
            _ = workspace.UpdateMarketDataAsync();
        }

        // アプリの更新は裏で確認してダウンロードしておく（再起動は利用者が選ぶ。失敗は設定画面に出るだけ）
        if (settings.CheckForUpdatesOnStartup)
        {
            _ = updates.CheckAsync();
        }
    }
}
