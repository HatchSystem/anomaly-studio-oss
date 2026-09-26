namespace AnomalyStudio.Views;

public sealed partial class ModeDetailPage : Page
{
    public ModeDetailPage()
    {
        InitializeComponent();
    }

    public ModeDetailViewModel ViewModel { get; } = App.GetService<ModeDetailViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _ = ViewModel.PrepareAiAssistAsync();
        if (e.Parameter is AnomalyStudio.Core.Modes.ModeDefinition mode)
        {
            ViewModel.Load(mode);
        }
        else
        {
            ViewModel.LoadNew();
        }
    }

    /// <summary>元に戻せないので、確認してから削除する。</summary>
    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = $"モード「{ViewModel.Name}」を削除しますか？",
            Content = new TextBlock
            {
                Text = "モードの名前・説明・抽出 SQL を削除します。元に戻せません。\n"
                       + "エントリー画面でこのモードを選んでいた場合は「すべて」に戻ります。保存済みの CSV・レポートは残ります。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "削除する",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.DeleteCommand.Execute(null);
        }
    }

    private async void OnAiAssistClick(object sender, RoutedEventArgs e)
    {
        var dialog = new AiAssistDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.Sql = dialog.ViewModel.GeneratedSql;
        }
    }
}
