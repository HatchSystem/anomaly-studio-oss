using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AnomalyStudio.Core;
using AnomalyStudio.Core.Analysis;
using AnomalyStudio.Core.Backtesting;
using AnomalyStudio.Core.Diagnostics;
using AnomalyStudio.Core.Modes;
using AnomalyStudio.Core.Storage;
using AnomalyStudio.Core.Symbols;
using AnomalyStudio.Core.Trading;

// 分析エンジンと保存層の所要時間を、保存済みの実データ（Parquet）で計測する。
// 使い方: dotnet run --project AnomalyStudio.Bench/AnomalyStudio.Bench.csproj -c Release -- [銘柄ID] [--root 保存場所] [--data データフォルダー] [--days 基準日数] [--repeat 回数] [--full-walkforward]
//   既定: USDJPY、保存場所は ANOMALYSTUDIO_ROOT か %LOCALAPPDATA%\AnomalyStudio、基準日数（窓スライドの回数）30、繰り返し 5
// 分析結果の DB（anomaly.duckdb）には触れないので、アプリの起動中でも実行できる。
// --full-walkforward は市場履歴を一時領域へコピーし、365 日の日次 WF を保存・評価まで含めて 1 回計測する。

var options = new Dictionary<string, string>(StringComparer.Ordinal);
string? specifiedSymbol = null;
var fullWalkForward = false;
for (var i = 0; i < args.Length; i++)
{
    var argument = args[i];
    if (argument == "--full-walkforward")
    {
        fullWalkForward = true;
    }
    else if (argument is "--root" or "--data" or "--days" or "--repeat")
    {
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)
            || !options.TryAdd(argument, args[++i]))
        {
            Console.Error.WriteLine($"{argument} は値を伴って 1 回だけ指定してください。");
            return 2;
        }
    }
    else if (argument.StartsWith("--", StringComparison.Ordinal) || specifiedSymbol is not null)
    {
        Console.Error.WriteLine($"不明な引数です: {argument}");
        return 2;
    }
    else
    {
        specifiedSymbol = argument;
    }
}

var symbolId = specifiedSymbol ?? "USDJPY";
if (symbolId.Length == 0 || symbolId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
{
    Console.Error.WriteLine("銘柄 ID は英数字・ハイフン・アンダースコアで指定してください。");
    return 2;
}

if (!TryPositiveOption("--days", 30, out var slideDays) || !TryPositiveOption("--repeat", 5, out var repeat))
{
    Console.Error.WriteLine("--days と --repeat は 1 以上の整数で指定してください。");
    return 2;
}

if (fullWalkForward && (options.ContainsKey("--days") || options.ContainsKey("--repeat")))
{
    Console.Error.WriteLine("--full-walkforward は 365 日・1 回固定です。--days・--repeat は通常の部分計測だけで指定できます。");
    return 2;
}

var root = options.GetValueOrDefault("--root") ?? Environment.GetEnvironmentVariable("ANOMALYSTUDIO_ROOT") ?? AppPaths.DefaultRoot;
var paths = new AppPaths(root, options.GetValueOrDefault("--data"));
var profile = SymbolCatalog.All.FirstOrDefault(s => s.Id == symbolId);
var parameters = new EngineParameters { FallbackSpread = profile?.FallbackSpread ?? 0.02 };
var rows = new List<(string Stage, double Ms, string Detail)>();

Console.WriteLine($"AnomalyStudio 計測: {symbolId}（{paths.DataDirectory}）, CPU {Environment.ProcessorCount} 論理コア, {(Debugger.IsAttached ? "デバッガー接続" : "")}{(IsRelease() ? "Release" : "Debug ビルド（Release で計測すること）")}");
Console.WriteLine();

if (fullWalkForward)
{
    return await MeasureFullWalkForwardAsync(paths, symbolId);
}

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
var selections = ModeDefinition.Defaults.Select(m => (m.Name, Selection: ModeDefinition.ParseBuiltIn(m.Sql)!)).ToArray();
foreach (var (name, selection) in selections)
{
    Measure($"select/{name}", () => PointSelector.SelectBuiltIn(table, selection), p => $"{p.Count} 件");
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
    foreach (var (_, selection) in selections)
    {
        PointSelector.SelectBuiltIn(reuse, selection);
    }

    selectTotal += sw.Elapsed;
}

rows.Add(($"walkforward/{slideDays} 日（分析 + {selections.Length} モード抽出、保存・評価なし）", slideWatch.Elapsed.TotalMilliseconds,
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

bool TryPositiveOption(string name, int fallback, out int value)
{
    value = fallback;
    return !options.TryGetValue(name, out var text)
        || (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0);
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

static async Task<int> MeasureFullWalkForwardAsync(AppPaths source, string symbolId)
{
    DirectoryInfo? temporary = null;
    try
    {
        // 元の保存先は読み取りだけにする。JsonFileStore.Load は壊れた JSON を退避するため使わない。
        SymbolProfile? symbol = null;
        if (File.Exists(source.SymbolsFile))
        {
            using var stream = File.OpenRead(source.SymbolsFile);
            var saved = JsonSerializer.Deserialize(stream, CoreJsonContext.Default.ListSymbolProfile);
            symbol = saved?.FirstOrDefault(s => s.Id == symbolId);
        }

        symbol ??= SymbolCatalog.All.FirstOrDefault(s => s.Id == symbolId);
        if (symbol is null)
        {
            Console.Error.WriteLine($"銘柄 {symbolId} の定義がありません。symbols.json または同梱カタログの銘柄を指定してください。");
            return 1;
        }

        var sourceDirectory = source.MarketDirectory(symbolId);
        var months = new List<(DateOnly Month, string File)>();
        if (Directory.Exists(sourceDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*.parquet", SearchOption.TopDirectoryOnly))
            {
                if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file) + "-01", "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
                {
                    months.Add((month, file));
                }
            }
        }

        if (months.Count == 0)
        {
            Console.Error.WriteLine($"{symbolId} の月別 Parquet がありません（{sourceDirectory}）。KGI は未検証です。");
            return 1;
        }

        // 評価 365 日 + 最初の分析窓 365 日。UTC/JST と月境界の余裕を含めた月だけをコピーする。
        var firstMonth = months.Max(m => m.Month).AddMonths(-25);
        temporary = Directory.CreateTempSubdirectory("AnomalyStudio.Bench-");
        var isolatedPaths = new AppPaths(temporary.FullName);
        Directory.CreateDirectory(isolatedPaths.MarketDirectory(symbolId));
        var copied = 0;
        foreach (var (_, file) in months.Where(m => m.Month >= firstMonth))
        {
            File.Copy(file, Path.Combine(isolatedPaths.MarketDirectory(symbolId), Path.GetFileName(file)));
            copied++;
        }

        await using var database = new AnalysisDatabase(isolatedPaths);
        var (count, first, last) = await database.GetMarketCoverageAsync(symbolId);
        if (count == 0 || first is null || last is null)
        {
            Console.Error.WriteLine("コピーした市場履歴に足がありません。KGI は未検証です。");
            return 1;
        }

        // 最新足の日は途中までの可能性があるため、前日までの 365 日を評価する。
        var to = DateOnly.FromDateTime(Jst.FromUtc(last.Value)).AddDays(-1);
        var from = to.AddDays(-364);
        var requiredFrom = Jst.StartOfDayUtc(from.AddDays(new EngineParameters().MaxPeriod * -1));
        Console.WriteLine($"市場履歴: {Jst.FromUtc(first.Value):yyyy/MM/dd HH:mm} ～ {Jst.FromUtc(last.Value):yyyy/MM/dd HH:mm} JST、{count:N0} 本、{copied} ファイルを一時領域へコピー");
        Console.WriteLine($"評価期間: {from:yyyy/MM/dd} ～ {to:yyyy/MM/dd}（365 日、毎日サイクル、初回）");
        if (first.Value > requiredFrom.AddDays(4))
        {
            Console.Error.WriteLine($"市場履歴が不足しています。最初の分析には {Jst.FromUtc(requiredFrom):yyyy/MM/dd} 頃からの足が必要です（先頭の休場猶予 4 日）。KGI は未検証です。");
            return 1;
        }

        var modes = ModeDefinition.Defaults;
        var evaluationMode = modes[0];
        Console.WriteLine($"評価モード: {evaluationMode.Name}。抽出・保存: {string.Join("、", modes.Select(m => m.Name))}");
        Console.WriteLine("RunAsync の読込・行列作成・分析・抽出・ポイント保存・取引評価を計測します（一時領域の準備・破棄は除外）。");
        var timings = new Dictionary<string, (TimeSpan Elapsed, int Count)>();
        var timingLock = new object();
        void RecordTiming(TimingEvent timing)
        {
            if (timing.Category is not ("storage" or "walkforward"))
            {
                return;
            }

            lock (timingLock)
            {
                var key = timing.Category + "/" + timing.Name;
                var previous = timings.GetValueOrDefault(key);
                timings[key] = (previous.Elapsed + timing.Elapsed, previous.Count + 1);
            }
        }

        Timing.Recorded += RecordTiming;
        var watch = Stopwatch.StartNew();
        WalkForwardResult result;
        try
        {
            result = await new WalkForwardBacktester(database).RunAsync(
                symbol, evaluationMode, modes, from, to, BacktestCycle.Daily, last.Value,
                new BenchmarkProgress(watch));
        }
        finally
        {
            watch.Stop();
            Timing.Recorded -= RecordTiming;
            lock (timingLock)
            {
                Console.WriteLine("| 計測区間 | 秒 | 通知回数 |");
                Console.WriteLine("|---|---:|---:|");
                foreach (var (stage, timing) in timings.OrderBy(t => t.Key, StringComparer.Ordinal))
                {
                    Console.WriteLine($"| {stage} | {timing.Elapsed.TotalSeconds:0.000} | {timing.Count} |");
                }

                // matrix は compute から除かれている。その他の入れ子の通知は重複加算しない。
                var accounted = new[] { "storage/load-quotes", "walkforward/matrix", "walkforward/compute", "walkforward/select", "walkforward/save", "walkforward/evaluate" }
                    .Sum(stage => timings.GetValueOrDefault(stage).Elapsed.TotalSeconds);
                Console.WriteLine($"| 全体との差分（その他の待機・準備） | {watch.Elapsed.TotalSeconds - accounted:0.000} | |");
                Console.WriteLine($"| RunAsync 全体 | {watch.Elapsed.TotalSeconds:0.000} | 1 |");
            }
        }

        var marketTrades = result.Trades.Where(t => t.Trade.IsInMarketHours).ToArray();
        var settled = marketTrades.Count(t => t.Trade.Status == TradeStatus.Settled);
        var noData = marketTrades.Count(t => t.Trade.Status == TradeStatus.NoData);
        var pending = marketTrades.Count(t => t.Trade.Status == TradeStatus.Pending);
        Console.WriteLine($"WF 全体: {watch.Elapsed.TotalSeconds:0.000} 秒。新規分析 {result.ComputedAnalyses}/365 基準日、分析データ不足 {result.UnavailableReportDates.Count} 基準日");
        Console.WriteLine($"市場営業時間内の評価: 確定 {settled:N0} 件、足の欠損 {noData:N0} 件、未確定 {pending:N0} 件");
        if (result.ComputedAnalyses != 365 || result.UnavailableReportDates.Count != 0 || settled == 0 || pending != 0)
        {
            if (result.UnavailableReportDates.Count != 0)
            {
                Console.Error.WriteLine($"分析できない基準日（先頭 10 件）: {string.Join("、", result.UnavailableReportDates.Take(10).Select(d => d.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)))}");
            }

            Console.Error.WriteLine("1 年分の分析・評価に必要な市場履歴が不足しています。KGI は未検証です。");
            return 1;
        }

        Console.WriteLine(!IsRelease() || Debugger.IsAttached
            ? "KGI: 判定対象外（デバッガーを接続せず Release で計測してください）。"
            : watch.Elapsed.TotalSeconds <= 60 ? "KGI: 60 秒以内を達成。" : "KGI: 60 秒を超過。");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
        or DuckDB.NET.Data.DuckDBException or ArgumentException)
    {
        Console.Error.WriteLine($"完全 WF の計測に失敗しました。KGI は未検証です: {ex.Message}");
        return 1;
    }
    finally
    {
        // CreateTempSubdirectory で作成した、この計測専用のディレクトリだけを削除する。
        if (temporary is not null)
        {
            temporary.Delete(recursive: true);
        }
    }
}

static bool IsRelease()
{
#if DEBUG
    return false;
#else
    return true;
#endif
}

/// <summary>同期で進捗を出し、終了後に非同期の通知が残らないようにする。</summary>
sealed class BenchmarkProgress(Stopwatch watch) : IProgress<WalkForwardProgress>
{
    private int _lastStep = -1;

    public void Report(WalkForwardProgress value)
    {
        var step = Math.Clamp((int)(value.Fraction * 10), 0, 10);
        if (step <= _lastStep)
        {
            return;
        }

        _lastStep = step;
        Console.WriteLine($"進捗 {step * 10}%（経過 {watch.Elapsed.TotalSeconds:0.0} 秒）: {value.Message}");
    }
}
