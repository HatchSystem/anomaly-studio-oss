using AnomalyStudio.Core.Modes;
using AnomalyStudio.ViewModels;

namespace AnomalyStudio.Core.Tests;

[TestClass]
public sealed class ModeDetailSaveTests
{
    [TestMethod]
    [DataRow("name", false)]
    [DataRow("description", false)]
    [DataRow("sql", false)]
    [DataRow("stop", false)]
    [DataRow("cancel", false)]
    [DataRow("reload", false)]
    [DataRow("name", true)]
    [DataRow("description", true)]
    [DataRow("sql", true)]
    [DataRow("stop", true)]
    [DataRow("cancel", true)]
    [DataRow("reload", true)]
    public async Task InvalidatedSave_DoesNotWriteOrNavigateAfterPendingValidation(string action, bool duringExtraction)
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;
        var saving = vm.SaveCommand.ExecuteAsync(null);
        Assert.AreEqual(fixture.Expected.Sql, await fixture.Workspace.ValidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        if (duringExtraction)
        {
            fixture.Workspace.CompleteValidation();
            Assert.AreEqual(fixture.Expected.Sql, await fixture.Workspace.ExtractionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        Assert.IsFalse(saving.IsCompleted, "遅延応答の到着前に入力変更・離脱を挟む");
        switch (action)
        {
            case "name": vm.Name = "変更後の名前"; break;
            case "description": vm.Description = "変更後の説明"; break;
            case "sql": vm.Sql = "SELECT 2"; break;
            case "stop": vm.Stop(); break;
            case "cancel": vm.CancelCommand.Execute(null); break;
            case "reload": vm.LoadNew(); break;
            default: Assert.Fail("未定義の操作"); break;
        }

        var navigationsAfterAction = fixture.Navigation.GoBackCount;
        Assert.AreEqual(action == "cancel" ? 1 : 0, navigationsAfterAction, "キャンセル操作自体の戻る遷移は1回だけ行う");
        fixture.Workspace.CompleteValidation();
        fixture.Workspace.CompleteExtraction();
        await saving.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, fixture.Modes.Saved.Count);
        Assert.AreEqual(navigationsAfterAction, fixture.Navigation.GoBackCount, "保存待ちの完了は追加遷移を起こさない");
        Assert.AreEqual(fixture.Original.Name, fixture.Settings.EntriesMode, "破棄した保存で選択中モード名も変更しない");
        if (!duringExtraction)
        {
            Assert.IsFalse(fixture.Workspace.ExtractionStarted.Task.IsCompleted, "無効になった検証から追加の抽出を始めない");
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SuccessfulSave_PersistsExactlyTheValidatedSnapshot(bool hasAnalysis)
    {
        using var fixture = new Fixture();
        fixture.Workspace.HasAnalysis = hasAnalysis;

        var saving = fixture.ViewModel.SaveCommand.ExecuteAsync(null);
        Assert.AreEqual(fixture.Expected.Sql, await fixture.Workspace.ValidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(0, fixture.Modes.Saved.Count);
        Assert.AreEqual(0, fixture.Navigation.GoBackCount);
        fixture.Workspace.CompleteValidation();
        if (hasAnalysis)
        {
            Assert.AreEqual(fixture.Expected.Sql, await fixture.Workspace.ExtractionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(0, fixture.Modes.Saved.Count, "実データでの抽出検証も完了するまでは保存しない");
            fixture.Workspace.CompleteExtraction();
        }

        await saving.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, fixture.Modes.Saved.Count);
        Assert.AreEqual(fixture.Expected, fixture.Modes.Saved[0], "ID・有効状態も含め、待機前に確定した編集内容だけを保存する");
        Assert.AreEqual(fixture.Expected.Name, fixture.Settings.EntriesMode);
        Assert.AreEqual(1, fixture.Navigation.GoBackCount);
        Assert.AreEqual(string.Empty, fixture.ViewModel.ErrorMessage);
        Assert.AreEqual(hasAnalysis, fixture.Workspace.ExtractionStarted.Task.IsCompleted);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _http = new(new RejectNetworkHandler());

        public Fixture()
        {
            Modes.Items.Add(Original);
            Settings.EntriesMode = Original.Name;
            // 保存テストはAIへ接続しない。実サービスを使うが、資格情報も通信も実環境へ渡さない。
            var ai = new AiSqlAssistant(Settings, _http, _http,
                () => null,
                _ => throw new InvalidOperationException("保存検証で資格情報を書き込んではいけません"),
                () => throw new InvalidOperationException("保存検証で資格情報を削除してはいけません"));
            ViewModel = new ModeDetailViewModel(Navigation, Modes, Workspace, ai, Settings);
            ViewModel.Load(Original);
            ViewModel.Name = Expected.Name;
            ViewModel.Description = Expected.Description;
            ViewModel.Sql = Expected.Sql;
        }

        public ModeDefinition Original { get; } = new()
        {
            Id = "custom-save-test", Name = "保存前", Description = "保存前の説明", Sql = "SELECT 0", Enabled = true,
        };

        public ModeDefinition Expected => Original with { Name = "検証した名前", Description = "検証した説明", Sql = "SELECT 1" };

        public NavigationService Navigation { get; } = new();
        public ModeRepository Modes { get; } = new();
        public AnomalyWorkspace Workspace { get; } = new() { HasAnalysis = true };
        public AppSettings Settings { get; } = new();
        public ModeDetailViewModel ViewModel { get; }

        public void Dispose() => _http.Dispose();
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("モード保存の単体テストでHTTP要求は許可しません");
    }
}
