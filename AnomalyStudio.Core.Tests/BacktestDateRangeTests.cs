using AnomalyStudio.Core.Backtesting;

namespace AnomalyStudio.Core.Tests;

[TestClass]
public sealed class BacktestDateRangeTests
{
    [TestMethod]
    public void ReversedDates_AreRejectedWithoutSubstitutingAnotherPeriod()
    {
        var from = new DateOnly(2026, 10, 11);
        var to = new DateOnly(2026, 9, 11);

        Assert.IsFalse(BacktestDateRange.TryCreate(from, to, out var range, out var error));
        Assert.IsNull(range);
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public void MissingDates_AreRejected(bool hasFrom, bool hasTo)
    {
        var day = new DateOnly(2026, 10, 11);

        Assert.IsFalse(BacktestDateRange.TryCreate(hasFrom ? day : null, hasTo ? day : null, out var range, out var error));
        Assert.IsNull(range);
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(365)]
    public void ValidDates_PreserveTheExactSelectedPeriod(int days)
    {
        var from = new DateOnly(2025, 10, 11);
        var to = from.AddDays(days);

        Assert.IsTrue(BacktestDateRange.TryCreate(from, to, out var range, out var error));
        Assert.IsNotNull(range);
        Assert.AreEqual(from, range.From);
        Assert.AreEqual(to, range.To);
        Assert.AreEqual(string.Empty, error);
    }
}
