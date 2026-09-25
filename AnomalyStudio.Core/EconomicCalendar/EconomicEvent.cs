namespace AnomalyStudio.Core.EconomicCalendar;

/// <summary>
/// 経済指標・イベント 1 件。時刻は JST。値（前回・予想・結果）は単位付きの表示文字列で、未発表や該当なしは空文字。
/// </summary>
public sealed record EconomicEvent(
    DateTime Time,
    IReadOnlyList<string> Countries,
    string Name,
    int Importance,
    string Previous,
    string Forecast,
    string Actual)
{
    /// <summary>発表時刻が決まっていない（<see cref="Time"/> はその日の目安）。</summary>
    public bool TimeUndecided { get; init; }

    /// <summary>祝日などの休場日。</summary>
    public bool IsHoliday { get; init; }

    /// <summary>代表の国コード（先頭）。</summary>
    public string Country => Countries.Count > 0 ? Countries[0] : string.Empty;
}

/// <summary>カレンダーの国・地域。<see cref="Code"/> は画面と設定で使うコード、<see cref="SiteCode"/> は取得元の略称。</summary>
public sealed record CalendarCountry(string Code, string Name, string SiteCode);

public static class CalendarCountries
{
    /// <summary>取得元（GMO クリック証券）が扱う国・地域。表示順。</summary>
    public static IReadOnlyList<CalendarCountry> All { get; } =
    [
        new("US", "米国", "us"),
        new("JP", "日本", "jp"),
        new("EU", "ユーロ圏", "eu"),
        new("GB", "英国", "uk"),
        new("DE", "ドイツ", "ge"),
        new("FR", "フランス", "fr"),
        new("AU", "豪州", "au"),
        new("NZ", "ニュージーランド", "nz"),
        new("CA", "カナダ", "ca"),
        new("CH", "スイス", "ch"),
        new("CN", "中国", "cn"),
        new("HK", "香港", "hk"),
        new("ZA", "南アフリカ", "za"),
        new("IN", "インド", "in"),
        new("TR", "トルコ", "tu"),
        new("MX", "メキシコ", "mx"),
        new("CZ", "チェコ", "cz"),
        new("PL", "ポーランド", "pl"),
        new("HU", "ハンガリー", "hu"),
    ];

    private static readonly Dictionary<string, string> BySiteCode =
        All.ToDictionary(c => c.SiteCode, c => c.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>取得元の略称を国コードへ変換する。未知の略称は大文字にしてそのまま使う。</summary>
    public static string FromSiteCode(string siteCode) =>
        BySiteCode.TryGetValue(siteCode, out var code) ? code : siteCode.ToUpperInvariant();
}
