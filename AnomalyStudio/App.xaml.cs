using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Uno.Resizetizer;

namespace AnomalyStudio;

public partial class App : Application
{
    /// <summary>
    /// Initializes the singleton application object. This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        // 最小化・最大化・閉じるボタンは Uno が描き、色はアプリ全体のテーマ（画面の RequestedTheme ではない）で決まる。
        // アプリ全体のテーマは起動時にしか設定できないので、保存済みのテーマに合わせておく
        if (GetService<AppSettings>().Theme switch { "ライトモード" => ApplicationTheme.Light, "ダークモード" => ApplicationTheme.Dark, _ => (ApplicationTheme?)null } is { } theme)
        {
            RequestedTheme = theme;
        }

        this.InitializeComponent();

        // 落ちる前に原因をログへ残す（画面に出る前に終了する例外を後から追えるように）
        var logger = GetService<ILogger<App>>();
        logger.LogInformation("AnomalyStudio {Version} を起動", ProductInfo.Version);
        UnhandledException += (_, e) => logger.LogCritical(e.Exception, "未処理の例外: {Message}", e.Message);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "未処理の例外（プロセスが終了します）");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "監視されていない Task の例外");
            e.SetObserved();
        };

        // 段階ごとの所要時間（取込・分析・保存・ウォークフォワード）をログに残し、高速化の判断を計測値で行う。画面の読み直しは細かいので Debug
        AnomalyStudio.Core.Diagnostics.Timing.Recorded += e =>
        {
            if (e.Category == "ui")
            {
                logger.LogDebug("計測 {Category}/{Name}: {Ms:N0} ms {Detail}", e.Category, e.Name, e.Elapsed.TotalMilliseconds, e.Detail);
            }
            else
            {
                logger.LogInformation("計測 {Category}/{Name}: {Ms:N0} ms {Detail}", e.Category, e.Name, e.Elapsed.TotalMilliseconds, e.Detail);
            }
        };
    }

    public static new App Current => (App)Application.Current;

    public static IServiceProvider Services { get; } = ConfigureServices();

    public Window? MainWindow { get; private set; }

    /// <summary>保存場所のルート。既定は %LOCALAPPDATA%\AnomalyStudio。</summary>
    private static string RootDirectory =>
#if DEBUG
        // 画面確認用: ANOMALYSTUDIO_ROOT で別の保存場所を使う
        Environment.GetEnvironmentVariable("ANOMALYSTUDIO_ROOT") is { Length: > 0 } root ? root :
#endif
        AppPaths.DefaultRoot;

    private static IServiceProvider ConfigureServices() =>
        new ServiceCollection()
            // 利用者の環境で起きた失敗を後から追えるよう、アプリのログはファイルにも残す（Uno の詳細ログは警告以上だけ）
            .AddLogging(builder => builder
                .SetMinimumLevel(LogLevel.Information)
                .AddFilter("Uno", LogLevel.Warning)
                .AddFilter("Windows", LogLevel.Warning)
                .AddFilter("Microsoft", LogLevel.Warning)
                .AddProvider(new FileLoggerProvider(new AppPaths(RootDirectory).LogsDirectory)))
            .AddSingleton(_ => new AppSettings(Path.Combine(RootDirectory, "settings.json")))
            .AddSingleton(sp => new AppPaths(RootDirectory, sp.GetRequiredService<AppSettings>().DataDirectory))
            .AddSingleton(sp => new AnalysisDatabase(sp.GetRequiredService<AppPaths>()))
            .AddSingleton<IMarketDataSource>(_ => new DukascopyMarketDataSource(DukascopyMarketDataSource.CreateHttpClient()))
            .AddSingleton<SymbolRepository>()
            .AddSingleton<ModeRepository>()
            .AddSingleton<AnomalyWorkspace>()
            .AddSingleton<AnalysisScheduler>()
            .AddSingleton<AiSqlAssistant>()
            .AddSingleton<IEconomicCalendarSource>(_ => new ClickSecCalendarSource(ClickSecCalendarSource.CreateHttpClient()))
            .AddSingleton<EconomicCalendarStore>()
            .AddSingleton<IEconomicCalendarService, EconomicCalendarService>()
            .AddSingleton<IUpdateService, UpdateService>()
            .AddSingleton<ClockService>()
            .AddSingleton<NavigationService>()
            .AddSingleton<ShellViewModel>()
            .AddTransient<SplashViewModel>()
            .AddTransient<DashboardViewModel>()
            .AddTransient<EntriesViewModel>()
            .AddTransient<CalendarViewModel>()
            .AddTransient<ExtractionViewModel>()
            .AddTransient<ModeDetailViewModel>()
            .AddTransient<AiAssistViewModel>()
            .AddTransient<BacktestViewModel>()
            .AddTransient<OptimizationCheckViewModel>()
            .AddTransient<SettingsViewModel>()
            .BuildServiceProvider();

    public static T GetService<T>()
        where T : notnull => Services.GetRequiredService<T>();

    /// <summary>設定のテーマ名（ライトモード／ダークモード／システム設定）をウィンドウへ適用する。</summary>
    public void ApplyTheme(string theme)
    {
        if (MainWindow?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                "ライトモード" => ElementTheme.Light,
                "ダークモード" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
    }

    /// <summary>
    /// ページ内の要素をウィンドウのドラッグ領域（タイトルバー）にする。
    /// OS のタイトルバーは隠し、最小化・最大化・閉じるボタンだけをコンテンツの上に重ねる
    /// （ボタンの配色は Uno がテーマに合わせて描画する）。
    /// </summary>
    public void SetTitleBar(UIElement dragRegion)
    {
        if (UsesCustomTitleBar)
        {
            MainWindow?.SetTitleBar(dragRegion);
        }
    }

    /// <summary>
    /// OS のタイトルバーを隠してアプリ側で描くか。macOS は左上の 3 つのボタン（閉じる・最小化・拡大）が左レールのロゴと重なるので、
    /// OS のタイトルバーをそのまま使う（上部のアプリ名の帯はその下に出る）。
    /// </summary>
    private static bool UsesCustomTitleBar => !OperatingSystem.IsMacOS();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new Window { Title = "AnomalyStudio" };
        // デザイン通り、上部 48px をアプリ側で描画する（ロゴ・戻る／進む・ウィンドウ操作ボタン）
        if (UsesCustomTitleBar)
        {
            MainWindow.ExtendsContentIntoTitleBar = true;
            MainWindow.AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        }
#if DEBUG
        MainWindow.UseStudio();
#endif
        GetService<ClockService>().Start(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        var settings = GetService<AppSettings>();
#if DEBUG
        // 画面確認用: ANOMALYSTUDIO_THEME=dark / ANOMALYSTUDIO_START=backtest / optimizationCheck などで起動状態を切り替える（設定には保存しない）
        var themeOverride = Environment.GetEnvironmentVariable("ANOMALYSTUDIO_THEME") == "dark" ? "ダークモード" : null;
        GetService<ShellViewModel>().StartOverride = Environment.GetEnvironmentVariable("ANOMALYSTUDIO_START") is { Length: > 0 } start ? start : null;
#else
        string? themeOverride = null;
#endif
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.Theme))
            {
                ApplyTheme(settings.Theme);
            }
        };


        // Do not repeat app initialization when the Window already has content,
        // just ensure that the window is active
        if (MainWindow.Content is not Frame rootFrame)
        {
            // Create a Frame to act as the navigation context and navigate to the first page
            rootFrame = new Frame();

            // Place the frame in the current Window
            MainWindow.Content = rootFrame;

            rootFrame.NavigationFailed += OnNavigationFailed;
        }

        if (rootFrame.Content == null)
        {
            // When the navigation stack isn't restored navigate to the first page,
            // configuring the new page by passing required information as a navigation
            // parameter
            rootFrame.Navigate(typeof(SplashPage), args.Arguments);
        }

        ApplyTheme(themeOverride ?? settings.Theme);
        // 表示してから大きさを変えると再描画でちらつくので、表示前に既定の大きさにする。
        // 表示倍率は OS から読み、実際の倍率が違ったときだけ表示後に合わせ直す
        _initialScale = SystemScale();
        Resize(_initialScale);
        rootFrame.Loaded += ResizeToDefault;
        MainWindow.SetWindowIcon();
        // Ensure the current window is active
        MainWindow.Activate();
    }

    private double _initialScale = 1.0;

    /// <summary>既定のウィンドウサイズ（論理ピクセル）。Resize は物理ピクセルなので表示倍率を掛ける。</summary>
    private void Resize(double scale) =>
        MainWindow?.AppWindow.Resize(new Windows.Graphics.SizeInt32 { Width = (int)(1180 * scale), Height = (int)(760 * scale) });

    /// <summary>表示後の実際の倍率が、表示前に仮定した倍率と違うときだけ合わせ直す。</summary>
    private void ResizeToDefault(object sender, RoutedEventArgs e)
    {
        var frame = (FrameworkElement)sender;
        frame.Loaded -= ResizeToDefault;
        var scale = frame.XamlRoot?.RasterizationScale ?? _initialScale;
        if (Math.Abs(scale - _initialScale) > 0.01)
        {
            Resize(scale);
        }
    }

    /// <summary>OS の表示倍率（96 DPI = 1.0）。Windows 以外は 1.0 を仮定し、表示後に合わせ直す。</summary>
    private static double SystemScale() => OperatingSystem.IsWindows() ? GetDpiForSystem() / 96.0 : 1.0;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    /// <summary>
    /// Invoked when Navigation to a certain page fails
    /// </summary>
    /// <param name="sender">The Frame which failed navigation</param>
    /// <param name="e">Details about the navigation failure</param>
    void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
    {
        throw new InvalidOperationException($"Failed to load {e.SourcePageType.FullName}: {e.Exception}");
    }

    /// <summary>
    /// Configures global Uno Platform logging
    /// </summary>
    public static void InitializeLogging()
    {
#if DEBUG
        // Logging is disabled by default for release builds, as it incurs a significant
        // initialization cost from Microsoft.Extensions.Logging setup. If startup performance
        // is a concern for your application, keep this disabled. If you're running on the web or
        // desktop targets, you can use URL or command line parameters to enable it.
        //
        // For more performance documentation: https://platform.uno/docs/articles/Uno-UI-Performance.html

        var factory = LoggerFactory.Create(builder =>
        {
#if __WASM__
            builder.AddProvider(new global::Uno.Extensions.Logging.WebAssembly.WebAssemblyConsoleLoggerProvider());
#elif __IOS__
            builder.AddProvider(new global::Uno.Extensions.Logging.OSLogLoggerProvider());

            // Log to the Visual Studio Debug console
            builder.AddConsole();
#else
            builder.AddConsole();
#endif

            // Exclude logs below this level
            builder.SetMinimumLevel(LogLevel.Information);

            // Default filters for Uno Platform namespaces
            builder.AddFilter("Uno", LogLevel.Warning);
            builder.AddFilter("Windows", LogLevel.Warning);
            builder.AddFilter("Microsoft", LogLevel.Warning);

            // Generic Xaml events
            // builder.AddFilter("Microsoft.UI.Xaml", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.VisualStateGroup", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.StateTriggerBase", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.UIElement", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.FrameworkElement", LogLevel.Trace );

            // Layouter specific messages
            // builder.AddFilter("Microsoft.UI.Xaml.Controls", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.Controls.Layouter", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.Controls.Panel", LogLevel.Debug );

            // builder.AddFilter("Windows.Storage", LogLevel.Debug );

            // Binding related messages
            // builder.AddFilter("Microsoft.UI.Xaml.Data", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.Data", LogLevel.Debug );

            // Binder memory references tracking
            // builder.AddFilter("Uno.UI.DataBinding.BinderReferenceHolder", LogLevel.Debug );

            // DevServer and HotReload related
            // builder.AddFilter("Uno.UI.RemoteControl", LogLevel.Information);

            // Debug JS interop
            // builder.AddFilter("Uno.Foundation.WebAssemblyRuntime", LogLevel.Debug );
        });

        global::Uno.Extensions.LogExtensionPoint.AmbientLoggerFactory = factory;

#if HAS_UNO
        global::Uno.UI.Adapter.Microsoft.Extensions.Logging.LoggingAdapter.Initialize();
#endif
#endif
    }
}
