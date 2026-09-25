namespace AnomalyStudio.Views;

public sealed partial class ExtractionPage : Page
{
    public ExtractionPage()
    {
        InitializeComponent();
    }

    public ExtractionViewModel ViewModel { get; } = App.GetService<ExtractionViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e) => ViewModel.Start();

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.Stop();

    /// <summary>元に戻せないので、確認してから初期化する。</summary>
    private async void OnResetDataClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = "データを初期化しますか？",
            Content = new TextBlock
            {
                Text = "取り込んだ1分足・分析結果・バックテスト用に保存したポイントをすべて削除します。元に戻せません。\n"
                       + "設定・モード・銘柄・経済指標カレンダーは残ります。使うには「分析を実行」で取り込み直してください（時間がかかります）。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "初期化する",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.ResetDataCommand.Execute(null);
        }
    }
}
