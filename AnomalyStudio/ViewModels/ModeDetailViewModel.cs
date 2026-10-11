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
    private object? _pendingSave;
    private bool _active;

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

    partial void OnNameChanged(string value) => InvalidatePendingSave(edited: true);

    partial void OnDescriptionChanged(string value) => InvalidatePendingSave(edited: true);

    partial void OnSqlChanged(string value) => InvalidatePendingSave(edited: true);

    private void InvalidatePendingSave(bool edited = false)
    {
        if (edited && _pendingSave is not null)
        {
            ErrorMessage = "検証中に内容が変わったため保存を取り消しました。もう一度保存してください";
        }

        _pendingSave = null;
    }

    /// <summary>画面を離れたあとは、検証待ちの保存や画面遷移を反映しない。</summary>
    public void Stop()
    {
        _active = false;
        InvalidatePendingSave();
    }

    /// <summary>既定モードを編集しているか（削除はできず、「既定に戻す」を出す）。</summary>
    [ObservableProperty]
    public partial bool IsDefault { get; set; }

    /// <summary>削除できるか（保存済みの自作モード）。</summary>
    [ObservableProperty]
    public partial bool CanDelete { get; set; }

    /// <summary>既定モードの SQL を既定から変えているときの注意。</summary>
    public string DefaultSqlNotice => HasDefaultSqlNotice
        ? "既定の SQL から変更しています。アプリの更新で既定の SQL が変わっても反映されず、ウォークフォワードは DuckDB を通すので遅くなります。"
          + "複合モードの並び順はモードの評価基準（平均または指定期間）のままです（SQL は候補の絞り込みに使います）。「既定に戻す」で元に戻せます"
        : string.Empty;

    public bool HasDefaultSqlNotice => _mode is { IsDefault: true } mode && !(mode with { Sql = Sql }).HasDefaultSql;

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public string SqlLines => $"{Sql.Split('\n').Length} 行";

    /// <summary>SQL で使える列（candidate_stats の定義）。</summary>
    public string SchemaHint => AnalysisDatabase.CandidateStatsSchema;

    public void Load(ModeDefinition mode)
    {
        InvalidatePendingSave();
        _active = true;
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
        InvalidatePendingSave();
        _active = true;
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
        if (!_active)
        {
            return;
        }

        ErrorMessage = string.Empty;
        if (BusyMessage() is { } busy)
        {
            ErrorMessage = busy;
            return;
        }

        var (mode, name, description, sql) = (_mode, Name, Description, Sql);
        if (ModeDefinition.ValidateName(name, modes.All, mode?.Id) is { } nameError)
        {
            ErrorMessage = nameError;
            return;
        }

        var attempt = _pendingSave = new object();
        try
        {
            // 初回の分析前でも、文法・列名・抽出結果の必須列を DB のスキーマで確かめる。
            await workspace.ValidateModeSqlAsync(sql);
            if (_pendingSave != attempt)
            {
                return;
            }

            if (workspace.HasAnalysis)
            {
                await workspace.TestModeSqlAsync(sql);
            }

            if (_pendingSave != attempt)
            {
                return;
            }

            // 非同期の検証中に自動分析が始まった場合や、別のモードが追加された場合も保存しない。
            if ((BusyMessage() ?? ModeDefinition.ValidateName(name, modes.All, mode?.Id)) is { } changedError)
            {
                ErrorMessage = changedError;
                return;
            }

            // エントリー画面はモードを名前で覚えているので、名前を変えたら覚えている名前も変える（保存で一覧を読み直す前に）
            if (mode is not null && mode.Name != name && settings.EntriesMode == mode.Name)
            {
                settings.EntriesMode = name;
            }

            // 追加したモードは既定でオフ（抽出結果を確認してから有効にする）。検証したスナップショットだけを保存する。
            modes.Save(mode is null
                ? new ModeDefinition
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = name,
                    Description = string.IsNullOrWhiteSpace(description) ? "説明なし" : description,
                    Sql = sql,
                    Enabled = false,
                }
                : mode with { Name = name, Description = description, Sql = sql });

            navigation.GoBack();
        }
        catch (Exception ex) when (ex is ModeSqlException or DuckDB.NET.Data.DuckDBException)
        {
            if (_pendingSave == attempt)
            {
                ErrorMessage = ex.Message;
            }
        }
        finally
        {
            if (_pendingSave == attempt)
            {
                _pendingSave = null;
            }
        }
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

        InvalidatePendingSave();
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
    private void Cancel()
    {
        Stop();
        navigation.GoBack();
    }
}
