using AnomalyStudio.Core.Modes;

namespace AnomalyStudio.ViewModels;

public sealed partial class ModeDetailViewModel(NavigationService navigation, ModeRepository modes, AnomalyWorkspace workspace, AiSqlAssistant ai)
    : ObservableObject
{
    /// <summary>AI アシストは設定の接続テストに合格しているときだけ使える。</summary>
    public bool CanUseAiAssist => ai.IsConnected;

    public string AiAssistHint => ai.IsConnected ? "自然文から抽出 SQL を生成します" : "設定の「接続テスト」に合格すると使えます";

    /// <summary>保存した API キーがあれば接続テストをして、AI アシストを使えるようにする。</summary>
    public async Task PrepareAiAssistAsync()
    {
        if (await ai.EnsureConnectedAsync() is not null)
        {
            OnPropertyChanged(nameof(CanUseAiAssist));
            OnPropertyChanged(nameof(AiAssistHint));
        }
    }

    private ModeDefinition? _mode;

    [ObservableProperty]
    public partial string Title { get; set; } = "モード詳細設定";

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SqlLines))]
    public partial string Sql { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public string SqlLines => $"{Sql.Split('\n').Length} 行";

    /// <summary>SQL で使える列（candidate_stats の定義）。</summary>
    public string SchemaHint => AnalysisDatabase.CandidateStatsSchema;

    public void Load(ModeDefinition mode)
    {
        _mode = mode;
        Title = "モード詳細設定";
        Name = mode.Name;
        Description = mode.Description;
        Sql = mode.Sql;
    }

    /// <summary>新規モードの入力を始める。SQL は勝率重視を雛形にし、一覧への追加は保存時に行う。</summary>
    public void LoadNew()
    {
        _mode = null;
        Title = "モードを追加";
        Name = modes.NewName();
        Description = string.Empty;
        Sql = ModeDefinition.BuiltInSql("score_win_rate");
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = string.Empty;
        try
        {
            ModeSql.Prepare(Sql, 0);
            if (workspace.HasAnalysis)
            {
                await workspace.TestModeSqlAsync(Sql);
            }
        }
        catch (Exception ex) when (ex is ModeSqlException or DuckDB.NET.Data.DuckDBException)
        {
            ErrorMessage = ex.Message;
            return;
        }

        // 追加したモードは既定でオフ（抽出結果を確認してから有効にする）
        modes.Save(_mode is null
            ? new ModeDefinition
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = Name,
                Description = string.IsNullOrWhiteSpace(Description) ? "説明なし" : Description,
                Sql = Sql,
                Enabled = false,
            }
            : _mode with { Name = Name, Description = Description, Sql = Sql });

        navigation.GoBack();
    }

    [RelayCommand]
    private void Cancel() => navigation.GoBack();
}
