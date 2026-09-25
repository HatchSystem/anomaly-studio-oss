using System.Globalization;
using System.Net;

namespace AnomalyStudio.Core.EconomicCalendar;

/// <summary>経済指標カレンダーの取得元。</summary>
public interface IEconomicCalendarSource
{
    /// <summary>指定月（JST）の生データを返す。提供範囲外の月は空文字。</summary>
    Task<string> FetchMonthAsync(int year, int month, CancellationToken cancellationToken = default);
}

/// <summary>
/// GMO クリック証券の経済指標カレンダー（https://www.click-sec.com/corp/guide/fxneo/cal/）が
/// 画面表示に読み込む月別 JSON（{yyyyMM}.json）を取得する。読み取りのみ。
/// </summary>
public sealed class ClickSecCalendarSource(HttpClient http) : IEconomicCalendarSource
{
    public const string PageUrl = "https://www.click-sec.com/corp/guide/fxneo/cal/";

    /// <summary>取得元が提供する最初の月（config.json の minDate）。</summary>
    public static readonly DateOnly FirstMonth = new(2010, 7, 1);

    /// <summary>
    /// 通常のブラウザーでカレンダー画面を開いたときと同じヘッダーで要求する
    /// （画面のスクリプトが jQuery.getJSON で読み込むときの要求に合わせる）。
    /// </summary>
    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var headers = client.DefaultRequestHeaders;
        headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36");
        headers.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
        headers.AcceptLanguage.ParseAdd("ja,en-US;q=0.9,en;q=0.8");
        headers.Referrer = new Uri(PageUrl);
        headers.Add("X-Requested-With", "XMLHttpRequest");
        headers.Add("Sec-Fetch-Dest", "empty");
        headers.Add("Sec-Fetch-Mode", "cors");
        headers.Add("Sec-Fetch-Site", "same-origin");
        return client;
    }

    public static string MonthUrl(int year, int month) =>
        string.Create(CultureInfo.InvariantCulture, $"{PageUrl}{year:0000}{month:00}.json");

    public async Task<string> FetchMonthAsync(int year, int month, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(MonthUrl(year, month), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
