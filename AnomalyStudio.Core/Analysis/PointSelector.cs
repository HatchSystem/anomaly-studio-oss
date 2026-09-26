namespace AnomalyStudio.Core.Analysis;

/// <summary>ポイント抽出の対象候補（モードの SQL が返す行）。</summary>
public readonly record struct PointCandidate(TradeDirection Direction, int EntryMinute, int HoldMinutes, double Score);

/// <summary>抽出されたエントリーポイント。</summary>
public readonly record struct EntryPoint(int Rank, TradeDirection Direction, int EntryMinute, int HoldMinutes, double Score)
{
    public int CloseMinute => (EntryMinute + HoldMinutes) % CandidateGrid.MinutesPerDay;
}

/// <summary>
/// 既定の形のモード SQL（<see cref="Modes.ModeDefinition.BuiltInSql(BuiltInSelection)"/>）が表す抽出条件: 並び順に使う列、使う保有時間、最低勝率。
/// <paramref name="Holds"/> が null なら既定の保有 3〜15 分、指定があればその保有時間（分）だけ。
/// <paramref name="MinWinRate"/> を指定すると、<paramref name="WinRatePeriods"/> の各期間（日）の勝率がすべてその値以上の候補だけ（勝率に値がない候補は除く）。
/// </summary>
public sealed record BuiltInSelection(
    string ScoreColumn, IReadOnlyList<int>? Holds = null, double? MinWinRate = null, IReadOnlyList<int>? WinRatePeriods = null)
{
    public bool AcceptsHold(int hold) =>
        Holds is null ? hold is >= PointSelector.BuiltInHoldMin and <= PointSelector.BuiltInHoldMax : Holds.Contains(hold);
}

/// <summary>
/// 時間帯が重ならないようにポイントを選ぶ（貪欲法）。
/// 占有範囲は半開区間 [Entry, Close) で、Close == 次の Entry は重複扱いしない（連続ドテン可）。
/// </summary>
public static class PointSelector
{
    public const int DefaultLimit = 50;

    /// <summary>
    /// 次の優先順（score 降順 → Entry 昇順 → 保有 昇順 → Long 先）に並べる。
    /// 既定モードの SQL と同じ並びで、SQL を通さずに選ぶときに使う。
    /// </summary>
    public static IEnumerable<PointCandidate> OrderByPriority(IEnumerable<PointCandidate> candidates) =>
        candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.EntryMinute)
            .ThenBy(c => c.HoldMinutes)
            .ThenBy(c => c.Direction);

    /// <summary>既定モードのポイント抽出条件（<see cref="Modes.ModeDefinition.BuiltInSql"/> と同じ）: 保有 3〜15 分。</summary>
    public const int BuiltInHoldMin = 3;
    public const int BuiltInHoldMax = 15;

    /// <summary>既定の保有時間（3〜15 分）で <see cref="BuiltInCandidates(CandidateTable, BuiltInSelection, string?)"/> を行う。</summary>
    public static IReadOnlyList<PointCandidate> BuiltInCandidates(CandidateTable table, string scoreColumn, string? valueColumn = null) =>
        BuiltInCandidates(table, new BuiltInSelection(scoreColumn), valueColumn);

    /// <summary>
    /// 既定の形のモード SQL（品質除外なし・保有時間の条件・score が値あり）と同じ候補を、DuckDB を通さずに候補統計から並べる。
    /// <paramref name="selection"/> は並び順に使う列と保有時間、<paramref name="valueColumn"/> は score として返す列（複合ポイントでは指標のもとの値。null なら並び順の列）。
    /// 結果は <see cref="OrderByPriority"/> の順（score 降順 → Entry 昇順 → 保有 昇順 → Long 先）で、SQL の ORDER BY と同じ。
    /// </summary>
    public static IReadOnlyList<PointCandidate> BuiltInCandidates(CandidateTable table, BuiltInSelection selection, string? valueColumn = null)
    {
        var scoreColumn = selection.ScoreColumn;
        var scores = table.Column(scoreColumn) ?? throw new ArgumentException($"列 {scoreColumn} は並び順に使えません。", nameof(selection));
        var values = valueColumn is null ? scores : table.Column(valueColumn) ?? throw new ArgumentException($"列 {valueColumn} は使えません。", nameof(valueColumn));
        var accepts = new bool[CandidateGrid.HoldMax + 1];
        for (var hold = CandidateGrid.HoldMin; hold <= CandidateGrid.HoldMax; hold++)
        {
            accepts[hold] = selection.AcceptsHold(hold);
        }

        var minWinRate = selection.MinWinRate ?? 0;
        var winRates = selection.MinWinRate is null ? [] : selection.WinRatePeriods!.Select(days => table.Period(days).WinRate).ToArray();

        // 候補は最大 172,800 件。並べ替えは LINQ の OrderBy より、構造体の鍵で配列を並べ替えるほうが数倍速い（ウォークフォワードで基準日ごとに行う）
        var indices = new int[CandidateGrid.Count];
        var keys = new PriorityKey[CandidateGrid.Count];
        var count = 0;
        for (var i = 0; i < CandidateGrid.Count; i++)
        {
            var hold = CandidateGrid.Hold(i);
            if (table.QualityExcluded[i] || !accepts[hold]
                || !double.IsFinite(scores[i]) || !double.IsFinite(values[i]) || !MeetsWinRate(winRates, i, minWinRate))
            {
                continue;
            }

            indices[count] = i;
            keys[count] = new PriorityKey(scores[i], PriorityKey.TieBreak(CandidateGrid.Entry(i), hold, CandidateGrid.Direction(i)));
            count++;
        }

        // 並びの鍵は (score 降順, Entry, 保有, 方向)。候補ごとに一意なので、並べ替えの安定性は問わない
        Array.Sort(keys, indices, 0, count);

        var list = new PointCandidate[count];
        for (var k = 0; k < count; k++)
        {
            var i = indices[k];
            list[k] = new PointCandidate(CandidateGrid.Direction(i), CandidateGrid.Entry(i), CandidateGrid.Hold(i), values[i]);
        }

        return list;
    }

    /// <summary>各期間の勝率がすべて <paramref name="min"/> 以上か（NaN = 値なしは満たさない。SQL の NULL と同じ）。</summary>
    private static bool MeetsWinRate(double[][] winRates, int i, double min)
    {
        foreach (var rates in winRates)
        {
            if (!(rates[i] >= min))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>並び順の鍵: score 降順 → (Entry, 保有, 方向) 昇順。構造体なので並べ替えで委譲を呼ばない。</summary>
    private readonly record struct PriorityKey(double Score, int Tie) : IComparable<PriorityKey>
    {
        /// <summary>(Entry 昇順, 保有 昇順, Long 先) を 1 つの整数にする。</summary>
        public static int TieBreak(int entry, int hold, TradeDirection direction) =>
            (((entry * CandidateGrid.HoldCount) + (hold - CandidateGrid.HoldMin)) * 2) + (int)direction;

        public int CompareTo(PriorityKey other)
        {
            var byScore = other.Score.CompareTo(Score);
            return byScore != 0 ? byScore : Tie.CompareTo(other.Tie);
        }
    }

    /// <summary>既定の保有時間（3〜15 分）で <see cref="SelectBuiltIn(CandidateTable, BuiltInSelection, int)"/> を行う。</summary>
    public static IReadOnlyList<EntryPoint> SelectBuiltIn(CandidateTable table, string scoreColumn, int limit = DefaultLimit) =>
        SelectBuiltIn(table, new BuiltInSelection(scoreColumn), limit);

    /// <summary>既定の形のモードのポイント抽出を SQL なしで行う（<see cref="BuiltInCandidates(CandidateTable, BuiltInSelection, string?)"/> → <see cref="SelectNonOverlapping"/>）。</summary>
    public static IReadOnlyList<EntryPoint> SelectBuiltIn(CandidateTable table, BuiltInSelection selection, int limit = DefaultLimit) =>
        SelectNonOverlapping(BuiltInCandidates(table, selection), limit);

    /// <summary>与えられた順に、既に選んだ時間帯と重ならない候補を最大 <paramref name="limit"/> 件選ぶ。</summary>
    public static IReadOnlyList<EntryPoint> SelectNonOverlapping(IEnumerable<PointCandidate> ordered, int limit = DefaultLimit)
    {
        var occupied = new bool[CandidateGrid.MinutesPerDay];
        var selected = new List<EntryPoint>(limit);

        foreach (var c in ordered)
        {
            if (!TryOccupy(occupied, c.EntryMinute, c.HoldMinutes))
            {
                continue;
            }

            selected.Add(new EntryPoint(selected.Count + 1, c.Direction, c.EntryMinute, c.HoldMinutes, c.Score));
            if (selected.Count >= limit)
            {
                break;
            }
        }

        return selected;
    }

    /// <summary>[Entry, Close) が空いていれば占有して true、重なれば何もせず false。</summary>
    internal static bool TryOccupy(bool[] occupied, int entry, int hold)
    {
        if (Overlaps(occupied, entry, hold))
        {
            return false;
        }

        for (var j = 0; j < hold; j++)
        {
            occupied[(entry + j) % CandidateGrid.MinutesPerDay] = true;
        }

        return true;
    }

    private static bool Overlaps(bool[] occupied, int entry, int hold)
    {
        for (var j = 0; j < hold; j++)
        {
            if (occupied[(entry + j) % CandidateGrid.MinutesPerDay])
            {
                return true;
            }
        }

        return false;
    }
}
