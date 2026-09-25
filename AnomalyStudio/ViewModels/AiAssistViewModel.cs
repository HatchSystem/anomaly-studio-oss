namespace AnomalyStudio.ViewModels;

/// <summary>
/// AI アシスト: 要望 → LLM で SQL を生成 → 文法チェック → LLM で指示文との照合
/// （エラーや不一致なら「修正を依頼」か「キャンセル」）→ テスト抽出。
/// テスト抽出に成功した SQL（その後に書き換えていないもの）だけを設定に反映できる。
/// </summary>
public sealed partial class AiAssistViewModel(AiSqlAssistant assistant, AnomalyWorkspace workspace) : ObservableObject
{
    /// <summary>テスト抽出に成功した SQL。表示中の SQL と同じときだけ反映できる。</summary>
    private string? _testedSql;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    public partial string Prompt { get; set; } = AiSqlAssistant.SamplePrompt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SqlLines))]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    public partial string GeneratedSql { get; set; } = string.Empty;

    /// <summary>生成・文法チェックの状況。</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = "要望を入力して「生成」を押してください";

    [ObservableProperty]
    public partial bool StatusIsError { get; set; }

    /// <summary>文法チェックのエラーや指示との不一致（「修正を依頼」で LLM に送る内容）。</summary>
    [ObservableProperty]
    public partial string ValidationError { get; set; } = string.Empty;

    /// <summary>問題の種類（「文法エラー」「指示との不一致」）。</summary>
    [ObservableProperty]
    public partial string ProblemTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasValidationError { get; set; }

    [ObservableProperty]
    public partial string TestResult { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(TestFailed))]
    public partial bool TestPassed { get; set; }

    public bool TestFailed => !TestPassed && TestResult.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(RequestFixCommand), nameof(TestExtractCommand))]
    public partial bool IsBusy { get; set; }

    public string SqlLines => GeneratedSql.Length == 0 ? "未生成" : $"{GeneratedSql.Split('\n').Length} 行";

    /// <summary>テスト抽出に成功した SQL をそのまま反映できる。</summary>
    public bool CanApply => TestPassed && !IsBusy && GeneratedSql == _testedSql;

    partial void OnGeneratedSqlChanged(string value)
    {
        if (value != _testedSql)
        {
            TestPassed = false;
            TestResult = string.Empty;
            OnPropertyChanged(nameof(TestFailed));
        }
    }

    partial void OnTestResultChanged(string value) => OnPropertyChanged(nameof(TestFailed));

    private bool CanGenerate() => !IsBusy && !string.IsNullOrWhiteSpace(Prompt);

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private Task GenerateAsync() => AskAsync("SQL を生成中…", () => assistant.GenerateSqlAsync(Prompt));

    private bool CanRequestFix() => !IsBusy && HasValidationError;

    /// <summary>エラー内容と一緒に LLM へ送り、直した SQL をもらう。</summary>
    [RelayCommand(CanExecute = nameof(CanRequestFix))]
    private Task RequestFixAsync()
    {
        var (sql, error) = (GeneratedSql, ValidationError);
        return AskAsync("エラー内容を添えて修正を依頼中…", () => assistant.RepairSqlAsync(Prompt, sql, error));
    }

    /// <summary>修正を依頼しない。SQL はエラーのまま（手で直すか、生成し直す）。</summary>
    [RelayCommand]
    private void CancelFix()
    {
        HasValidationError = false;
        RequestFixCommand.NotifyCanExecuteChanged();
        SetStatus($"{ProblemTitle}のままです。SQL を手で直すか、生成し直してください（「テスト抽出」に成功すれば反映できます）", isError: true);
    }

    private async Task AskAsync(string busyMessage, Func<Task<string>> ask)
    {
        IsBusy = true;
        HasValidationError = false;
        SetStatus(busyMessage, isError: false);
        try
        {
            GeneratedSql = await ask();
            if (await ValidateAsync())
            {
                await ReviewAsync();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            SetStatus(ex is TaskCanceledException ? "AI の応答が時間内に返りませんでした" : ex.Message, isError: true);
        }
        finally
        {
            IsBusy = false;
            RequestFixCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>文法チェック。エラーなら「修正を依頼」か「キャンセル」を選んでもらう。</summary>
    private async Task<bool> ValidateAsync()
    {
        try
        {
            await workspace.ValidateModeSqlAsync(GeneratedSql);
            ValidationError = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is ModeSqlException or DuckDB.NET.Data.DuckDBException)
        {
            ShowProblem("文法エラー", ex.Message, "生成した SQL にエラーがあります。修正を依頼しますか？");
            return false;
        }
    }

    /// <summary>
    /// 指示文と SQL が一致しているかを LLM に確かめる。一致しなければ「修正を依頼」か「キャンセル」を選んでもらう。
    /// 照合そのものができなかったときは止めず、テスト抽出で確かめてもらう。
    /// </summary>
    private async Task ReviewAsync()
    {
        SetStatus("文法チェック OK。指示文と SQL が一致しているか確認中…", isError: false);
        try
        {
            var review = await assistant.ReviewSqlAsync(Prompt, GeneratedSql);
            if (review is null)
            {
                SetStatus("文法チェック OK（指示との照合は応答を読めずにできませんでした）。「テスト抽出」で確認してください", isError: false);
            }
            else if (review.Match)
            {
                SetStatus("文法チェック・指示との照合 OK。「テスト抽出」で抽出されるポイントを確認してください", isError: false);
            }
            else
            {
                var issues = review.Issues.Count == 0 ? ["指示と一致しない点があります"] : review.Issues;
                ShowProblem("指示との不一致", string.Join("\n", issues.Select(i => "・" + i)), "生成した SQL が指示と一致していない可能性があります。修正を依頼しますか？");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            SetStatus("文法チェック OK（指示との照合はできませんでした: " + ex.Message + "）。「テスト抽出」で確認してください", isError: false);
        }
    }

    private void ShowProblem(string title, string detail, string status)
    {
        ProblemTitle = title;
        ValidationError = detail;
        HasValidationError = true;
        SetStatus(status, isError: true);
    }

    private void SetStatus(string text, bool isError) => (Status, StatusIsError) = (text, isError);

    private bool CanTestExtract() => !IsBusy;

    /// <summary>SQL を最新の分析結果で実行し、抽出されるポイント数を表示する。成功した SQL だけを反映できる。</summary>
    [RelayCommand(CanExecute = nameof(CanTestExtract))]
    private async Task TestExtractAsync()
    {
        TestPassed = false;
        if (GeneratedSql.Trim().Length == 0)
        {
            TestResult = "先に SQL を生成してください";
            return;
        }

        if (!workspace.CanTestModeSql)
        {
            TestResult = "分析結果がないためテスト抽出できません。データ抽出設定の「分析を実行」のあとで試してください";
            return;
        }

        var sql = GeneratedSql;
        try
        {
            TestResult = "テスト抽出: " + await workspace.TestModeSqlAsync(sql);
            _testedSql = sql;
            TestPassed = sql == GeneratedSql;
        }
        catch (Exception ex) when (ex is ModeSqlException or DuckDB.NET.Data.DuckDBException)
        {
            TestResult = "テスト抽出に失敗しました: " + ex.Message;
        }
    }
}
