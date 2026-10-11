using AnomalyStudio.Core;
using AnomalyStudio.Core.Time;
using Microsoft.UI.Dispatching;

namespace AnomalyStudio.Services;

/// <summary>
/// UI スレッドで毎秒 <see cref="Tick"/> を発生させる共有時計。時刻は JST（エントリーの展開と同じ基準。PC のタイムゾーンに依存しない）。
/// </summary>
public sealed partial class ClockService(ISystemTimeSynchronizer synchronizer) : ObservableObject
{
    private DispatcherQueueTimer? _timer;

    public event EventHandler<DateTime>? Tick;

    public DateTime Now => Jst.Now;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SynchronizeCommand))]
    public partial bool IsSynchronizing { get; set; }

    [ObservableProperty]
    public partial string SynchronizationStatus { get; set; } = synchronizer.IsSupported
        ? "PCの時計を使用しています。Windowsの設定済み時刻サーバーと同期できます"
        : "この端末ではOSの日付と時刻の設定で同期してください";

    private bool CanSynchronize() => synchronizer.IsSupported && !IsSynchronizing;

    /// <summary>利用者が押したときだけ Windows 自体の時計を再同期する。二重実行はしない。</summary>
    [RelayCommand(CanExecute = nameof(CanSynchronize))]
    private async Task SynchronizeAsync()
    {
        if (!CanSynchronize())
        {
            return;
        }

        IsSynchronizing = true;
        SynchronizationStatus = "Windowsの時刻を同期中…管理者確認が表示された場合は許可してください";
        try
        {
            var result = await synchronizer.SynchronizeAsync();
            SynchronizationStatus = result.Outcome switch
            {
                SystemTimeSyncOutcome.Succeeded => $"Windowsの再同期が完了しました（{Now:MM/dd HH:mm:ss} JST）",
                SystemTimeSyncOutcome.Cancelled => "時刻同期を取り消しました。管理者確認が許可されていません",
                SystemTimeSyncOutcome.Unsupported => "この端末ではOSの日付と時刻の設定で同期してください",
                SystemTimeSyncOutcome.TimedOut => "時刻同期が時間内に完了しませんでした。ネットワークとWindowsの時刻設定を確認してください",
                _ => $"時刻同期に失敗しました（終了コード {result.ExitCode?.ToString() ?? "不明"}）。Windowsの時刻設定とネットワークを確認してください",
            };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            SynchronizationStatus = "時刻同期を実行できませんでした。Windowsの時刻設定を確認してください";
        }
        finally
        {
            IsSynchronizing = false;
            // OS の補正後の時刻を、表示とカウントダウンへすぐ反映する。
            Tick?.Invoke(this, Now);
        }
    }

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
