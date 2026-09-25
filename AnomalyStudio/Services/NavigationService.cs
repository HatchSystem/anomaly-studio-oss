namespace AnomalyStudio.Services;

/// <summary>シェル内のコンテンツ Frame を操作する。画面キーとページ型の対応はここだけで持つ。</summary>
public sealed class NavigationService
{
    public const string Dashboard = "dashboard";
    public const string Entries = "entries";
    public const string Calendar = "calendar";
    public const string Extraction = "extraction";
    public const string ModeDetail = "modeDetail";
    public const string Backtest = "backtest";
    public const string OptimizationCheck = "optimizationCheck";
    public const string Settings = "settings";

    private static readonly Dictionary<string, Type> Pages = new()
    {
        [Dashboard] = typeof(DashboardPage),
        [Entries] = typeof(EntriesPage),
        [Calendar] = typeof(CalendarPage),
        [Extraction] = typeof(ExtractionPage),
        [ModeDetail] = typeof(ModeDetailPage),
        [Backtest] = typeof(BacktestPage),
        [OptimizationCheck] = typeof(OptimizationCheckPage),
        [Settings] = typeof(SettingsPage),
    };

    /// <summary>設定「起動時の画面」の選択肢と画面キーの対応。</summary>
    public static readonly IReadOnlyDictionary<string, string> StartScreens = new Dictionary<string, string>
    {
        ["ダッシュボード"] = Dashboard,
        ["エントリー一覧"] = Entries,
        ["経済指標カレンダー"] = Calendar,
        ["ウォークフォワード・バックテスト"] = Backtest,
        ["最適化確認"] = OptimizationCheck,
        ["データ抽出設定"] = Extraction,
        ["設定"] = Settings,
    };

    private Frame? _frame;

    public event EventHandler<string>? Navigated;

    public bool CanGoBack => _frame?.CanGoBack ?? false;

    public bool CanGoForward => _frame?.CanGoForward ?? false;

    public string? CurrentKey { get; private set; }

    public void Attach(Frame frame)
    {
        _frame = frame;
        _frame.Navigated += (_, e) =>
        {
            CurrentKey = Pages.FirstOrDefault(p => p.Value == e.SourcePageType).Key;
            if (CurrentKey is not null)
            {
                Navigated?.Invoke(this, CurrentKey);
            }
        };
    }

    public void Navigate(string key, object? parameter = null)
    {
        if (_frame is null || (key == CurrentKey && parameter is null))
        {
            return;
        }

        // 切り替えのアニメーション（新しいページが一瞬空白になってから現れる）は使わず、すぐ表示する
        _frame.Navigate(Pages[key], parameter, new Microsoft.UI.Xaml.Media.Animation.SuppressNavigationTransitionInfo());
    }

    public void GoBack()
    {
        if (CanGoBack)
        {
            _frame!.GoBack();
        }
    }

    public void GoForward()
    {
        if (CanGoForward)
        {
            _frame!.GoForward();
        }
    }
}
