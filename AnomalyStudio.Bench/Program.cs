using System.Diagnostics;
using System.Globalization;
using AnomalyStudio.Core;
using AnomalyStudio.Core.Analysis;
using AnomalyStudio.Core.Diagnostics;
using AnomalyStudio.Core.Storage;
using AnomalyStudio.Core.Symbols;

// 分析エンジンと保存層の所要時間を、保存済みの実データ（Parquet）で計測する。
// 使い方: dotnet run --project AnomalyStudio.Bench/AnomalyStudio.Bench.csproj -c Release -- [銘柄ID] [--root 保存場所] [--data データフォルダー] [--days 基準日数] [--repeat 回数]
//   既定: USDJPY、保存場所は ANOMALYSTUDIO_ROOT か %LOCALAPPDATA%\AnomalyStudio、基準日数（窓スライドの回数）30、繰り返し 5
// 分析結果の DB（anomaly.duckdb）には触れないので、アプリの起動中でも実行できる。

var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
var symbolId = positional.Count > 0 ? positional[0] : "USDJPY";
var root = Option("--root") ?? Environment.GetEnvironmentVariable("ANOMALYSTUDIO_ROOT") ?? AppPaths.DefaultRoot;
var slideDays = int.TryParse(Option("--days"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : 30;
var repeat = int.TryParse(Option("--repeat"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 5;
var paths = new AppPaths(root, Option("--data"));
var profile = SymbolCatalog.All.FirstOrDefault(s => s.Id == symbolId);
var parameters = new EngineParameters { FallbackSpread = profile?.FallbackSpread ?? 0.02 };
var rows = new List<(string Stage, double Ms, string Detail)>();

Console.WriteLine($"AnomalyStudio 計測: {symbolId}（{paths.DataDirectory}）, CPU {Environment.ProcessorCount} 論理コア, {(Debugger.IsAttached ? "デバッガー接続" : "")}{(IsRelease() ? "Release" : "Debug ビルド（Release で計測すること）")}");
Console.WriteLine();

await using var store = new MarketDataStore(paths);
var (count, _, last) = await store.GetCoverageAsync(symbolId);
if (count == 0 || last is null)
{
    Console.Error.WriteLine($"{symbolId} の 1 分足がありません（{paths.MarketDirectory(symbolId)}）。アプリでデータを取り込んでから実行してください。");
    return 1;
}

// 基準日は保存済みの最新の足の翌日（365 日分がそろっている）
var reportDate = DateOnly.FromDateTime(Jst.FromUtc(last.Value)).AddDays(1);
var maxPeriod = parameters.MaxPeriod;

// 1. 1 年分の読込
var (fromUtc, toUtc) = PriceMatrix.RequiredRange(reportDate, maxPeriod);
var quotes = await MeasureAsync("storage/load-quotes（1 年）", async () => await store.LoadOpenQuotesAsync(symbolId, fromUtc, toUtc), q => $"{q.Count:N0} 本");
if (quotes.Count == 0)
{
    Console.Error.WriteLine("基準日の 365 日分の足がありません。");
    return 1;
}

// 2. 行列の作成
var matrix = Measure("analysis/matrix", () => PriceMatrix.Build(reportDate, maxPeriod, quotes), m => $"{m.BarCount:N0} 本, {m.Days} 日");

// 3. 分析（初回はウォームアップ、以降の最小と平均）
var table = AnomalyEngine.Compute(matrix, parameters);
var times = new List<double>();
for (var i = 0; i < repeat; i++)
{
    var sw = Stopwatch.StartNew();
    table = AnomalyEngine.Compute(matrix, parameters, reuse: table);
    times.Add(sw.Elapsed.TotalMilliseconds);
}

rows.Add(("analysis/compute（172,800 候補 × 4 期間）", times.Min(), $"最小 {times.Min():0} / 平均 {times.Average():0} ms（{repeat} 回、入れ物を使い回し）"));

// 3b. うちスコア・品質除外・順位付けの部分
var scoreTimes = new List<double>();
for (var i = 0; i < repeat; i++)
{
    var sw = Stopwatch.StartNew();
    AnomalyEngine.Score(table, parameters);
    scoreTimes.Add(sw.Elapsed.TotalMilliseconds);
}

rows.Add(("analysis/compute のうち score（正規化・順位）", scoreTimes.Min(), $"最小 {scoreTimes.Min():0} / 平均 {scoreTimes.Average():0} ms"));

// 4. 既定モードのポイント抽出（DuckDB を通さない経路）
foreach (var column in new[] { "score_win_rate", "score_profit_eff", "win_rate_lcb_avg" })
{
    Measure($"select/{column}", () => PointSelector.SelectBuiltIn(table, column), p => $"{p.Count} 件");
}

// 5. ウォークフォワードの窓スライド（毎日サイクル slideDays 日分。価格は 1 回読み、窓をずらして分析・抽出）
var firstReport = reportDate.AddDays(-slideDays + 1);
var (slideFrom, _) = PriceMatrix.RequiredRange(firstReport, maxPeriod);
var slideQuotes = await MeasureAsync($"storage/load-quotes（{slideDays} 日分の窓）", async () => await store.LoadOpenQuotesAsync(symbolId, slideFrom, toUtc), q => $"{q.Count:N0} 本");
var firstDay = firstReport.AddDays(-maxPeriod);
var full = Measure("walkforward/matrix", () => PriceMatrix.BuildRange(firstDay, reportDate.DayNumber - firstDay.DayNumber + 1, slideQuotes), m => $"{m.Days} 日");
var computeTotal = TimeSpan.Zero;
var selectTotal = TimeSpan.Zero;
CandidateTable? reuse = null;
var slideWatch = Stopwatch.StartNew();
for (var day = firstReport; day <= reportDate; day = day.AddDays(1))
{
    var sw = Stopwatch.StartNew();
    reuse = AnomalyEngine.Compute(full.Window(day, maxPeriod, excludeReportDate: true), parameters, reuse: reuse);
    computeTotal += sw.Elapsed;
    sw.Restart();
    foreach (var column in new[] { "score_win_rate", "score_profit_eff", "win_rate_lcb_avg" })
    {
        PointSelector.SelectBuiltIn(reuse, column);
    }

    selectTotal += sw.Elapsed;
}

rows.Add(($"walkforward/{slideDays} 日（分析 + 3 モード抽出）", slideWatch.Elapsed.TotalMilliseconds,
    $"分析 平均 {computeTotal.TotalMilliseconds / slideDays:0} ms, 抽出 平均 {selectTotal.TotalMilliseconds / slideDays:0} ms, 365 日換算 {slideWatch.Elapsed.TotalSeconds / slideDays * 365:0} 秒"));

// 6. メモリ
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
rows.Add(("memory/managed", 0, $"{GC.GetTotalMemory(false) / 1024.0 / 1024.0:0} MB（GC 後）"));
rows.Add(("memory/working-set", 0, $"{Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024.0:0} MB"));

Console.WriteLine("| 段階 | ms | 補足 |");
Console.WriteLine("|---|---:|---|");
foreach (var (stage, ms, detail) in rows)
{
    Console.WriteLine($"| {stage} | {(ms > 0 ? ms.ToString("0", CultureInfo.InvariantCulture) : "")} | {detail} |");
}

return 0;

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

T Measure<T>(string stage, Func<T> action, Func<T, string> detail)
{
    var sw = Stopwatch.StartNew();
    var result = action();
    rows.Add((stage, sw.Elapsed.TotalMilliseconds, detail(result)));
    return result;
}

async Task<T> MeasureAsync<T>(string stage, Func<Task<T>> action, Func<T, string> detail)
{
    var sw = Stopwatch.StartNew();
    var result = await action();
    rows.Add((stage, sw.Elapsed.TotalMilliseconds, detail(result)));
    return result;
}

static bool IsRelease()
{
#if DEBUG
    return false;
#else
    return true;
#endif
}
