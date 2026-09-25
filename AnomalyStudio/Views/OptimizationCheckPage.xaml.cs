namespace AnomalyStudio.Views;

public sealed partial class OptimizationCheckPage : Page
{
    public OptimizationCheckPage()
    {
        InitializeComponent();
        BacktestRunDialog.Attach(this, ViewModel);
        SavedFileDialog.Attach(this, ViewModel);
    }

    public OptimizationCheckViewModel ViewModel { get; } = App.GetService<OptimizationCheckViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e) => ViewModel.Start();

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (ViewModel.IsRunning)
        {
            ViewModel.CancelRunCommand.Execute(null);
        }
    }

    /// <summary>バックテスト画面の条件を置き換えて移動するので、確認してから行う。</summary>
    private async void OnCopyToBacktestClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = "設定を反映してバックテスト画面に移動しますか？",
            Content = new TextBlock
            {
                Text = $"{ViewModel.RunCondition}\n\n"
                       + "銘柄・モード・評価サイクル・検証期間をウォークフォワード・バックテストの画面に設定します。"
                       + "取引時間帯・ポイント数・曜日は両方の画面で共通です。計算は移動後に「実行」で行います。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "OK",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.CopyToBacktestCommand.Execute(null);
        }
    }
}
