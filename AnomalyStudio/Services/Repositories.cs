using AnomalyStudio.Core;
using AnomalyStudio.Core.Modes;
using AnomalyStudio.Core.Storage;
using AnomalyStudio.Core.Symbols;

namespace AnomalyStudio.Services;

/// <summary>分析対象の銘柄（symbols.json）。ユーザーが追加・削除・有効化できる。</summary>
public sealed class SymbolRepository(AppPaths paths)
{
    private List<SymbolProfile> _items = JsonFileStore.Load(
        paths.SymbolsFile, CoreJsonContext.Default.ListSymbolProfile, () => [.. SymbolCatalog.Defaults]);

    public event EventHandler? Changed;

    public IReadOnlyList<SymbolProfile> All => _items;

    public IReadOnlyList<SymbolProfile> Enabled => [.. _items.Where(s => s.Enabled)];

    public SymbolProfile? Find(string id) => _items.FirstOrDefault(s => s.Id == id);

    public void Add(SymbolProfile symbol)
    {
        if (_items.Any(s => s.Id == symbol.Id))
        {
            return;
        }

        _items = [.. _items, symbol];
        Save();
    }

    public void Update(SymbolProfile symbol)
    {
        _items = [.. _items.Select(s => s.Id == symbol.Id ? symbol : s)];
        Save();
    }

    public void Remove(string id)
    {
        _items = [.. _items.Where(s => s.Id != id)];
        Save();
    }

    private void Save()
    {
        JsonFileStore.Save(paths.SymbolsFile, _items, CoreJsonContext.Default.ListSymbolProfile);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>データ抽出モード（modes.json）。</summary>
public sealed class ModeRepository(AppPaths paths)
{
    private List<ModeDefinition> _items = Load(paths);

    /// <summary>
    /// 保存済みのモードを読む。廃止した既定モード（時間効率）が残っていれば取り除き、
    /// 後から追加した既定モードが無ければ無効の状態で足して（既存の抽出結果を変えない）、変わったら保存し直す。
    /// </summary>
    private static List<ModeDefinition> Load(AppPaths paths)
    {
        var items = JsonFileStore.Load(paths.ModesFile, CoreJsonContext.Default.ListModeDefinition, () => [.. ModeDefinition.Defaults]);
        var kept = items.Where(m => !ModeDefinition.RetiredIds.Contains(m.Id)).ToList();
        kept.AddRange(ModeDefinition.Defaults.Where(d => kept.All(m => m.Id != d.Id)).Select(d => d with { Enabled = false }));
        if (kept.Count != items.Count)
        {
            JsonFileStore.Save(paths.ModesFile, kept, CoreJsonContext.Default.ListModeDefinition);
        }

        return kept;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<ModeDefinition> All => _items;

    public IReadOnlyList<ModeDefinition> Enabled => [.. _items.Where(m => m.Enabled)];

    public ModeDefinition? Find(string id) => _items.FirstOrDefault(m => m.Id == id);

    public void Save(ModeDefinition mode)
    {
        _items = _items.Any(m => m.Id == mode.Id)
            ? [.. _items.Select(m => m.Id == mode.Id ? mode : m)]
            : [.. _items, mode];
        JsonFileStore.Save(paths.ModesFile, _items, CoreJsonContext.Default.ListModeDefinition);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string NewName()
    {
        var i = 1;
        while (_items.Any(m => m.Name == $"新しいモード {i}"))
        {
            i++;
        }

        return $"新しいモード {i}";
    }
}
