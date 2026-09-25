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

    private async void OnAiAssistClick(object sender, RoutedEventArgs e)
    {
        var dialog = new AiAssistDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.Sql = dialog.ViewModel.GeneratedSql;
        }
    }
}
