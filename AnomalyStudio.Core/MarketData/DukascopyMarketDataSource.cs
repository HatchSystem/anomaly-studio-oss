using System.Globalization;
using System.Net;
using System.Text.Json;

namespace AnomalyStudio.Core.MarketData;

/// <summary>
/// Dukascopy のチャート用 JSON API から 1 分足を取得する。
/// 1 回の要求で最大 30,000 本（約 1 か月）を返すため、カーソルを進めながら繰り返し取得する。
/// 応答の行は [時刻(ms), open, high, low, close, volume]。
/// </summary>
public sealed class DukascopyMarketDataSource(HttpClient http) : IMarketDataSource
{
    private const string Endpoint = "https://freeserv.dukascopy.com/2.0/index.php";
    private const int PageLimit = 30_000;
    private const int MaxRetries = 5;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>Dukascopy はブラウザー以外の要求を拒否するため、チャート画面と同じヘッダーを付ける。</summary>
    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Referrer = new Uri("https://freeserv.dukascopy.com/2.0/?path=chart/index");
        return client;
    }

    public async Task<IReadOnlyList<SideBar>> FetchAsync(
        string instrument,
        OfferSide side,
        DateTime fromUtc,
        DateTime toUtc,
        IProgress<DateTime>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var bars = new List<SideBar>();
        var cursor = new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var end = new DateTimeOffset(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        while (cursor < end)
        {
            var page = await FetchPageAsync(instrument, side, cursor, cancellationToken);

            // 2 ページ目以降の先頭は取得済みの足が重なるので、カーソルより前は捨てる
            var added = 0;
            long last = cursor;
            foreach (var row in page)
            {
                if (row.Time < cursor)
                {
                    continue;
                }

                if (row.Time >= end)
                {
                    break;
                }

                bars.Add(row.Bar);
                last = row.Time;
                added++;
            }

            if (added == 0)
            {
                break;
            }

            cursor = last + 1;
            progress?.Report(DateTimeOffset.FromUnixTimeMilliseconds(last).UtcDateTime);
        }

        return bars;
    }

    private async Task<List<(long Time, SideBar Bar)>> FetchPageAsync(
        string instrument, OfferSide side, long cursor, CancellationToken cancellationToken)
    {
        var callback = "_callbacks____" + Guid.NewGuid().ToString("N")[..9];
        var query = string.Join('&',
            "path=chart%2Fjson3",
            "splits=true",
            "stocks=true",
            "time_direction=N",
            $"jsonp={callback}",
            $"last_update={cursor.ToString(CultureInfo.InvariantCulture)}",
            $"offer_side={(side == OfferSide.Bid ? "B" : "A")}",
            $"instrument={Uri.EscapeDataString(instrument)}",
            "interval=1MIN",
            $"limit={PageLimit}");

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync($"{Endpoint}?{query}", cancellationToken);
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRetries)
                {
                    await Task.Delay(RetryDelay * attempt, cancellationToken);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                return Parse(text, callback);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       && !cancellationToken.IsCancellationRequested && attempt < MaxRetries)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }

    /// <summary>JSONP（callback([...]);）を外して行を読み取る。</summary>
    internal static List<(long Time, SideBar Bar)> Parse(string text, string callback)
    {
        var start = text.IndexOf('(');
        var end = text.LastIndexOf(')');
        var json = text.StartsWith(callback, StringComparison.Ordinal) && start >= 0 && end > start
            ? text[(start + 1)..end]
            : text;

        using var document = JsonDocument.Parse(json);
        var rows = new List<(long, SideBar)>(document.RootElement.GetArrayLength());
        foreach (var row in document.RootElement.EnumerateArray())
        {
            var time = row[0].GetInt64();
            rows.Add((time, new SideBar(
                DateTimeOffset.FromUnixTimeMilliseconds(time).UtcDateTime,
                row[1].GetDouble(),
                row[2].GetDouble(),
                row[3].GetDouble(),
                row[4].GetDouble(),
                row[5].GetDouble())));
        }

        return rows;
    }
}
