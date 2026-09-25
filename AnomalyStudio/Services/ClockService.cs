using AnomalyStudio.Core;
using Microsoft.UI.Dispatching;

namespace AnomalyStudio.Services;

/// <summary>
/// UI スレッドで毎秒 <see cref="Tick"/> を発生させる共有時計。時刻は JST（エントリーの展開と同じ基準。PC のタイムゾーンに依存しない）。
/// </summary>
public sealed class ClockService
{
    private DispatcherQueueTimer? _timer;

    public event EventHandler<DateTime>? Tick;

    public DateTime Now => Jst.Now;

    public void Start(DispatcherQueue dispatcherQueue)
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => Tick?.Invoke(this, Jst.Now);
        _timer.Start();
    }
}
