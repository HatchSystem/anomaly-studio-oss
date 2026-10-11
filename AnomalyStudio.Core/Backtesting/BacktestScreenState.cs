namespace AnomalyStudio.Core.Backtesting;

/// <summary>バックテスト系画面ごとに保存する条件。時間帯・ポイント数・曜日は既存の共有設定に持つ。</summary>
public sealed record BacktestScreenState(
    IReadOnlyList<string> SymbolIds,
    string Mode,
    BacktestCycle Cycle,
    DateTimeOffset? StartDate,
    DateTimeOffset? EndDate)
{
    /// <summary>初めて画面を開いたときだけ、直近 1 年を既定の検証期間にする。</summary>
    public static BacktestScreenState CreateDefault(
        IReadOnlyList<string> availableSymbols, string defaultMode, DateTimeOffset today) =>
        new(availableSymbols.Take(1).ToArray(), defaultMode, BacktestCycle.Weekly, today.AddYears(-1), today);

    /// <summary>
    /// 消えた選択肢だけを整理する。明示的な空選択と日付の未選択・逆転は保持し、別の日付へ補正しない。
    /// 選んだ銘柄がすべて消えたときは最初の有効な銘柄、モードが消えたときは既定モードへ戻す。
    /// </summary>
    public BacktestScreenState Restore(
        IReadOnlyList<string> availableSymbols, IReadOnlyList<string> availableModes, string defaultMode)
    {
        // 不完全な手編集 JSON の null は、利用者が選択を外した空配列と区別する。
        var symbols = availableSymbols.Where(id => SymbolIds?.Contains(id) == true).Distinct(StringComparer.Ordinal).ToArray();
        if ((SymbolIds is null || SymbolIds.Count > 0) && symbols.Length == 0)
        {
            symbols = availableSymbols.Take(1).ToArray();
        }

        var mode = availableModes.Contains(Mode) ? Mode
            : availableModes.Contains(defaultMode) ? defaultMode
            : availableModes.FirstOrDefault() ?? string.Empty;
        return this with
        {
            SymbolIds = symbols,
            Mode = mode,
            Cycle = Enum.IsDefined(Cycle) ? Cycle : BacktestCycle.Weekly,
        };
    }
}
