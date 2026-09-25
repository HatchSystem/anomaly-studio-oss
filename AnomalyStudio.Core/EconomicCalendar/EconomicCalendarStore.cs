using System.Text;
using System.Text.Json;
using AnomalyStudio.Core.Storage;

namespace AnomalyStudio.Core.EconomicCalendar;

/// <summary>
/// 経済指標カレンダーを月単位で取得し、受け取った JSON をそのまま data\calendar\yyyy-MM.json に保存する。
/// 月末から <see cref="SettleDays"/> 日以上たってから取得した月は結果まで揃っているので、取り直す必要がない。
/// </summary>
public sealed class EconomicCalendarStore(IEconomicCalendarSource source, AppPaths paths)
{
    /// <summary>月末の指標の結果が反映されるまでの猶予。</summary>
    public const int SettleDays = 2;

    /// <summary>保存済みの月を読む。未保存・読めない場合は null。</summary>
    public IReadOnlyList<EconomicEvent>? Load(int year, int month)
    {
        var path = paths.CalendarFile(year, month);
        try
        {
            return File.Exists(path) ? ClickSecCalendarParser.Parse(File.ReadAllText(path, Encoding.UTF8)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>保存済みで、結果が揃った後に取得した月か。</summary>
    public bool IsComplete(int year, int month)
    {
        var path = paths.CalendarFile(year, month);
        if (!File.Exists(path))
        {
            return false;
        }

        var settledUtc = Jst.ToUtc(new DateTime(year, month, 1).AddMonths(1).AddDays(SettleDays));
        return File.GetLastWriteTimeUtc(path) >= settledUtc;
    }

    /// <summary>取得して保存する。提供範囲外（本文が空）の月は保存せず空を返す。</summary>
    public async Task<IReadOnlyList<EconomicEvent>> FetchAsync(int year, int month, CancellationToken cancellationToken = default)
    {
        var json = await source.FetchMonthAsync(year, month, cancellationToken);
        var events = ClickSecCalendarParser.Parse(json);
        if (events.Count > 0)
        {
            var path = paths.CalendarFile(year, month);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, json, new UTF8Encoding(false), cancellationToken);
            File.Move(temp, path, overwrite: true);
        }

        return events;
    }
}
