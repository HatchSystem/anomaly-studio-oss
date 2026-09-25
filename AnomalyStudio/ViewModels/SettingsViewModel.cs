using System.Collections.ObjectModel;
using System.Diagnostics;
using AnomalyStudio.Core;
using AnomalyStudio.Core.Symbols;

namespace AnomalyStudio.ViewModels;

/// <summary>設定の「銘柄」グループの 1 行。</summary>
public sealed partial class SymbolItemViewModel(SymbolProfile symbol, string coverage, SymbolRepository repository, Func<SymbolItemViewModel, Task> remove)
    : ObservableObject
{
    public SymbolProfile Symbol { get; private set; } = symbol;

    public string Id => Symbol.Id;

    public string Detail => $"{Symbol.DukascopyInstrument} · 単位 {Symbol.UnitLabel}";

    public string Coverage { get; } = coverage;

    public bool IsEnabled
    {
        get => Symbol.Enabled;
        set
        {
            if (Symbol.Enabled != value)
            {
                Symbol = Symbol with { Enabled = value };
                repository.Update(Symbol);
                OnPropertyChanged();
            }
        }
    }

    [RelayCommand]
    private Task RemoveAsync() => remove(this);
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly string[] DayNames = ["日曜日", "月曜日", "火曜日", "水曜日", "木曜日", "金曜日", "土曜日"];

    private readonly IEconomicCalendarService _data;
    private readonly AnomalyWorkspace _workspace;
    private readonly AiSqlAssistant _ai;

    public SettingsViewModel(AppSettings settings, IEconomicCalendarService data, AnomalyWorkspace workspace, AiSqlAssistant ai, IUpdateService updates)
    {
        Settings = settings;
        _data = data;
        _workspace = workspace;
        _ai = ai;
        Updates = updates;
        ApiKey = ai.ApiKey;
        UpdateConnection();
        Countries = data.GetCountries()
            .Select(c => new CountryOptionViewModel(c, settings.CalendarCountries.Contains(c.Code), OnCountryChanged))
            .ToList();
        RefreshSymbols();
    }

    // ------------------------------------------------------------------
    // 銘柄
    // ------------------------------------------------------------------

    public ObservableCollection<SymbolItemViewModel> SymbolItems { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<string> AvailableInstruments { get; set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSymbolCommand))]
    public partial string? SelectedInstrument { get; set; }

    private void RefreshSymbols()
    {
        SymbolItems.Clear();
        foreach (var symbol in _workspace.Symbols.All)
        {
            var status = _workspace.Statuses.FirstOrDefault(s => s.Symbol.Id == symbol.Id);
            var coverage = status is { BarCount: > 0 }
                ? $"{status.BarCount:N0} 本（{Jst.FromUtc(status.LastBarUtc!.Value):yyyy/MM/dd HH:mm} まで）"
                    + (status.LatestRun is { } run ? $" · {run.ReportDate:yyyy/MM/dd} 基準で分析済み" : status.NeedsReanalysis ? " · 再分析が必要" : " · 未分析")
                : "未取得（データ抽出設定の「分析を実行」で取り込みます）";
            SymbolItems.Add(new SymbolItemViewModel(symbol, coverage, _workspace.Symbols, RemoveSymbolAsync));
        }

        var existing = _workspace.Symbols.All.Select(s => s.DukascopyInstrument).ToHashSet();
        AvailableInstruments = [.. SymbolCatalog.All.Select(s => s.DukascopyInstrument).Where(i => !existing.Contains(i))];
        SelectedInstrument = null;
    }

    private bool CanAddSymbol() => !string.IsNullOrEmpty(SelectedInstrument);

    [RelayCommand(CanExecute = nameof(CanAddSymbol))]
    private void AddSymbol()
    {
        _workspace.Symbols.Add(SymbolCatalog.Create(SelectedInstrument!));
        RefreshSymbols();
    }

    private async Task RemoveSymbolAsync(SymbolItemViewModel item)
    {
        _workspace.Symbols.Remove(item.Id);
        await _workspace.RemoveSymbolDataAsync(item.Id);
        RefreshSymbols();
    }

    // ------------------------------------------------------------------
    // データ
    // ------------------------------------------------------------------

    public string DataDirectory => _workspace.Paths.DataDirectory;

    [RelayCommand]
    private void OpenDataFolder()
    {
        Directory.CreateDirectory(DataDirectory);
        Process.Start(new ProcessStartInfo { FileName = DataDirectory, UseShellExecute = true });
    }

    public IReadOnlyList<string> ScheduleDayOptions => DayNames;

    public string ScheduleDayLabel
    {
        get => DayNames[(int)Settings.ScheduleDay];
        set => Settings.ScheduleDay = (DayOfWeek)Math.Max(0, Array.IndexOf(DayNames, value));
    }

    public IReadOnlyList<string> ScheduleTimeOptions { get; } = [.. Enumerable.Range(0, 24).Select(h => $"{h:00}:00")];

    public IReadOnlyList<string> RunsToKeepOptions { get; } = ["1 回", "3 回", "4 回", "8 回", "12 回", "26 回", "52 回"];

    public string RunsToKeepLabel
    {
        get => $"{Settings.RunsToKeep} 回";
        set => Settings.RunsToKeep = int.TryParse(value.Split(' ')[0], out var n) ? n : AnomalyStudio.Core.Analysis.AnalysisRunner.DefaultRunsToKeep;
    }

    // ------------------------------------------------------------------

    public AppSettings Settings { get; }

    public IReadOnlyList<CountryOptionViewModel> Countries { get; }

    [RelayCommand]
    private void SelectAllCountries()
    {
        var target = !Countries.All(c => c.IsChecked);
        foreach (var c in Countries)
        {
            c.IsChecked = target;
        }
    }

    private void OnCountryChanged()
    {
        Settings.CalendarCountries.Clear();
        foreach (var c in Countries.Where(c => c.IsChecked))
        {
            Settings.CalendarCountries.Add(c.Code);
        }

        Settings.NotifyCalendarFilterChanged();
        OnPropertyChanged(nameof(CountryLabel));
    }

    public IReadOnlyList<string> ThemeOptions => AppSettings.ThemeOptions;

    public IReadOnlyList<string> StartScreenOptions => AppSettings.StartScreenOptions;

    public IReadOnlyList<string> CalendarRefreshOptions { get; } = ["起動時のみ", "起動時 + 毎日 06:00", "1時間ごと"];

    public IReadOnlyList<string> CalendarStarOptions => CalendarViewModel.ImportanceLabels;

    public string CalendarStars
    {
        get => CalendarStarOptions[Math.Clamp(Settings.CalendarMinStars, 0, 5)];
        set => Settings.CalendarMinStars = Math.Max(0, CalendarStarOptions.ToList().IndexOf(value));
    }

    public string CountryLabel
    {
        get
        {
            var all = _data.GetCountries();
            var selected = all.Where(c => Settings.CalendarCountries.Contains(c.Code)).Select(c => c.Code).ToList();
            return selected.Count == all.Count ? "すべての国"
                : selected.Count == 0 ? "未選択"
                : selected.Count > 4 ? $"{string.Join("・", selected.Take(4))} ほか{selected.Count - 4}"
                : string.Join("・", selected);
        }
    }

    public string Version { get; } = $"バージョン {ProductInfo.Version}";

    // ------------------------------------------------------------------
    // 更新
    // ------------------------------------------------------------------

    /// <summary>更新の状態（画面は StatusText・State を表示する）。</summary>
    public IUpdateService Updates { get; }

    [RelayCommand]
    private Task CheckForUpdatesAsync() => Updates.CheckAsync();

    [ObservableProperty]
    public partial string RestartMessage { get; set; } = string.Empty;

    /// <summary>ダウンロード済みの更新を反映して再起動する。取込・分析・バックテストの実行中は中断しないように待ってもらう。</summary>
    [RelayCommand]
    private async Task RestartToUpdateAsync()
    {
        if (_workspace.IsWorking)
        {
            RestartMessage = "取込・分析・バックテストの実行中は再起動できません。終わってからもう一度押してください（次にアプリを起動したときにも更新されます）";
            return;
        }

        RestartMessage = string.Empty;
        await _workspace.PrepareForExitAsync();
        Updates.RestartToApply();
    }

    /// <summary>API キーの保存先の説明（Windows は資格情報マネージャー、macOS はキーチェーン）。</summary>
    public string KeyStorageNote { get; } = $"接続テストに合格すると、{CredentialStore.StoreName}に暗号化して保存します（設定ファイルには書きません）";

    public string KeyDeleteTip { get; } = $"保存したAPIキーを{CredentialStore.StoreName}から削除";

    [ObservableProperty]
    public partial string ApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConnectionStatus { get; set; } = "未確認";

    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    /// <summary>資格情報マネージャーに API キーを保存している。</summary>
    [ObservableProperty]
    public partial bool HasStoredKey { get; set; }

    /// <summary>保存した API キーを資格情報マネージャーから消す。</summary>
    [RelayCommand]
    private void DeleteStoredKey()
    {
        _ai.DeleteStoredKey();
        ApiKey = string.Empty;
        UpdateConnection();
        ConnectionStatus = "保存したAPIキーを削除しました";
    }

    partial void OnApiKeyChanged(string value)
    {
        if (value != _ai.ApiKey)
        {
            ResetConnection();
        }
    }

    private void ResetConnection()
    {
        _ai.Invalidate();
        UpdateConnection();
    }

    /// <summary>
    /// 画面を開いている間だけ、接続テストの取り消し（ベースURL・モデルの変更）を表示に反映する。
    /// 保存した API キーがあってまだ確かめていなければ、そのキーで接続テストをする。
    /// </summary>
    public async void Start()
    {
        _ai.ConnectionChanged += OnConnectionChanged;
        if (!_ai.IsConnected && _ai.ApiKey.Length > 0)
        {
            ConnectionStatus = "保存したAPIキーで確認中…";
            if (await _ai.EnsureConnectedAsync() is { } result)
            {
                UpdateConnection();
                ConnectionStatus = result;
            }
        }
    }

    public void Stop() => _ai.ConnectionChanged -= OnConnectionChanged;

    private void OnConnectionChanged(object? sender, EventArgs e) => UpdateConnection();

    private void UpdateConnection()
    {
        IsConnected = _ai.IsConnected;
        HasStoredKey = _ai.HasStoredKey;
        ConnectionStatus = _ai.IsConnected ? "接続済み" : "未確認（AIアシストは接続テストに合格すると使えます）";
    }

    /// <summary>
    /// ベースURLの /models を API キーで取得して確かめる。合格すると AI アシストを使え、
    /// API キーを資格情報マネージャーに保存する（次回の起動で読み込む）。
    /// </summary>
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        ConnectionStatus = "確認中…";
        var result = await _ai.TestConnectionAsync(Settings.AiBaseUrl, Settings.AiModel, ApiKey);
        UpdateConnection();
        ConnectionStatus = result;
    }
}
