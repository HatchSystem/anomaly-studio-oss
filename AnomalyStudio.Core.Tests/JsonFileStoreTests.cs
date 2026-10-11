using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnomalyStudio.Core.Tests;

[TestClass]
public sealed class JsonFileStoreTests
{
    [TestMethod]
    [DataRow(unchecked((int)0x80070005))]
    [DataRow(unchecked((int)0x80070020))]
    [DataRow(unchecked((int)0x80070021))]
    public void TemporaryWindowsReplacementFailure_RetriesTheCompletedTempFileThenReplaces(int hresult)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows のファイル置換エラーに対する限定的な再試行");
        }

        using var fixture = new Fixture();
        var attempts = 0;
        var delays = new List<int>();
        string? firstTemp = null;
        JsonFileStore.Save(fixture.Path, new[] { "新しい設定" }, JsonFileStoreTestContext.Default.StringArray,
            (temp, path) =>
            {
                firstTemp ??= temp;
                Assert.AreEqual(firstTemp, temp);
                CollectionAssert.AreEqual(new[] { "以前の設定" }, fixture.Read());
                CollectionAssert.AreEqual(new[] { "新しい設定" }, fixture.Read(temp));
                if (++attempts < 5)
                {
                    throw ReplacementError(hresult);
                }
                File.Move(temp, path, overwrite: true);
            }, delays.Add);

        Assert.AreEqual(5, attempts);
        CollectionAssert.AreEqual(new[] { 25, 50, 100, 200 }, delays);
        CollectionAssert.AreEqual(new[] { "新しい設定" }, fixture.Read());
        Assert.AreEqual(0, Directory.GetFiles(fixture.Directory, "*.tmp").Length);
    }

    [TestMethod]
    public void PersistentAccessDenied_ThrowsAndPreservesPreviousJsonWithoutTemporaryFiles()
    {
        using var fixture = new Fixture();
        var attempts = 0;
        var failure = new UnauthorizedAccessException("テスト用の恒久アクセス拒否");
        var delays = new List<int>();

        var actual = Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            JsonFileStore.Save(fixture.Path, new[] { "保存してはいけない設定" }, JsonFileStoreTestContext.Default.StringArray,
                (_, _) => { attempts++; throw failure; }, delays.Add));

        Assert.AreSame(failure, actual);
        Assert.AreEqual(OperatingSystem.IsWindows() ? 5 : 1, attempts);
        Assert.AreEqual(OperatingSystem.IsWindows() ? 375 : 0, delays.Sum());
        CollectionAssert.AreEqual(new[] { "以前の設定" }, fixture.Read());
        Assert.AreEqual(0, Directory.GetFiles(fixture.Directory, "*.tmp").Length);
    }

    [TestMethod]
    public void OtherIoError_DoesNotRetryOrReplaceThePreviousJson()
    {
        using var fixture = new Fixture();
        var failure = new IOException("テスト用の容量不足", unchecked((int)0x80070070));
        var delays = new List<int>();
        var attempts = 0;

        var actual = Assert.ThrowsExactly<IOException>(() =>
            JsonFileStore.Save(fixture.Path, new[] { "新しい設定" }, JsonFileStoreTestContext.Default.StringArray,
                (_, _) => { attempts++; throw failure; }, delays.Add));

        Assert.AreSame(failure, actual);
        Assert.AreEqual(1, attempts);
        Assert.AreEqual(0, delays.Count);
        CollectionAssert.AreEqual(new[] { "以前の設定" }, fixture.Read());
        Assert.AreEqual(0, Directory.GetFiles(fixture.Directory, "*.tmp").Length);
    }

    [TestMethod]
    public void OverlappingSaves_UseIndependentTemporaryFilesAndKeepEachCompletedValue()
    {
        using var fixture = new Fixture();
        JsonFileStore.Save(fixture.Path, new[] { "最後に完了する設定" }, JsonFileStoreTestContext.Default.StringArray,
            (outerTemp, path) =>
            {
                JsonFileStore.Save(path, new[] { "先に完了する設定" }, JsonFileStoreTestContext.Default.StringArray,
                    (innerTemp, destination) =>
                    {
                        Assert.AreNotEqual(outerTemp, innerTemp);
                        CollectionAssert.AreEqual(new[] { "最後に完了する設定" }, fixture.Read(outerTemp));
                        File.Move(innerTemp, destination, overwrite: true);
                    }, _ => Assert.Fail("再試行は不要"));
                CollectionAssert.AreEqual(new[] { "先に完了する設定" }, fixture.Read());
                File.Move(outerTemp, path, overwrite: true);
            }, _ => Assert.Fail("再試行は不要"));

        CollectionAssert.AreEqual(new[] { "最後に完了する設定" }, fixture.Read());
        Assert.AreEqual(0, Directory.GetFiles(fixture.Directory, "*.tmp").Length);
    }

    private static Exception ReplacementError(int hresult) => hresult == unchecked((int)0x80070005)
        ? new UnauthorizedAccessException("テスト用の一時アクセス拒否")
        : new IOException("テスト用の共有・ロック競合", hresult);

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AnomalyStudio-json-test-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Directory, "settings.json");

        public Fixture() => JsonFileStore.Save(Path, new[] { "以前の設定" }, JsonFileStoreTestContext.Default.StringArray);

        public string[] Read(string? path = null) => JsonSerializer.Deserialize(File.ReadAllText(path ?? Path), JsonFileStoreTestContext.Default.StringArray)!;

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}

[JsonSerializable(typeof(string[]))]
internal sealed partial class JsonFileStoreTestContext : JsonSerializerContext;
