using AnomalyStudio.Core.Modes;

namespace AnomalyStudio.ViewModels;

public sealed partial class ModeDetailViewModel(
    NavigationService navigation, ModeRepository modes, AnomalyWorkspace workspace, AiSqlAssistant ai, AppSettings settings)
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
    [NotifyPropertyChangedFor(nameof(DefaultSqlNotice))]
    [NotifyPropertyChangedFor(nameof(HasDefaultSqlNotice))]
    public partial string Sql { get; set; } = string.Empty;

    /// <summary>既定モードを編集しているか（削除はできず、「既定に戻す」を出す）。</summary>
    [ObservableProperty]
    public partial bool IsDefault { get; set; }

    /// <summary>削除できるか（保存済みの自作モード）。</summary>
    [ObservableProperty]
    public partial bool CanDelete { get; set; }

    /// <summary>既定モードの SQL を既定から変えているときの注意。</summary>
    public string DefaultSqlNotice => HasDefaultSqlNotice
        ? "既定の SQL から変更しています。アプリの更新で既定の SQL が変わっても反映されず、ウォークフォワードは DuckDB を通すので遅くなります。"
          + "複合モードの並び順は既定の指標のままです（SQL は候補の絞り込みに使います）。「既定に戻す」で元に戻せます"
        : string.Empty;

    public bool HasDefaultSqlNotice => _mode is { IsDefault: true } mode && !(mode with { Sql = Sql }).HasDefaultSql;

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public string SqlLines => $"{Sql.Split('\n').Length} 行";

    /// <summary>SQL で使える列（candidate_stats の定義）。</summary>
    public string SchemaHint => AnalysisDatabase.CandidateStatsSchema;

    public void Load(ModeDefinition mode)
    {
        // 一覧から開いたときの内容が古いことがあるので、保存済みの最新を使う
        mode = modes.Find(mode.Id) ?? mode;
        _mode = mode;
        Title = "モード詳細設定";
        IsDefault = mode.IsDefault;
        CanDelete = !mode.IsDefault;
        ErrorMessage = string.Empty;
        Name = mode.Name;
        Description = mode.Description;
        Sql = mode.Sql;
        OnPropertyChanged(nameof(DefaultSqlNotice));
        OnPropertyChanged(nameof(HasDefaultSqlNotice));
    }

    /// <summary>新規モードの入力を始める。SQL は勝率重視を雛形にし、一覧への追加は保存時に行う。</summary>
    public void LoadNew()
    {
        _mode = null;
        Title = "モードを追加";
        IsDefault = false;
        CanDelete = false;
        ErrorMessage = string.Empty;
        Name = modes.NewName();
        Description = string.Empty;
        Sql = ModeDefinition.BuiltInSql("score_win_rate");
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = string.Empty;
        if (BusyMessage() is { } busy)
        {
            ErrorMessage = busy;
            return;
        }

        if (ModeDefinition.ValidateName(Name, modes.All, _mode?.Id) is { } nameError)
        {
            ErrorMessage = nameError;
            return;
        }

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

        // エントリー画面はモードを名前で覚えているので、名前を変えたら覚えている名前も変える（保存で一覧を読み直す前に）
        if (_mode is not null && _mode.Name != Name && settings.EntriesMode == _mode.Name)
        {
            settings.EntriesMode = Name;
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

    /// <summary>自作のモードを削除する（確認は画面が行う）。エントリー画面で選んでいれば「すべて」に戻す。</summary>
    [RelayCommand]
    private void Delete()
    {
        ErrorMessage = string.Empty;
        if (_mode is not { IsDefault: false } mode)
        {
            return;
        }

        if (BusyMessage() is { } busy)
        {
            ErrorMessage = busy;
            return;
        }

        if (settings.EntriesMode == mode.Name)
        {
            settings.EntriesMode = ModeDefinition.AllModesLabel;
        }

        modes.Delete(mode.Id);

        navigation.GoBack();
    }

    /// <summary>既定モードの名前・説明・SQL を既定に戻す（入力欄に入れるだけで、保存で反映する）。</summary>
    [RelayCommand]
    private void ResetToDefault()
    {
        if (_mode is not null && ModeDefinition.Defaults.FirstOrDefault(d => d.Id == _mode.Id) is { } d)
        {
            ErrorMessage = string.Empty;
            Name = d.Name;
            Description = d.Description;
            Sql = d.Sql;
        }
    }

    /// <summary>取込・分析の実行中は、途中で抽出条件が変わらないように保存・削除しない。</summary>
    private string? BusyMessage() =>
        workspace.IsBusy ? "取込・分析の実行中は保存・削除できません。終わってからもう一度押してください" : null;

    [RelayCommand]
    private void Cancel() => navigation.GoBack();
}
