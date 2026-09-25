using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnomalyStudio.Core.EconomicCalendar;

/// <summary>
/// GMO クリック証券の経済指標カレンダーの月別 JSON（{"events":[...]}）を読む。
/// utcTime は名前に反して "Tue, 01 Sep 2026 08:50:00 +0900" の形式（オフセット付き）、
/// importantLevel は "0"〜"4"（★1〜★5）で空文字は重要度なし（要人発言・祝日など）、
/// showTime=0 は時刻未定、dayOff=1 は休場日。
/// </summary>
public static partial class ClickSecCalendarParser
{
    /// <summary>本文が空（提供範囲外の月）なら空のリストを返す。形式が違えば <see cref="JsonException"/>。</summary>
    public static IReadOnlyList<EconomicEvent> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("events", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("経済指標カレンダーの JSON に events がありません。");
        }

        var events = new List<EconomicEvent>(items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
        {
            // 週末には名前も国もない空行が入る
            var name = Text(item, "calendarTitle").Trim();
            if (name.Length == 0)
            {
                continue;
            }

            var countries = new List<string>();
            if (item.TryGetProperty("countries", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var country in list.EnumerateArray())
                {
                    var code = CalendarCountries.FromSiteCode(Text(country, "abbr").Trim());
                    if (code.Length > 0 && !countries.Contains(code))
                    {
                        countries.Add(code);
                    }
                }
            }

            var level = Text(item, "importantLevel").Trim();
            events.Add(new EconomicEvent(
                ParseTime(Text(item, "utcTime")),
                countries,
                name,
                int.TryParse(level, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n + 1, 1, 5) : 0,
                Text(item, "lasttime").Trim(),
                Text(item, "expect").Trim(),
                Text(item, "result").Trim())
            {
                TimeUndecided = !Flag(item, "showTime"),
                IsHoliday = Flag(item, "dayOff"),
            });
        }

        // 元データは時刻順だが、念のため同時刻の並びを保ったまま並べ替える
        return [.. events.OrderBy(e => e.Time)];
    }

    /// <summary>"+1.5％"、"-272億AUD"、"1,234千人" などの先頭の数値を読む（予想と結果の比較用）。</summary>
    public static bool TryParseValue(string text, out double value)
    {
        value = 0;
        var match = NumberPattern().Match(text);
        return match.Success
            && double.TryParse(match.Value.Replace(",", string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static DateTime ParseTime(string text)
    {
        var time = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None);
        return Jst.FromUtc(time.UtcDateTime);
    }

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                _ => string.Empty,
            }
            : string.Empty;

    private static bool Flag(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.GetDouble() != 0,
            JsonValueKind.String => value.GetString() is { } s && s != "0" && s.Length > 0 && !s.Equals("false", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    [GeneratedRegex(@"^[+\-]?\d[\d,]*(\.\d+)?")]
    private static partial Regex NumberPattern();
}
