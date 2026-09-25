namespace AnomalyStudio.Views;

public sealed partial class SplashPage : Page
{
    public SplashPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public SplashViewModel ViewModel { get; } = App.GetService<SplashViewModel>();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.Current.SetTitleBar(TitleBarDragRegion);
        await ViewModel.LoadAsync();
        Frame.Navigate(typeof(ShellPage));
        Frame.BackStack.Clear();
        ViewModel.StartBackgroundUpdate();
    }
}
