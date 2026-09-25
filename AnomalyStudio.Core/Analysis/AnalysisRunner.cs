using System.Diagnostics;
using System.Text.Json;
using AnomalyStudio.Core.Diagnostics;
using AnomalyStudio.Core.Storage;
using AnomalyStudio.Core.Symbols;

namespace AnomalyStudio.Core.Analysis;

public sealed class InsufficientDataException(string message) : Exception(message);

/// <summary>保存済みの 1 分足から候補統計を計算し、run として保存する。</summary>
public sealed class AnalysisRunner(AnalysisDatabase database)
{
    /// <summary>保存しておく run の数（銘柄ごと）の既定。画面は最新の run しか使わないので、少なくてよい。</summary>
    public const int DefaultRunsToKeep = 3;

    /// <summary>保存しておく run の数（銘柄ごと）。</summary>
    public int RunsToKeep { get; set; } = DefaultRunsToKeep;

    /// <param name="reportDate">基準日（JST）。基準日−365 日 〜 基準日−1 日が評価対象。</param>
    public async Task<AnalysisRun> RunAsync(
        SymbolProfile symbol,
        DateOnly reportDate,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new EngineParameters { FallbackSpread = symbol.FallbackSpread };
        var (fromUtc, toUtc) = PriceMatrix.RequiredRange(reportDate, parameters.MaxPeriod);
        var stopwatch = Stopwatch.StartNew();
        using var total = Timing.Start("analysis", "total");

        IReadOnlyList<OpenQuote> quotes;
        using (var timing = Timing.Start("analysis", "load"))
        {
            quotes = await database.LoadOpenQuotesAsync(symbol.Id, fromUtc, toUtc);
            timing.Detail = $"{symbol.Id} {quotes.Count:N0} 本";
        }

        if (quotes.Count == 0)
        {
            throw new InsufficientDataException($"{symbol.Id} の市場データがありません。先にデータを取り込んでください。");
        }

        var lastEvaluatedDayEndUtc = Jst.StartOfDayUtc(reportDate);
        if (quotes[^1].TimeUtc < lastEvaluatedDayEndUtc.AddDays(-4))
        {
            throw new InsufficientDataException(
                $"{symbol.Id} の市場データが {Jst.FromUtc(quotes[^1].TimeUtc):yyyy/MM/dd} までしかありません。基準日の前日まで取り込んでください。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var table = await Task.Run(() =>
        {
            PriceMatrix matrix;
            using (Timing.Start("analysis", "matrix"))
            {
                matrix = PriceMatrix.Build(reportDate, parameters.MaxPeriod, quotes);
            }

            using (Timing.Start("analysis", "compute"))
            {
                return AnomalyEngine.Compute(matrix, parameters, progress);
            }
        }, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var paramsJson = JsonSerializer.Serialize(
            new RunParameters(
                parameters.Periods,
                parameters.ScorePeriods,
                parameters.MinSampleRatio,
                parameters.FallbackSpread,
                table.QualityThresholds.ToDictionary(
                    k => k.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    k => new QualityThreshold(k.Value.Max, k.Value.Threshold))),
            CoreJsonContext.Default.RunParameters);
        var run = await database.SaveRunAsync(symbol.Id, reportDate, table, stopwatch.ElapsedMilliseconds, quotes.Count, paramsJson);
        using (Timing.Start("analysis", "prune"))
        {
            await database.PruneRunsAsync(symbol.Id, RunsToKeep);
        }

        total.Detail = $"{symbol.Id} {reportDate:yyyy/MM/dd} 基準";
        return run;
    }
}
