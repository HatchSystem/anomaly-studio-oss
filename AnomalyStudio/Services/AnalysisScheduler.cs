using System.Globalization;
using AnomalyStudio.Core;
using Microsoft.UI.Dispatching;

namespace AnomalyStudio.Services;

/// <summary>週次の自動分析。設定でオンのとき、指定の曜日・時刻を過ぎていて未実行なら分析する。</summary>
public sealed class AnalysisScheduler(AnomalyWorkspace workspace, AppSettings settings)
{
    private DispatcherQueueTimer? _timer;

    public void Start(DispatcherQueue dispatcherQueue)
    {
        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(1);
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
    }

    private async Task CheckAsync()
    {
        if (!settings.ScheduleEnabled || workspace.IsBusy
            || !TimeOnly.TryParse(settings.ScheduleTime, CultureInfo.InvariantCulture, out var time))
        {
            return;
        }

        var due = WeeklySchedule.LastOccurrence(Jst.Now, settings.ScheduleDay, time);
        var ran = workspace.Statuses
            .Where(s => s.Symbol.Enabled)
            .All(s => s.LatestRun is { } run && Jst.FromUtc(run.CreatedAtUtc) >= due);
        if (!ran)
        {
            await workspace.RunAnalysisAsync();
        }
    }
}
