using AnomalyStudio.Core.Analysis;
using AnomalyStudio.Core.Modes;
using AnomalyStudio.Core.Storage;

namespace AnomalyStudio.Core.Trading;

/// <summary>モードの SQL で候補を並べ、時間帯の重ならないポイントを選ぶ。</summary>
public sealed class PointService(AnalysisDatabase database)
{
    public async Task<IReadOnlyList<EntryPoint>> GetPointsAsync(long runId, ModeDefinition mode, int limit = PointSelector.DefaultLimit)
    {
        var candidates = await database.QueryPointCandidatesAsync(runId, mode.Sql);
        return PointSelector.SelectNonOverlapping(candidates, limit);
    }
}
