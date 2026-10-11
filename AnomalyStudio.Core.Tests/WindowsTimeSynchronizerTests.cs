using System.ComponentModel;
using System.Diagnostics;
using AnomalyStudio.Core.Time;

namespace AnomalyStudio.Core.Tests;

[TestClass]
public sealed class WindowsTimeSynchronizerTests
{
    [TestMethod]
    public async Task Unsupported_DoesNotLaunchProcess()
    {
        var called = false;
        var service = new WindowsTimeSynchronizer(_ =>
        {
            called = true;
            return Task.FromResult(0);
        }, () => false, @"C:\Windows");

        Assert.IsFalse(service.IsSupported);
        Assert.AreEqual(new SystemTimeSyncResult(SystemTimeSyncOutcome.Unsupported),
            await service.SynchronizeAsync());
        Assert.IsFalse(called);
    }

    [TestMethod]
    public async Task ExplicitSynchronization_UsesFixedElevatedHiddenCommand()
    {
        ProcessStartInfo? captured = null;
        var service = new WindowsTimeSynchronizer(info =>
        {
            captured = info;
            return Task.FromResult(0);
        }, () => true, @"C:\Windows");

        // 構築・対応判定は同期を開始しない。実 OS へはフェイクランナーから接続しない。
        Assert.IsTrue(service.IsSupported);
        Assert.IsNull(captured);
        Assert.AreEqual(new SystemTimeSyncResult(SystemTimeSyncOutcome.Succeeded, 0),
            await service.SynchronizeAsync());
        Assert.IsNotNull(captured);
        Assert.AreEqual(Path.Combine(@"C:\Windows", "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
            captured.FileName);
        Assert.IsTrue(captured.UseShellExecute);
        Assert.AreEqual("runas", captured.Verb);
        Assert.AreEqual(ProcessWindowStyle.Hidden, captured.WindowStyle);
        Assert.AreEqual(string.Empty, captured.Arguments);
        CollectionAssert.AreEqual(new[]
        {
            "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command",
            WindowsTimeSynchronizer.SynchronizationScript
        }, captured.ArgumentList.ToArray());
    }

    [TestMethod]
    [DataRow(0, SystemTimeSyncOutcome.Succeeded)]
    [DataRow(5, SystemTimeSyncOutcome.Failed)]
    [DataRow(1460, SystemTimeSyncOutcome.TimedOut)]
    public async Task HelperExitCode_IsReported(int exitCode, SystemTimeSyncOutcome outcome)
    {
        var service = new WindowsTimeSynchronizer(_ => Task.FromResult(exitCode), () => true, @"C:\Windows");
        Assert.AreEqual(new SystemTimeSyncResult(outcome, exitCode), await service.SynchronizeAsync());
    }

    [TestMethod]
    public async Task UacCancellation_IsDistinctFromFailure()
    {
        var cancelled = new WindowsTimeSynchronizer(_ => Task.FromException<int>(new Win32Exception(1223)),
            () => true, @"C:\Windows");
        var denied = new WindowsTimeSynchronizer(_ => Task.FromException<int>(new Win32Exception(5)),
            () => true, @"C:\Windows");
        Assert.AreEqual(new SystemTimeSyncResult(SystemTimeSyncOutcome.Cancelled, 1223),
            await cancelled.SynchronizeAsync());
        Assert.AreEqual(new SystemTimeSyncResult(SystemTimeSyncOutcome.Failed, 5),
            await denied.SynchronizeAsync());
    }

    [TestMethod]
    public async Task ProcessUnavailable_IsReportedWithoutThrowing()
    {
        var service = new WindowsTimeSynchronizer(_ => Task.FromException<int>(new InvalidOperationException()),
            () => true, @"C:\Windows");
        Assert.AreEqual(new SystemTimeSyncResult(SystemTimeSyncOutcome.Failed), await service.SynchronizeAsync());
    }
}
