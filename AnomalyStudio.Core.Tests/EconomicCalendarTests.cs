using System.Text.Json;
using AnomalyStudio.Core.EconomicCalendar;
using AnomalyStudio.Core.Storage;

namespace AnomalyStudio.Core.Tests;

[TestClass]
public sealed class EconomicCalendarTests
{
    // GMO クリック証券の月別 JSON（2026-09 / 2026-10）から抜き出した行
    private const string Sample = """
        {"events":[
        {"dId":"D20260904","utcTime":"Fri, 04 Sep 2026 21:30:00 +0900","showTime":1,"dayOff":0,"calendarTitle":"非農業部門雇用者数","lasttime":"+73千人","expect":"+75千人","result":"+162千人","importantLevel":"4","countries":[{"name":"アメリカ","abbr":"us"}]},
        {"dId":"D20260901","utcTime":"Tue, 01 Sep 2026 08:50:00 +0900","showTime":1,"dayOff":0,"calendarTitle":"法人季報設備投資[前年比]","lasttime":"0.0％","expect":"-0.3％","result":"+1.6％","importantLevel":"3","countries":[{"name":"日本","abbr":"jp"}]},
        {"dId":"D20260905","utcTime":"Sat, 05 Sep 2026 00:00:00 +0900","showTime":1,"dayOff":1,"calendarTitle":"","lasttime":"","expect":"","result":"","importantLevel":"","countries":[]},
        {"dId":"D20260917","utcTime":"Thu, 17 Sep 2026 03:30:00 +0900","showTime":1,"dayOff":0,"calendarTitle":"ウォーシュ・FRB議長　定例会見","lasttime":"","expect":"","result":"","importantLevel":"","countries":[{"name":"アメリカ","abbr":"us"}]},
        {"dId":"D20261001","utcTime":"Thu, 01 Oct 2026 00:00:00 +0900","showTime":0,"dayOff":1,"calendarTitle":"国慶節","lasttime":"","expect":"","result":"","importantLevel":"","countries":[{"name":"中国","abbr":"cn"},{"name":"香港","abbr":"hk"}]},
        {"dId":"D20261030","utcTime":"Fri, 30 Oct 2026 23:59:00 +0900","showTime":0,"dayOff":0,"calendarTitle":"経済・物価情勢の展望[展望レポート]","lasttime":"","expect":"","result":"","importantLevel":"3","countries":[{"name":"日本","abbr":"jp"}]},
        {"dId":"D20260904","utcTime":"Fri, 04 Sep 2026 17:30:00 +0900","showTime":1,"dayOff":0,"calendarTitle":"Ifo","lasttime":"","expect":"","result":"","importantLevel":"0","countries":[{"name":"ドイツ","abbr":"ge"},{"name":"イギリス","abbr":"uk"}]}
        ]}
        """;

    private string _root = string.Empty;

    [TestInitialize]
    public void Setup() => _root = Path.Combine(Path.GetTempPath(), "anomaly-tests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void Parse_ReadsEvents_InJstOrder_AndSkipsBlankRows()
    {
        var events = ClickSecCalendarParser.Parse(Sample);

        Assert.HasCount(6, events);
        CollectionAssert.AreEqual(
            new[] { "法人季報設備投資[前年比]", "Ifo", "非農業部門雇用者数", "ウォーシュ・FRB議長　定例会見", "国慶節", "経済・物価情勢の展望[展望レポート]" },
            events.Select(e => e.Name).ToArray());

        var nfp = events.Single(e => e.Name == "非農業部門雇用者数");
        Assert.AreEqual(new DateTime(2026, 9, 4, 21, 30, 0), nfp.Time);
        Assert.AreEqual(5, nfp.Importance);
        Assert.AreEqual("US", nfp.Country);
        Assert.AreEqual("+73千人", nfp.Previous);
        Assert.AreEqual("+75千人", nfp.Forecast);
        Assert.AreEqual("+162千人", nfp.Actual);
        Assert.IsFalse(nfp.TimeUndecided);
        Assert.IsFalse(nfp.IsHoliday);
    }

    [TestMethod]
    public void Parse_MapsImportance_HolidayUndecidedAndCountries()
    {
        var events = ClickSecCalendarParser.Parse(Sample).ToDictionary(e => e.Name);

        // 重要度なし（要人発言）は 0、"0" は ★1
        Assert.AreEqual(0, events["ウォーシュ・FRB議長　定例会見"].Importance);
        Assert.AreEqual(1, events["Ifo"].Importance);
        CollectionAssert.AreEqual(new[] { "DE", "GB" }, events["Ifo"].Countries.ToArray());

        var holiday = events["国慶節"];
        Assert.IsTrue(holiday.IsHoliday);
        Assert.IsTrue(holiday.TimeUndecided);
        CollectionAssert.AreEqual(new[] { "CN", "HK" }, holiday.Countries.ToArray());

        var outlook = events["経済・物価情勢の展望[展望レポート]"];
        Assert.IsTrue(outlook.TimeUndecided);
        Assert.IsFalse(outlook.IsHoliday);
        Assert.AreEqual(string.Empty, outlook.Actual);
    }

    [TestMethod]
    public void Parse_ConvertsOtherOffsetsToJst()
    {
        var events = ClickSecCalendarParser.Parse(
            """{"events":[{"utcTime":"Fri, 04 Sep 2026 12:30:00 +0000","showTime":1,"dayOff":0,"calendarTitle":"x","importantLevel":"2","countries":[{"abbr":"us"}]}]}""");

        Assert.AreEqual(new DateTime(2026, 9, 4, 21, 30, 0), events[0].Time);
        Assert.AreEqual(3, events[0].Importance);
    }

    [TestMethod]
    public void Parse_EmptyBody_IsNoData_AndOtherShapesAreErrors()
    {
        Assert.IsEmpty(ClickSecCalendarParser.Parse(string.Empty));
        Assert.IsEmpty(ClickSecCalendarParser.Parse("""{"events":[]}"""));
        Assert.Throws<JsonException>(() => ClickSecCalendarParser.Parse("""{"items":[]}"""));
        Assert.Throws<JsonException>(() => ClickSecCalendarParser.Parse("<html></html>"));
    }

    [TestMethod]
    [DataRow("+1.6％", 1.6)]
    [DataRow("-0.3％", -0.3)]
    [DataRow("-272億AUD", -272.0)]
    [DataRow("1,234.5千人", 1234.5)]
    [DataRow("52.0", 52.0)]
    public void TryParseValue_ReadsLeadingNumber(string text, double expected)
    {
        Assert.IsTrue(ClickSecCalendarParser.TryParseValue(text, out var value));
        Assert.AreEqual(expected, value, 1e-9);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("—")]
    [DataRow("据え置き")]
    public void TryParseValue_RejectsNonNumbers(string text) =>
        Assert.IsFalse(ClickSecCalendarParser.TryParseValue(text, out _));

    [TestMethod]
    public void Countries_MapSiteCodes()
    {
        Assert.AreEqual("GB", CalendarCountries.FromSiteCode("uk"));
        Assert.AreEqual("DE", CalendarCountries.FromSiteCode("ge"));
        Assert.AreEqual("TR", CalendarCountries.FromSiteCode("tu"));
        Assert.AreEqual("EU", CalendarCountries.FromSiteCode("eu"));
        Assert.AreEqual("SG", CalendarCountries.FromSiteCode("sg"));
        Assert.HasCount(19, CalendarCountries.All);
    }

    [TestMethod]
    public async Task Store_SavesFetchedMonth_AndLoadsIt()
    {
        var paths = new AppPaths(_root);
        var source = new FakeSource { ["2026-09"] = Sample };
        var store = new EconomicCalendarStore(source, paths);

        Assert.IsNull(store.Load(2026, 9));
        var fetched = await store.FetchAsync(2026, 9);

        Assert.HasCount(6, fetched);
        Assert.IsTrue(File.Exists(paths.CalendarFile(2026, 9)));
        Assert.HasCount(6, store.Load(2026, 9)!);
        CollectionAssert.AreEqual(new[] { "2026-09" }, source.Requests);
    }

    [TestMethod]
    public async Task Store_DoesNotSaveMonthsOutsideTheProvidedRange()
    {
        var paths = new AppPaths(_root);
        var store = new EconomicCalendarStore(new FakeSource { ["2010-06"] = string.Empty }, paths);

        Assert.IsEmpty(await store.FetchAsync(2010, 6));
        Assert.IsFalse(File.Exists(paths.CalendarFile(2010, 6)));
    }

    [TestMethod]
    public async Task Store_IsComplete_OnlyWhenFetchedAfterTheMonthSettled()
    {
        var paths = new AppPaths(_root);
        var store = new EconomicCalendarStore(new FakeSource { ["2026-09"] = Sample }, paths);
        Assert.IsFalse(store.IsComplete(2026, 9));

        await store.FetchAsync(2026, 9);
        var file = paths.CalendarFile(2026, 9);

        // 翌月 1 日から 2 日後（10/3 0:00 JST = 10/2 15:00 UTC）以降に取得したものを確定とみなす
        File.SetLastWriteTimeUtc(file, new DateTime(2026, 10, 2, 14, 59, 0, DateTimeKind.Utc));
        Assert.IsFalse(store.IsComplete(2026, 9));
        File.SetLastWriteTimeUtc(file, new DateTime(2026, 10, 2, 15, 0, 0, DateTimeKind.Utc));
        Assert.IsTrue(store.IsComplete(2026, 9));
    }

    [TestMethod]
    public void Store_BrokenCache_IsTreatedAsMissing()
    {
        var paths = new AppPaths(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.CalendarFile(2026, 9))!);
        File.WriteAllText(paths.CalendarFile(2026, 9), "{broken");

        Assert.IsNull(new EconomicCalendarStore(new FakeSource(), paths).Load(2026, 9));
    }

    [TestMethod]
    public void MonthUrl_FollowsTheSiteLayout() =>
        Assert.AreEqual("https://www.click-sec.com/corp/guide/fxneo/cal/202609.json", ClickSecCalendarSource.MonthUrl(2026, 9));

    private sealed class FakeSource : Dictionary<string, string>, IEconomicCalendarSource
    {
        public List<string> Requests { get; } = [];

        public Task<string> FetchMonthAsync(int year, int month, CancellationToken cancellationToken = default)
        {
            var key = $"{year:0000}-{month:00}";
            Requests.Add(key);
            return Task.FromResult(TryGetValue(key, out var json) ? json : string.Empty);
        }
    }
}
