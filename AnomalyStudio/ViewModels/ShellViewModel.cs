namespace AnomalyStudio.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    /// <summary>左レールの並び順。タイトルバーの矢印と PageUp / PageDown はこの順に移動する。</summary>
    private static readonly string[] MenuOrder =
    [
        NavigationService.Dashboard,
        NavigationService.Entries,
        NavigationService.Calendar,
        NavigationService.Backtest,
        NavigationService.OptimizationCheck,
        NavigationService.Extraction,
        NavigationService.Settings,
    ];

    private readonly NavigationService _navigation;
    private readonly AppSettings _settings;

    public ShellViewModel(NavigationService navigation, AppSettings settings, IUpdateService updates)
    {
        _navigation = navigation;
        _settings = settings;
        Updates = updates;
        updates.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IUpdateService.AvailableVersion))
            {
                OnPropertyChanged(nameof(UpdateNotice));
            }
        };
        _navigation.Navigated += (_, key) =>
        {
            // モード詳細はデータ抽出設定の配下として扱う
            SelectedKey = key == NavigationService.ModeDetail ? NavigationService.Extraction : key;
            _settings.LastScreen = SelectedKey;
        };
    }

    /// <summary>タイトルバーのアプリ名と版。</summary>
    public string AppName => ProductInfo.Name;

    public string AppVersion => $"v{ProductInfo.Version}";

    /// <summary>更新の状態。ダウンロード済みの更新があれば、上部の版の横に「更新あり」を出す。</summary>
    public IUpdateService Updates { get; }

    public string UpdateNotice => $"· {Updates.AvailableVersion} に更新できます（設定 › アプリの更新）";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousMenuCommand), nameof(NextMenuCommand))]
    public partial string SelectedKey { get; set; } = NavigationService.Dashboard;

    /// <summary>画面確認用に起動画面を一時的に指定する（設定には保存しない）。</summary>
    public string? StartOverride { get; set; }

    public string InitialScreen =>
        StartOverride ?? (_settings.StartScreen == "最後に表示した画面"
            ? _settings.LastScreen ?? NavigationService.Dashboard
            : NavigationService.StartScreens.GetValueOrDefault(_settings.StartScreen, NavigationService.Dashboard));

    private int MenuIndex => Array.IndexOf(MenuOrder, SelectedKey);

    [RelayCommand]
    private void Navigate(string key) => _navigation.Navigate(key);

    [RelayCommand(CanExecute = nameof(CanPreviousMenu))]
    private void PreviousMenu() => _navigation.Navigate(MenuOrder[MenuIndex - 1]);

    private bool CanPreviousMenu() => MenuIndex > 0;

    [RelayCommand(CanExecute = nameof(CanNextMenu))]
    private void NextMenu() => _navigation.Navigate(MenuOrder[MenuIndex + 1]);

    private bool CanNextMenu() => MenuIndex >= 0 && MenuIndex < MenuOrder.Length - 1;
}
