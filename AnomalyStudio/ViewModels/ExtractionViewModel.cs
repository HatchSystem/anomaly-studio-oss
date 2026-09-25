using System.Collections.ObjectModel;
using AnomalyStudio.Core;
using AnomalyStudio.Core.Modes;

namespace AnomalyStudio.ViewModels;

public sealed partial class ModeItemViewModel(ModeDefinition mode, ModeRepository repository) : ObservableObject
{
    public ModeDefinition Mode { get; private set; } = mode;

    public string Name => Mode.Name;

    public string Description => Mode.Description;

    public bool IsEnabled
    {
        get => Mode.Enabled;
        set
        {
            if (Mode.Enabled == value)
            {
                return;
            }

            Mode = Mode with { Enabled = value };
            repository.Save(Mode);
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateLabel));
        }
    }

    public string StateLabel => IsEnabled ? "オン" : "オフ";

    /// <summary>データの大きさ: 各銘柄の最新の分析で、このモードが抽出するポイント数と候補数。</summary>
    [ObservableProperty]
    public partial string DataSize { get; set; } = "データ: 集計中…";
}

/// <summary>銘柄ごとの取込状況と最新の分析結果（データ抽出設定の「分析」カード）。</summary>
public sealed record SymbolStatusRow(string Symbol, string Coverage, string Analysis);

public sealed partial class ExtractionViewModel : ObservableObject
{
    private readonly NavigationService _navigation;

    public ExtractionViewModel(AnomalyWorkspace workspace, ModeRepository modes, NavigationService navigation)
    {
        Workspace = workspace;
        _navigation = navigation;
        Modes = new(modes.All.Select(m => new ModeItemViewModel(m, modes)));
        RefreshStatuses();
    }

    public AnomalyWorkspace Workspace { get; }

    public ObservableCollection<ModeItemViewModel> Modes { get; }

    public ObservableCollection<SymbolStatusRow> Statuses { get; } = [];

    public void Start()
    {
        Workspace.Changed += OnWorkspaceChanged;
        _ = RefreshModeSizesAsync();
    }

    public void Stop() => Workspace.Changed -= OnWorkspaceChanged;

    private void OnWorkspaceChanged(object? sender, EventArgs e)
    {
        RefreshStatuses();
        _ = RefreshModeSizesAsync();
    }

    /// <summary>各モードの SQL を銘柄ごとの最新の分析で試し、抽出されるポイント数と候補数を出す。</summary>
    private async Task RefreshModeSizesAsync()
    {
        foreach (var item in Modes)
        {
            try
            {
                item.DataSize = "データ: " + await Workspace.TestModeSqlAsync(item.Mode.Sql);
            }
            catch (Exception ex) when (ex is ModeSqlException or DuckDB.NET.Data.DuckDBException)
            {
                item.DataSize = "データ: SQL を実行できません";
            }
        }
    }

    private void RefreshStatuses()
    {
        Statuses.Clear();
        foreach (var s in Workspace.Statuses.Where(s => s.Symbol.Enabled))
        {
            var coverage = s.BarCount == 0
                ? "未取得"
                : $"{s.BarCount:N0} 本（{Jst.FromUtc(s.FirstBarUtc!.Value):yyyy/MM/dd} 〜 {Jst.FromUtc(s.LastBarUtc!.Value):yyyy/MM/dd HH:mm}）";
            var analysis = s.LatestRun is { } run
                ? $"{run.ReportDate:yyyy/MM/dd} 基準（{Jst.FromUtc(run.CreatedAtUtc):MM/dd HH:mm} 実行）"
                : s.NeedsReanalysis ? "再分析が必要（アプリの更新で計算方式が変わりました）" : "未分析";
            Statuses.Add(new SymbolStatusRow(s.Symbol.Id, coverage, analysis));
        }
    }

    [RelayCommand]
    private Task RunAnalysisAsync() => Workspace.RunAnalysisAsync();

    [RelayCommand]
    private Task UpdateDataAsync() => Workspace.UpdateMarketDataAsync();

    [RelayCommand]
    private void Cancel() => Workspace.Cancel();

    /// <summary>データの初期化（確認は画面が行う）。</summary>
    [RelayCommand]
    private Task ResetDataAsync() => Workspace.ResetDataAsync();

    /// <summary>いきなり追加せず、空のモード詳細設定を開く。保存したときに一覧へ追加される。</summary>
    [RelayCommand]
    private void AddMode() => _navigation.Navigate(NavigationService.ModeDetail);

    [RelayCommand]
    private void Open(ModeItemViewModel item) => _navigation.Navigate(NavigationService.ModeDetail, item.Mode);
}
