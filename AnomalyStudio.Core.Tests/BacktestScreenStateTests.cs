using System.Text.Json;
using System.Text.Json.Serialization;
using AnomalyStudio.Core.Backtesting;
using AnomalyStudio.Core.Storage;

namespace AnomalyStudio.Core.Tests;

[TestClass]
public sealed class BacktestScreenStateTests
{
    [TestMethod]
    public void FirstUse_DefaultIsOneYear_ButSavedDatesDoNotAdvance()
    {
        var today = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.FromHours(9));
        var state = BacktestScreenState.CreateDefault(["AAA", "BBB"], "average", today);
        Assert.AreEqual(today.AddYears(-1), state.StartDate);
        Assert.AreEqual(today, state.EndDate);
        CollectionAssert.AreEqual(new[] { "AAA" }, state.SymbolIds.ToArray());
        Assert.AreEqual(BacktestCycle.Weekly, state.Cycle);
        var restored = state.Restore(["AAA", "BBB"], ["average"], "average");
        Assert.AreEqual(state.StartDate, restored.StartDate);
        Assert.AreEqual(state.EndDate, restored.EndDate);
    }

    [TestMethod]
    public void Restore_MissingChoicesFallBack_ButExplicitEmptySelectionRemainsEmpty()
    {
        var state = new BacktestScreenState(["removed", "BBB"], "removed-mode", BacktestCycle.Monthly, null, null);
        var remaining = state.Restore(["AAA", "BBB"], ["disabled", "enabled"], "enabled");
        CollectionAssert.AreEqual(new[] { "BBB" }, remaining.SymbolIds.ToArray());
        Assert.AreEqual("enabled", remaining.Mode);
        Assert.AreEqual(BacktestCycle.Monthly, remaining.Cycle);

        var missing = (state with { SymbolIds = ["removed"] }).Restore(["AAA", "BBB"], ["enabled"], "enabled");
        CollectionAssert.AreEqual(new[] { "AAA" }, missing.SymbolIds.ToArray());
        var empty = (state with { SymbolIds = [] }).Restore(["AAA", "BBB"], ["enabled"], "enabled");
        Assert.AreEqual(0, empty.SymbolIds.Count);
        Assert.AreEqual(0, state.Restore([], [], "enabled").SymbolIds.Count);
        var incomplete = JsonSerializer.Deserialize("{}", ScreenStatesJsonForTest.Default.BacktestScreenState)!;
        CollectionAssert.AreEqual(new[] { "AAA" }, incomplete.Restore(["AAA"], ["enabled"], "enabled").SymbolIds.ToArray());
    }

    [TestMethod]
    public void Restore_DoesNotReplaceNullOrReversedDates()
    {
        var later = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.FromHours(9));
        var earlier = later.AddDays(-2);
        var state = new BacktestScreenState(["AAA"], "average", BacktestCycle.Daily, later, earlier);
        var restored = state.Restore(["AAA"], ["average"], "average");
        Assert.AreEqual(later, restored.StartDate);
        Assert.AreEqual(earlier, restored.EndDate);
        var incomplete = (state with { StartDate = null }).Restore(["AAA"], ["average"], "average");
        Assert.IsNull(incomplete.StartDate);
        Assert.AreEqual(earlier, incomplete.EndDate);
        Assert.AreEqual(BacktestCycle.Weekly,
            (state with { Cycle = (BacktestCycle)999 }).Restore(["AAA"], ["average"], "average").Cycle);
    }

    [TestMethod]
    public void JsonFileRoundTrip_KeepsTwoScreensIndependentAndPreservesNullDates()
    {
        var root = Path.Combine(Path.GetTempPath(), "anomaly-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "screen-conditions.json");
        var end = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.FromHours(9));
        var data = new ScreenStatesForTest
        {
            WalkForwardConditions = new(["BBB", "AAA"], "short", BacktestCycle.Daily, end.AddDays(-30), end),
            OptimizationConditions = new([], "long", BacktestCycle.Monthly, null, null),
        };
        try
        {
            JsonFileStore.Save(path, data, ScreenStatesJsonForTest.Default.ScreenStatesForTest);
            var restored = JsonFileStore.Load(path, ScreenStatesJsonForTest.Default.ScreenStatesForTest, () => new ScreenStatesForTest());
            Assert.IsNotNull(restored.WalkForwardConditions);
            Assert.IsNotNull(restored.OptimizationConditions);
            CollectionAssert.AreEqual(new[] { "BBB", "AAA" }, restored.WalkForwardConditions.SymbolIds.ToArray());
            Assert.AreEqual("short", restored.WalkForwardConditions.Mode);
            Assert.AreEqual(BacktestCycle.Daily, restored.WalkForwardConditions.Cycle);
            Assert.AreEqual(end.AddDays(-30), restored.WalkForwardConditions.StartDate);
            Assert.AreEqual(end, restored.WalkForwardConditions.EndDate);
            Assert.AreEqual(0, restored.OptimizationConditions.SymbolIds.Count);
            Assert.AreEqual("long", restored.OptimizationConditions.Mode);
            Assert.AreEqual(BacktestCycle.Monthly, restored.OptimizationConditions.Cycle);
            Assert.IsNull(restored.OptimizationConditions.StartDate);
            Assert.IsNull(restored.OptimizationConditions.EndDate);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}

internal sealed class ScreenStatesForTest
{
    public BacktestScreenState? WalkForwardConditions { get; set; }
    public BacktestScreenState? OptimizationConditions { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ScreenStatesForTest))]
internal sealed partial class ScreenStatesJsonForTest : JsonSerializerContext;
