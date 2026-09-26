using System.Text.Json.Serialization;
using AnomalyStudio.Core.Analysis;
using AnomalyStudio.Core.Storage;

namespace AnomalyStudio.Services;

/// <summary>settings.json の内容。</summary>
public sealed class SettingsData
{
    public string Theme { get; set; } = "ダークモード";
    public string StartScreen { get; set; } = "最後に表示した画面";
    public string? LastScreen { get; set; }
    public string CalendarRefresh { get; set; } = "起動時 + 毎日 06:00";
    public int CalendarMinStars { get; set; } = 3;
    public List<string> CalendarCountries { get; set; } = ["US", "JP", "EU", "GB", "DE", "AU", "CN"];
    public string AiBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string AiModel { get; set; } = "gpt-4.1-mini";
    public string? DataDirectory { get; set; }
    public bool UpdateOnStartup { get; set; } = true;
    public bool CheckForUpdatesOnStartup { get; set; } = true;
    public bool ScheduleEnabled { get; set; }
    public DayOfWeek ScheduleDay { get; set; } = DayOfWeek.Sunday;
    public string ScheduleTime { get; set; } = "07:00";
    public int RunsToKeep { get; set; } = AnalysisRunner.DefaultRunsToKeep;

    /// <summary>旧版のエントリー画面の銘柄（1 銘柄・「すべて」・「複合」）。読み込み時に <see cref="EntriesSymbols"/> へ移し、保存しない。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntriesPair { get; set; }

    /// <summary>エントリー画面で選んだ銘柄。null はすべての銘柄。</summary>
    public List<string>? EntriesSymbols { get; set; }
    public bool EntriesComposite { get; set; }
    public string EntriesSide { get; set; } = "すべて";
    public string EntriesMode { get; set; } = "すべて";
    public int BacktestStartHour { get; set; } = 8;
    public int BacktestEndHour { get; set; } = 19;
    public int BacktestMaxPoints { get; set; } = 10;
    public List<DayOfWeek> BacktestWeekdays { get; set; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday];

    /// <summary>曜日の選択肢に土曜がある版で保存した。旧版（月〜金だけ）の設定を読むときに土曜を足すかの判断に使う。</summary>
    public bool WeekdaysIncludeSaturday { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SettingsData))]
internal sealed partial class AppJsonContext : JsonSerializerContext;

/// <summary>設定画面の値。変更のたびに settings.json へ保存する。</summary>
public sealed partial class AppSettings : ObservableObject
{
    private readonly string? _path;
    private bool _loading;

    public AppSettings()
        : this(null)
    {
    }

    public AppSettings(string? path)
    {
        _path = path;
        if (path is not null)
        {
            Apply(JsonFileStore.Load(path, AppJsonContext.Default.SettingsData, () => new SettingsData()));
        }

        PropertyChanged += (_, _) => Save();
    }

    public static IReadOnlyList<string> ThemeOptions { get; } = ["ライトモード", "ダークモード", "システム設定"];

    public static IReadOnlyList<string> StartScreenOptions { get; } =
        ["最後に表示した画面", "ダッシュボード", "エントリー一覧", "経済指標カレンダー", "ウォークフォワード・バックテスト", "最適化確認", "データ抽出設定", "設定"];

    [ObservableProperty]
    public partial string Theme { get; set; } = "ダークモード";

    [ObservableProperty]
    public partial string StartScreen { get; set; } = "最後に表示した画面";

    [ObservableProperty]
    public partial string CalendarRefresh { get; set; } = "起動時 + 毎日 06:00";

    /// <summary>カレンダーに表示する最小の重要度（★の数）。0 は重要度のない要人発言なども含めてすべて。</summary>
    [ObservableProperty]
    public partial int CalendarMinStars { get; set; } = 3;

    public HashSet<string> CalendarCountries { get; } = ["US", "JP", "EU", "GB", "DE", "AU", "CN"];

    [ObservableProperty]
    public partial string AiBaseUrl { get; set; } = "https://api.openai.com/v1";

    [ObservableProperty]
    public partial string AiModel { get; set; } = "gpt-4.1-mini";

    /// <summary>市場データと分析 DB の保存先。null は既定（%LOCALAPPDATA%\AnomalyStudio\data）。変更は再起動後に反映。</summary>
    [ObservableProperty]
    public partial string? DataDirectory { get; set; }

    /// <summary>起動時に未取得の 1 分足を取り込み、エントリーの結果を確定させる。</summary>
    [ObservableProperty]
    public partial bool UpdateOnStartup { get; set; } = true;

    /// <summary>起動時にアプリの更新を確認し、新しい版があればダウンロードしておく（再起動か次の起動で反映）。</summary>
    [ObservableProperty]
    public partial bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>週次の自動分析。既定はオフ（手動実行）。</summary>
    [ObservableProperty]
    public partial bool ScheduleEnabled { get; set; }

    [ObservableProperty]
    public partial DayOfWeek ScheduleDay { get; set; } = DayOfWeek.Sunday;

    [ObservableProperty]
    public partial string ScheduleTime { get; set; } = "07:00";

    /// <summary>銘柄ごとに保存しておく分析結果の数。画面は最新の結果しか使わないので既定は少なめ（1 件 ≈ 75 MB）。</summary>
    [ObservableProperty]
    public partial int RunsToKeep { get; set; } = AnalysisRunner.DefaultRunsToKeep;

    /// <summary>エントリー画面で選んだ銘柄。null はすべての銘柄（あとから有効にした銘柄も含む）。変更は <see cref="SetEntriesSymbols"/> で行う。</summary>
    public IReadOnlyList<string>? EntriesSymbols { get; private set; }

    public void SetEntriesSymbols(IReadOnlyList<string>? ids)
    {
        EntriesSymbols = ids;
        Save();
    }

    /// <summary>エントリー画面の複合モード（選んだ銘柄をまとめた順位で、最適化確認と同じ条件で抽出する）。</summary>
    [ObservableProperty]
    public partial bool EntriesComposite { get; set; }

    /// <summary>エントリー画面の絞り込み（方向・モード）。選択肢にない値は「すべて」として扱う。</summary>

    [ObservableProperty]
    public partial string EntriesSide { get; set; } = "すべて";

    [ObservableProperty]
    public partial string EntriesMode { get; set; } = "すべて";

    /// <summary>バックテスト・最適化確認の取引時間帯（JST の時。開始以上・終了以下）。</summary>
    [ObservableProperty]
    public partial int BacktestStartHour { get; set; } = 8;

    [ObservableProperty]
    public partial int BacktestEndHour { get; set; } = 19;

    /// <summary>バックテスト・最適化確認で使うポイント数（順位の高い順）。</summary>
    [ObservableProperty]
    public partial int BacktestMaxPoints { get; set; } = 10;

    /// <summary>
    /// バックテスト・最適化確認・エントリーで使う取引日の曜日（JST、月〜土）。土曜は早朝（NY クローズまで）だけ市場が開いている。
    /// 変更後は <see cref="NotifyBacktestWeekdaysChanged"/> で保存する。
    /// </summary>
    public HashSet<DayOfWeek> BacktestWeekdays { get; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday];

    public void NotifyBacktestWeekdaysChanged() => Save();

    /// <summary>取引時間帯の開始を変える。終了以降を選んだら終了を 1 時間後へずらす。</summary>
    public void SetBacktestStartHour(int hour)
    {
        BacktestStartHour = hour;
        if (BacktestEndHour <= hour)
        {
            BacktestEndHour = hour + 1;
        }
    }

    /// <summary>取引時間帯の終了を変える。開始以前を選んだら開始を 1 時間前へずらす。</summary>
    public void SetBacktestEndHour(int hour)
    {
        BacktestEndHour = hour;
        if (BacktestStartHour >= hour)
        {
            BacktestStartHour = hour - 1;
        }
    }

    private string? _lastScreen;

    /// <summary>最後に表示した画面（「起動時の画面」が既定値のときに使う）。</summary>
    public string? LastScreen
    {
        get => _lastScreen;
        set
        {
            _lastScreen = value;
            Save();
        }
    }

    public event EventHandler? CalendarFilterChanged;

    public void NotifyCalendarFilterChanged()
    {
        CalendarFilterChanged?.Invoke(this, EventArgs.Empty);
        Save();
    }

    partial void OnCalendarMinStarsChanged(int value) => CalendarFilterChanged?.Invoke(this, EventArgs.Empty);

    private void Apply(SettingsData d)
    {
        _loading = true;
        Theme = d.Theme;
        // 旧版の「バックテスト」はウォークフォワード・バックテスト（同じ画面キー）に読み替える
        StartScreen = d.StartScreen == "バックテスト" ? "ウォークフォワード・バックテスト" : d.StartScreen;
        _lastScreen = d.LastScreen;
        CalendarRefresh = d.CalendarRefresh;
        CalendarMinStars = Math.Clamp(d.CalendarMinStars, 0, 5);
        CalendarCountries.Clear();
        CalendarCountries.UnionWith(d.CalendarCountries);
        AiBaseUrl = d.AiBaseUrl;
        AiModel = d.AiModel;
        DataDirectory = d.DataDirectory;
        UpdateOnStartup = d.UpdateOnStartup;
        CheckForUpdatesOnStartup = d.CheckForUpdatesOnStartup;
        ScheduleEnabled = d.ScheduleEnabled;
        ScheduleDay = d.ScheduleDay;
        ScheduleTime = d.ScheduleTime;
        RunsToKeep = Math.Clamp(d.RunsToKeep, 1, 100);
        (EntriesSymbols, EntriesComposite) = d.EntriesSymbols is not null || d.EntriesPair is null
            ? (d.EntriesSymbols, d.EntriesComposite)
            : d.EntriesPair switch
            {
                // 旧版の「複合」は全銘柄の複合ポイント、「すべて」は全銘柄、それ以外は 1 銘柄
                "複合" => (null, true),
                "すべて" => (null, false),
                var id => ([id], false),
            };
        EntriesSide = d.EntriesSide;
        EntriesMode = d.EntriesMode;
        BacktestStartHour = Math.Clamp(d.BacktestStartHour, 0, 23);
        BacktestEndHour = Math.Clamp(d.BacktestEndHour, BacktestStartHour + 1, 24);
        BacktestMaxPoints = Math.Clamp(d.BacktestMaxPoints, 1, PointSelector.DefaultLimit);
        BacktestWeekdays.Clear();
        BacktestWeekdays.UnionWith(d.BacktestWeekdays);

        // 旧版は選択肢が月〜金だけだった。すべて選んでいた（既定のまま）なら、選択肢に加えた土曜も含める
        if (!d.WeekdaysIncludeSaturday && new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }.All(BacktestWeekdays.Contains))
        {
            BacktestWeekdays.Add(DayOfWeek.Saturday);
        }
        _loading = false;
    }

    private SettingsData ToData() => new()
    {
        Theme = Theme,
        StartScreen = StartScreen,
        LastScreen = _lastScreen,
        CalendarRefresh = CalendarRefresh,
        CalendarMinStars = CalendarMinStars,
        CalendarCountries = [.. CalendarCountries.Order()],
        AiBaseUrl = AiBaseUrl,
        AiModel = AiModel,
        DataDirectory = DataDirectory,
        UpdateOnStartup = UpdateOnStartup,
        CheckForUpdatesOnStartup = CheckForUpdatesOnStartup,
        ScheduleEnabled = ScheduleEnabled,
        ScheduleDay = ScheduleDay,
        ScheduleTime = ScheduleTime,
        RunsToKeep = RunsToKeep,
        EntriesSymbols = EntriesSymbols is null ? null : [.. EntriesSymbols],
        EntriesComposite = EntriesComposite,
        EntriesSide = EntriesSide,
        EntriesMode = EntriesMode,
        BacktestStartHour = BacktestStartHour,
        BacktestEndHour = BacktestEndHour,
        BacktestMaxPoints = BacktestMaxPoints,
        BacktestWeekdays = [.. BacktestWeekdays.Order()],
        WeekdaysIncludeSaturday = true,
    };

    private void Save()
    {
        if (_path is null || _loading)
        {
            return;
        }

        JsonFileStore.Save(_path, ToData(), AppJsonContext.Default.SettingsData);
    }
}
