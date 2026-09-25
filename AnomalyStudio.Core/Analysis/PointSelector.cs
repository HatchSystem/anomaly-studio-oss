namespace AnomalyStudio.Core.Analysis;

/// <summary>ポイント抽出の対象候補（モードの SQL が返す行）。</summary>
public readonly record struct PointCandidate(TradeDirection Direction, int EntryMinute, int HoldMinutes, double Score);

/// <summary>抽出されたエントリーポイント。</summary>
public readonly record struct EntryPoint(int Rank, TradeDirection Direction, int EntryMinute, int HoldMinutes, double Score)
{
    public int CloseMinute => (EntryMinute + HoldMinutes) % CandidateGrid.MinutesPerDay;
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

    /// <summary>
    /// 既定モードの SQL（品質除外なし・保有 3〜15 分・score が値あり）と同じ候補を、DuckDB を通さずに候補統計から並べる。
    /// <paramref name="scoreColumn"/> は並び順に使う列、<paramref name="valueColumn"/> は score として返す列（複合ポイントでは指標のもとの値。null なら並び順の列）。
    /// 結果は <see cref="OrderByPriority"/> の順（score 降順 → Entry 昇順 → 保有 昇順 → Long 先）で、SQL の ORDER BY と同じ。
    /// </summary>
    public static IReadOnlyList<PointCandidate> BuiltInCandidates(CandidateTable table, string scoreColumn, string? valueColumn = null)
    {
        var scores = table.Column(scoreColumn) ?? throw new ArgumentException($"列 {scoreColumn} は並び順に使えません。", nameof(scoreColumn));
        var values = valueColumn is null ? scores : table.Column(valueColumn) ?? throw new ArgumentException($"列 {valueColumn} は使えません。", nameof(valueColumn));

        // 候補は最大 80,640 件。並べ替えは LINQ の OrderBy より、構造体の鍵で配列を並べ替えるほうが数倍速い（ウォークフォワードで基準日ごとに行う）
        var indices = new int[CandidateGrid.Count];
        var keys = new PriorityKey[CandidateGrid.Count];
        var count = 0;
        for (var i = 0; i < CandidateGrid.Count; i++)
        {
            var hold = CandidateGrid.Hold(i);
            if (table.QualityExcluded[i] || hold < BuiltInHoldMin || hold > BuiltInHoldMax
                || !double.IsFinite(scores[i]) || !double.IsFinite(values[i]))
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

    /// <summary>既定モードのポイント抽出を SQL なしで行う（<see cref="BuiltInCandidates"/> → <see cref="SelectNonOverlapping"/>）。</summary>
    public static IReadOnlyList<EntryPoint> SelectBuiltIn(CandidateTable table, string scoreColumn, int limit = DefaultLimit) =>
        SelectNonOverlapping(BuiltInCandidates(table, scoreColumn), limit);

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
