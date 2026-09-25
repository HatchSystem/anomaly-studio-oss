namespace AnomalyStudio.Core.Analysis;

/// <summary>銘柄付きのエントリーポイント（複合ポイント）。<see cref="EntryPoint.Rank"/> は全銘柄を通した順位。</summary>
public sealed record SymbolPoint(string SymbolId, EntryPoint Point);

/// <summary>
/// 複合ポイント: すべての銘柄の候補を 1 つの順位表にまとめて選ぶ。
/// score（銘柄をまたいで比べられるもとの値）の降順 → Entry 昇順 → 保有 昇順 → Long 先 → 銘柄 ID 順に並べ、
/// 銘柄をまたいでも時間帯が重ならない（同時に持つポジションは 1 つ）ように最大 <c>limit</c> 件を選ぶ。
/// </summary>
public static class CompositePointSelector
{
    public static IReadOnlyList<SymbolPoint> Select(
        IReadOnlyDictionary<string, IReadOnlyList<PointCandidate>> candidatesBySymbol, int limit = PointSelector.DefaultLimit)
    {
        var ordered = candidatesBySymbol
            .SelectMany(kv => kv.Value.Where(c => double.IsFinite(c.Score)).Select(c => (Symbol: kv.Key, Candidate: c)))
            .OrderByDescending(x => x.Candidate.Score)
            .ThenBy(x => x.Candidate.EntryMinute)
            .ThenBy(x => x.Candidate.HoldMinutes)
            .ThenBy(x => x.Candidate.Direction)
            .ThenBy(x => x.Symbol, StringComparer.Ordinal);

        var occupied = new bool[CandidateGrid.MinutesPerDay];
        var selected = new List<SymbolPoint>(limit);
        foreach (var (symbol, c) in ordered)
        {
            if (!PointSelector.TryOccupy(occupied, c.EntryMinute, c.HoldMinutes))
            {
                continue;
            }

            selected.Add(new SymbolPoint(symbol, new EntryPoint(selected.Count + 1, c.Direction, c.EntryMinute, c.HoldMinutes, c.Score)));
            if (selected.Count >= limit)
            {
                break;
            }
        }

        return selected;
    }
}
