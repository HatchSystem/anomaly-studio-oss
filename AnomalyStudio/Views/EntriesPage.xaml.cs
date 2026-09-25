namespace AnomalyStudio.Views;

/// <summary>
/// エントリー一覧。ページを作り直すと一覧を描き直すまで空白になりちらつくので、
/// ページ（と ViewModel・描画済みの一覧）を使い回す（<c>NavigationCacheMode="Required"</c>）。
/// </summary>
public sealed partial class EntriesPage : Page
{
    public EntriesPage()
    {
        InitializeComponent();
    }

    public EntriesViewModel ViewModel { get; } = App.GetService<EntriesViewModel>();

    /// <summary>バックテストの「エントリーに反映」からは、その条件を受け取ってから開く。</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel.ScrollRequested += OnScrollRequested;
        if (e.Parameter is EntryConditions conditions)
        {
            ViewModel.ApplyConditions(conditions);
        }

        ViewModel.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.ScrollRequested -= OnScrollRequested;
        ViewModel.Stop();
    }

    /// <summary>アクティブな行（次回・進行中）を見える位置へ。一覧を作り直した直後なので、配置が終わってから行う。</summary>
    private void OnScrollRequested(object? sender, EntryRowViewModel row) =>
        DispatcherQueue.TryEnqueue(() => EntryList.ScrollIntoView(row, ScrollIntoViewAlignment.Leading));
}
