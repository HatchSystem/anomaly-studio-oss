namespace AnomalyStudio.ViewModels;

/// <summary>表示中だけ共有時計の毎秒通知を受け取る画面用の基底クラス。</summary>
public abstract partial class ClockedViewModel(ClockService clock) : ObservableObject
{
    [ObservableProperty]
    public partial string TimeText { get; set; } = string.Empty;

    public void Activate()
    {
        clock.Tick += OnTick;
        OnTick(this, clock.Now);
    }

    public void Deactivate() => clock.Tick -= OnTick;

    private void OnTick(object? sender, DateTime now)
    {
        TimeText = Format.Clock(now);
        Update(now);
    }

    protected abstract void Update(DateTime now);
}
