namespace AnomalyStudio.Core.Analysis;

public enum TradeDirection
{
    Long,
    Short,
}

/// <summary>
/// 候補母集団: 1440 Entry 時刻 × 保有 3〜30 分 × Long/Short = 80,640 候補。
/// 候補は (保有, 方向, Entry) の順に並ぶ連番で表す。
/// </summary>
public static class CandidateGrid
{
    public const int MinutesPerDay = 1440;
    public const int HoldMin = 3;
    public const int HoldMax = 30;
    public const int HoldCount = HoldMax - HoldMin + 1;
    public const int Count = HoldCount * 2 * MinutesPerDay;

    public static int Index(int holdMinutes, TradeDirection direction, int entryMinute) =>
        (((holdMinutes - HoldMin) * 2) + (int)direction) * MinutesPerDay + entryMinute;

    public static int Hold(int index) => (index / (2 * MinutesPerDay)) + HoldMin;

    public static TradeDirection Direction(int index) => (TradeDirection)(index / MinutesPerDay % 2);

    public static int Entry(int index) => index % MinutesPerDay;

    public static int Close(int index) => (Entry(index) + Hold(index)) % MinutesPerDay;

    public static bool CrossesDay(int index) => Entry(index) + Hold(index) >= MinutesPerDay;

    public static string DirectionName(TradeDirection direction) => direction == TradeDirection.Long ? "Long" : "Short";

    public static TradeDirection ParseDirection(string value) =>
        value.Equals("Short", StringComparison.OrdinalIgnoreCase) ? TradeDirection.Short : TradeDirection.Long;
}
