namespace AnomalyStudio.Views;

public sealed partial class BacktestPage : Page
{
    public BacktestPage()
    {
        InitializeComponent();
        BacktestRunDialog.Attach(this, ViewModel);
        SavedFileDialog.Attach(this, ViewModel);
    }

    public BacktestViewModel ViewModel { get; } = App.GetService<BacktestViewModel>();

    /// <summary>最適化確認の「バックテストへコピー」からは、その条件を受け取って開く。</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is BacktestConditions conditions)
        {
            ViewModel.Start(conditions);
        }
        else
        {
            ViewModel.Start();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (ViewModel.IsRunning)
        {
            ViewModel.CancelRunCommand.Execute(null);
        }
    }

    /// <summary>エントリー画面の抽出条件を置き換えて移動するので、確認してから行う。</summary>
    private async void OnReflectToEntriesClick(object sender, RoutedEventArgs e)
    {
        var warning = ViewModel.ReflectWarning;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = "条件をエントリーに反映して移動しますか？",
            Content = new TextBlock
            {
                Text = $"{ViewModel.RunCondition}\n\n"
                       + "銘柄・モードをエントリー画面の抽出条件にします（銘柄が複数なら複合モード、1 つなら銘柄別で、順位はポイント数に合わせます）。"
                       + "取引時間帯・ポイント数・曜日は両方の画面で共通です。"
                       + (warning.Length > 0 ? "\n\n" + warning : string.Empty),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "OK",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.ReflectToEntriesCommand.Execute(null);
        }
    }
}
