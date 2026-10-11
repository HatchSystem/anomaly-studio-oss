using System.Net;
using System.ComponentModel;
using AnomalyStudio.Services;

namespace AnomalyStudio.Core.Tests;

[TestClass]
public sealed class AiConnectionTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SuccessfulConnection_ReflectsWhetherTheCredentialWasSaved(bool saved)
    {
        using var fixture = new Fixture(saveSucceeds: saved);
        var connection = fixture.TestAsync("test-key");
        fixture.Handler.Succeed(0);

        Assert.IsNotNull(await connection);
        Assert.IsTrue(fixture.Assistant.IsConnected);
        Assert.AreEqual(saved, fixture.Assistant.HasStoredKey);
        CollectionAssert.AreEqual(new[] { "test-key" }, fixture.SavedKeys);
    }

    [TestMethod]
    [DataRow("not-json")]
    [DataRow("[]")]
    [DataRow("42")]
    [DataRow("null")]
    [DataRow("{\"data\":[null]}")]
    [DataRow("{\"data\":[{\"id\":42}]}")]
    [DataRow("{\"data\":[{\"id\":{}}]}")]
    public void MalformedModelList_DoesNotLeakJsonTypeExceptions(string json)
    {
        Assert.IsNull(AiSqlAssistant.ReadModelIds(json));
    }

    [TestMethod]
    public void ModelList_PreservesCompatibilityWithMissingDataAndValidIds()
    {
        Assert.IsNull(AiSqlAssistant.ReadModelIds("{}"), "モデル一覧を返さない互換 API は従来どおりモデル照合を省略する");
        var ids = AiSqlAssistant.ReadModelIds("{\"data\":[{\"id\":\"test-model\"},{\"id\":\"other-model\"}]}");
        Assert.IsNotNull(ids);
        CollectionAssert.AreEquivalent(new[] { "test-model", "other-model" }, ids.ToArray());
    }

    [TestMethod]
    [DataRow("url")]
    [DataRow("model")]
    [DataRow("key")]
    public async Task EditingSettingsDuringTest_DoesNotConnectOrSaveTheOldKey(string setting)
    {
        using var fixture = new Fixture();
        var pending = fixture.TestAsync("test-key-before-edit");
        switch (setting)
        {
            case "url": fixture.Settings.AiBaseUrl = "https://changed.invalid/v1"; break;
            case "model": fixture.Settings.AiModel = "changed-model"; break;
            // API キーは SettingsViewModel の変更通知が Invalidate を呼ぶ。
            default: fixture.Assistant.Invalidate(); break;
        }

        fixture.Handler.Succeed(0);

        Assert.IsNull(await pending);
        Assert.IsFalse(fixture.Assistant.IsConnected);
        Assert.AreEqual(0, fixture.SavedKeys.Count);
        Assert.AreEqual(string.Empty, fixture.Assistant.ApiKey);
    }

    [TestMethod]
    public async Task DeletingKeyDuringTest_DoesNotRestoreItWhenTheResponseArrives()
    {
        using var fixture = new Fixture("stored-test-key");
        var pending = fixture.Assistant.EnsureConnectedAsync();

        fixture.Assistant.DeleteStoredKey();
        fixture.Handler.Succeed(0);

        Assert.IsNull(await pending);
        Assert.AreEqual(1, fixture.Deletes);
        Assert.AreEqual(0, fixture.SavedKeys.Count);
        Assert.AreEqual(string.Empty, fixture.Assistant.ApiKey);
        Assert.IsFalse(fixture.Assistant.HasStoredKey);
        Assert.IsFalse(fixture.Assistant.IsConnected);
    }

    [TestMethod]
    public async Task FailedCredentialDeletion_PreservesStoredFlagAndDisconnects()
    {
        using var fixture = new Fixture("stored-test-key", deleteFails: true);
        var connection = fixture.Assistant.EnsureConnectedAsync();
        fixture.Handler.Succeed(0);
        await connection;
        Assert.IsTrue(fixture.Assistant.IsConnected);

        Assert.IsFalse(fixture.Assistant.DeleteStoredKey());

        Assert.IsTrue(fixture.Assistant.HasStoredKey, "削除を再試行できるよう、永続キーが残ることを表示する");
        Assert.IsFalse(fixture.Assistant.IsConnected);
        Assert.AreEqual(string.Empty, fixture.Assistant.ApiKey);
        Assert.AreEqual(1, fixture.Deletes);
    }

    [TestMethod]
    public async Task FailedDeletion_DoesNotReconnectFromAPendingResponse()
    {
        using var fixture = new Fixture("stored-test-key", deleteFails: true);
        var pending = fixture.Assistant.EnsureConnectedAsync();

        Assert.IsFalse(fixture.Assistant.DeleteStoredKey());
        fixture.Handler.Succeed(0);

        Assert.IsNull(await pending);
        Assert.IsTrue(fixture.Assistant.HasStoredKey);
        Assert.IsFalse(fixture.Assistant.IsConnected);
        Assert.AreEqual(string.Empty, fixture.Assistant.ApiKey);
        Assert.AreEqual(0, fixture.SavedKeys.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OlderResponseArrivingLast_DoesNotOverrideTheLatestTest(bool olderFails)
    {
        using var fixture = new Fixture();
        var oldTest = fixture.TestAsync("old-test-key");
        var latestTest = fixture.TestAsync("new-test-key");

        fixture.Handler.Succeed(1);
        Assert.IsNotNull(await latestTest);
        fixture.Handler.Respond(0, olderFails ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, "{\"data\":[{\"id\":\"test-model\"}]}");

        Assert.IsNull(await oldTest);
        Assert.IsTrue(fixture.Assistant.IsConnected);
        Assert.AreEqual("new-test-key", fixture.Assistant.ApiKey);
        CollectionAssert.AreEqual(new[] { "new-test-key" }, fixture.SavedKeys);
    }

    [TestMethod]
    public async Task SettingsChangedDuringGeneration_RejectsTheOldResponse()
    {
        using var fixture = new Fixture();
        var connection = fixture.TestAsync("test-key");
        fixture.Handler.Succeed(0);
        await connection;

        var generation = fixture.Assistant.GenerateSqlAsync("テスト用の条件");
        fixture.Settings.AiModel = "changed-model";
        fixture.Handler.Respond(1, HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"SELECT 1\"}}]}");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await generation);
        Assert.IsFalse(fixture.Assistant.IsConnected);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _client;

        public Fixture(string? storedKey = null, bool saveSucceeds = true, bool deleteFails = false)
        {
            _client = new HttpClient(Handler);
            // 実資格情報ストアには一切触れない。すべてのHTTP要求も保留ハンドラー内だけで完結する。
            Assistant = new AiSqlAssistant(Settings, _client, _client, () => storedKey,
                key => { SavedKeys.Add(key); return saveSucceeds; },
                () =>
                {
                    Deletes++;
                    if (deleteFails)
                    {
                        throw new Win32Exception(5);
                    }
                });
        }

        public AppSettings Settings { get; } = new();
        public DeferredHandler Handler { get; } = new();
        public AiSqlAssistant Assistant { get; }
        public List<string> SavedKeys { get; } = [];
        public int Deletes { get; private set; }

        public Task<string?> TestAsync(string key) => Assistant.TestConnectionAsync(Settings.AiBaseUrl, Settings.AiModel, key);

        public void Dispose() => _client.Dispose();
    }

    private sealed class DeferredHandler : HttpMessageHandler
    {
        private readonly List<TaskCompletionSource<HttpResponseMessage>> _responses = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _responses.Add(response);
            return response.Task;
        }

        public void Succeed(int index) => Respond(index, HttpStatusCode.OK, "{\"data\":[{\"id\":\"test-model\"}]}");

        public void Respond(int index, HttpStatusCode status, string content) =>
            _responses[index].SetResult(new HttpResponseMessage(status) { Content = new StringContent(content) });
    }
}
