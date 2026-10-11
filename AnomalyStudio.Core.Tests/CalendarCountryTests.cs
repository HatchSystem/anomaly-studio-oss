using AnomalyStudio.ViewModels;

namespace AnomalyStudio.Core.Tests;

[TestClass]
public sealed class CalendarCountryTests
{
    [TestMethod]
    public void SelectAllThenClearThenSelectAll_NotifiesOnlyTheFinalStateOncePerCommand()
    {
        var settings = new AppSettings();
        var data = new CalendarData();
        var vm = new CalendarViewModel(data, settings, new ClockService());
        Assert.AreEqual(19, vm.Countries.Count);
        var queriesBefore = data.QueryCount;

        foreach (var selected in new[] { true, false, true })
        {
            var savesBefore = settings.SavedCalendarCountries.Count;
            vm.SelectAllCountriesCommand.Execute(null);

            Assert.AreEqual(savesBefore + 1, settings.SavedCalendarCountries.Count);
            Assert.IsTrue(vm.Countries.All(country => country.IsChecked == selected));
            var expected = selected ? data.GetCountries().Select(country => country.Code).Order().ToArray() : [];
            CollectionAssert.AreEqual(expected, settings.SavedCalendarCountries[^1]);
            Assert.AreEqual(selected ? "すべて" : "未選択", vm.CountryLabel);
            Assert.AreEqual(queriesBefore + 2, data.QueryCount, "次の指標と表示期間の取得も、一括操作の最終状態で各1回にまとめる");
            queriesBefore = data.QueryCount;
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailedSave_ShowsTheErrorUntilTheNextSuccessfulSaveAndKeepsSelectionConsistent(bool ioError)
    {
        Exception failure = ioError
            ? new IOException("テスト用の恒久保存エラー")
            : new UnauthorizedAccessException("テスト用の恒久保存エラー");
        var settings = new AppSettings { CalendarSaveFailure = failure };
        var data = new CalendarData();
        var vm = new CalendarViewModel(data, settings, new ClockService());
        vm.Start();
        try
        {
            var queriesBefore = data.QueryCount;
            vm.SelectAllCountriesCommand.Execute(null);

            Assert.AreEqual(1, settings.SavedCalendarCountries.Count);
            Assert.IsTrue(vm.Countries.All(country => country.IsChecked));
            Assert.AreEqual(19, settings.CalendarCountries.Count);
            Assert.AreEqual("すべて", vm.CountryLabel);
            Assert.AreEqual(queriesBefore + 2, data.QueryCount);
            Assert.AreEqual("国・地域の設定を保存できませんでした", vm.StatusText);
            var errorText = vm.StatusText;

            data.ChangeStatus("データ取得完了");
            Assert.AreEqual(errorText, vm.StatusText, "データ更新は未解消の保存エラーを隠さない");

            settings.CalendarSaveFailure = null;
            vm.SelectAllCountriesCommand.Execute(null);
            Assert.AreEqual(2, settings.SavedCalendarCountries.Count);
            Assert.IsTrue(vm.Countries.All(country => !country.IsChecked));
            Assert.AreEqual(0, settings.CalendarCountries.Count);
            Assert.AreEqual("未選択", vm.CountryLabel);
            Assert.AreEqual("データ取得完了", vm.StatusText);
        }
        finally
        {
            vm.Stop();
        }
    }

    [TestMethod]
    public void SingleCountryChange_StillNotifiesAndRefreshesImmediately()
    {
        var settings = new AppSettings();
        var vm = new CalendarViewModel(new CalendarData(), settings, new ClockService());

        vm.Countries.Single(country => country.Code == "US").IsChecked = false;

        Assert.AreEqual(1, settings.SavedCalendarCountries.Count);
        Assert.IsFalse(settings.CalendarCountries.Contains("US"));
        Assert.AreEqual("6か国", vm.CountryLabel);
    }

    private sealed class CalendarData : IEconomicCalendarService
    {
        public string StatusText { get; private set; } = "テスト";
        public event EventHandler? Changed;
        public int QueryCount { get; private set; }

        public IReadOnlyList<EconomicEvent> GetEvents(DateTime from, DateTime to)
        {
            QueryCount++;
            return [];
        }

        public Task RefreshAsync(DateTime from, DateTime to) => Task.CompletedTask;

        public void ChangeStatus(string status)
        {
            StatusText = status;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public IReadOnlyList<Country> GetCountries() =>
            CalendarCountries.All.Select(country => new Country(country.Code, country.Name)).ToArray();
    }
}
