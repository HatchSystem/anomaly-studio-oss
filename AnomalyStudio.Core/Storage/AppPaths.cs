namespace AnomalyStudio.Core.Storage;

/// <summary>
/// 保存場所。既定は %LOCALAPPDATA%\AnomalyStudio（ユーザー単位で書き込め、ローミング同期の対象外）。
/// 設定・モード・銘柄の JSON はルート直下、市場データと分析 DB は DataDirectory 配下に置く。
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string root, string? dataDirectory = null)
    {
        Root = root;
        DataDirectory = string.IsNullOrWhiteSpace(dataDirectory) ? Path.Combine(root, "data") : dataDirectory;
    }

    public static string DefaultRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnomalyStudio");

    public string Root { get; }

    public string DataDirectory { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string ModesFile => Path.Combine(Root, "modes.json");

    public string SymbolsFile => Path.Combine(Root, "symbols.json");

    public string DatabaseFile => Path.Combine(DataDirectory, "anomaly.duckdb");

    /// <summary>日別のログ（anomalystudio-yyyyMMdd.log）。ルート直下なので、データの保存先を変えても同じ場所。</summary>
    public string LogsDirectory => Path.Combine(Root, "logs");

    public string MarketDirectory(string symbolId) => Path.Combine(DataDirectory, "market", symbolId);

    /// <summary>1 分足の月別 Parquet（例 data\market\USDJPY\2026-09.parquet）。</summary>
    public string MarketFile(string symbolId, int year, int month) =>
        Path.Combine(MarketDirectory(symbolId), $"{year:0000}-{month:00}.parquet");

    /// <summary>経済指標カレンダーの月別 JSON（例 data\calendar\2026-09.json）。取得元の応答をそのまま保存する。</summary>
    public string CalendarFile(int year, int month) =>
        Path.Combine(DataDirectory, "calendar", $"{year:0000}-{month:00}.json");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DataDirectory);
    }
}
