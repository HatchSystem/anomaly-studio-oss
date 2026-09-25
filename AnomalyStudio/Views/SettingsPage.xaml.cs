namespace AnomalyStudio.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; } = App.GetService<SettingsViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e) => ViewModel.Start();

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.Stop();
}
