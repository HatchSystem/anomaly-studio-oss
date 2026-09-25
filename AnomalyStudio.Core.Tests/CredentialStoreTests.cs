using AnomalyStudio.Core.Storage;

namespace AnomalyStudio.Core.Tests;

[TestClass]
public sealed class CredentialStoreTests
{
    // 本物の資格情報マネージャーを使うので、テストごとに別の名前にして必ず消す
    private readonly string _target = "AnomalyStudio:Test:" + Guid.NewGuid().ToString("N");

    [TestCleanup]
    public void Cleanup() => CredentialStore.Delete(_target);

    [TestMethod]
    public void WriteReadReplaceDelete()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("資格情報マネージャーは Windows だけ");
        }

        Assert.IsNull(CredentialStore.Read(_target));

        CredentialStore.Write(_target, "AnomalyStudio", "sk-test-日本語-1", "テスト");
        Assert.AreEqual("sk-test-日本語-1", CredentialStore.Read(_target));

        CredentialStore.Write(_target, "AnomalyStudio", "sk-test-2", "テスト");
        Assert.AreEqual("sk-test-2", CredentialStore.Read(_target));

        Assert.IsTrue(CredentialStore.Delete(_target));
        Assert.IsNull(CredentialStore.Read(_target));
        Assert.IsFalse(CredentialStore.Delete(_target));
    }
}
