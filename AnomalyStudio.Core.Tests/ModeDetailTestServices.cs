global using CommunityToolkit.Mvvm.ComponentModel;
global using CommunityToolkit.Mvvm.Input;
global using AnomalyStudio.Services;
global using AnomalyStudio.Core.Storage;

using AnomalyStudio.Core.Modes;

namespace AnomalyStudio.Services;

// ソースリンクした ModeDetailViewModel の外部境界だけを置き換える。
// 入力通知・コマンド・保存可否の判断は、実 ViewModel と MVVM ソース生成器が実行する。
public sealed class NavigationService
{
    public int GoBackCount { get; private set; }

    public void GoBack() => GoBackCount++;
}

public sealed class ModeRepository
{
    public List<ModeDefinition> Items { get; } = [];

    public List<ModeDefinition> Saved { get; } = [];

    public IReadOnlyList<ModeDefinition> All => Items;

    public ModeDefinition? Find(string id) => Items.FirstOrDefault(mode => mode.Id == id);

    public string NewName() => "新しいモード 1";

    public void Save(ModeDefinition mode)
    {
        Saved.Add(mode);
        Items.RemoveAll(item => item.Id == mode.Id);
        Items.Add(mode);
    }

    public void Delete(string id) => Items.RemoveAll(mode => mode.Id == id);
}

public sealed class AnomalyWorkspace
{
    private readonly TaskCompletionSource _validation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _extraction = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsBusy { get; set; }

    public bool HasAnalysis { get; set; }

    public TaskCompletionSource<string> ValidationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<string> ExtractionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task ValidateModeSqlAsync(string sql)
    {
        ValidationStarted.SetResult(sql);
        return _validation.Task;
    }

    public Task<string> TestModeSqlAsync(string sql)
    {
        ExtractionStarted.SetResult(sql);
        return _extraction.Task;
    }

    public void CompleteValidation() => _validation.TrySetResult();

    public void CompleteExtraction() => _extraction.TrySetResult("テスト抽出結果");
}
