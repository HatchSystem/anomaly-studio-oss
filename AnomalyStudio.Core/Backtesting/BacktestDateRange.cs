using System.Diagnostics.CodeAnalysis;

namespace AnomalyStudio.Core.Backtesting;

/// <summary>利用者が指定した検証期間。未選択や逆転した日付を別の期間に置き換えない。</summary>
public sealed record BacktestDateRange
{
    private BacktestDateRange(DateOnly from, DateOnly to) => (From, To) = (from, to);

    public DateOnly From { get; }

    public DateOnly To { get; }

    public static bool TryCreate(DateOnly? from, DateOnly? to,
        [NotNullWhen(true)] out BacktestDateRange? range, out string error)
    {
        range = null;
        if (from is null || to is null)
        {
            error = "検証期間の開始日と終了日を選んでください";
            return false;
        }

        if (from > to)
        {
            error = "検証期間の開始日は終了日以前にしてください";
            return false;
        }

        range = new BacktestDateRange(from.Value, to.Value);
        error = string.Empty;
        return true;
    }
}
