namespace AnomalyStudio.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
    }

    public DashboardViewModel ViewModel { get; } = App.GetService<DashboardViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e) => ViewModel.Start();

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.Stop();
}
