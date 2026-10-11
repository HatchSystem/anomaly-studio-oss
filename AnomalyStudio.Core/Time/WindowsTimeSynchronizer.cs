using System.ComponentModel;
using System.Diagnostics;

namespace AnomalyStudio.Core.Time;

public interface ISystemTimeSynchronizer
{
    bool IsSupported { get; }
    Task<SystemTimeSyncResult> SynchronizeAsync();
}

public enum SystemTimeSyncOutcome
{
    Succeeded,
    Cancelled,
    Failed,
    Unsupported,
    TimedOut
}

public sealed record SystemTimeSyncResult(SystemTimeSyncOutcome Outcome, int? ExitCode = null);

/// <summary>明示操作でのみ、Windows の設定済み時刻サーバーへ再同期を要求する。</summary>
public sealed class WindowsTimeSynchronizer : ISystemTimeSynchronizer
{
    private const int CancelledError = 1223;
    private const int TimeoutError = 1460;
    private readonly Func<ProcessStartInfo, Task<int>> _run;
    private readonly Func<bool> _isWindows;
    private readonly string _windowsDirectory;

    // 固定スクリプトのみを渡す。サーバー、サービスの起動方式、レジストリは変更しない。
    internal const string SynchronizationScript = """
        $ErrorActionPreference = 'Stop'
        try {
            $service = Get-Service -Name W32Time
            if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
                Start-Service -Name W32Time
                $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(10))
            }
            $info = [System.Diagnostics.ProcessStartInfo]::new()
            $info.FileName = [System.IO.Path]::Combine($env:SystemRoot, 'System32', 'w32tm.exe')
            $info.Arguments = '/resync'
            $info.UseShellExecute = $false
            $info.CreateNoWindow = $true
            $info.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
            $process = [System.Diagnostics.Process]::Start($info)
            if ($null -eq $process) { exit 1 }
            try {
                if (-not $process.WaitForExit(30000)) {
                    try { $process.Kill() } catch { }
                    exit 1460
                }
                $code = $process.ExitCode
            } finally {
                $process.Dispose()
            }
            exit $code
        } catch {
            exit 1
        }
        """;

    public WindowsTimeSynchronizer()
        : this(RunAsync, OperatingSystem.IsWindows,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows))
    {
    }

    internal WindowsTimeSynchronizer(Func<ProcessStartInfo, Task<int>> run, Func<bool> isWindows,
        string windowsDirectory)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _isWindows = isWindows ?? throw new ArgumentNullException(nameof(isWindows));
        _windowsDirectory = windowsDirectory;
    }

    public bool IsSupported => _isWindows();

    public async Task<SystemTimeSyncResult> SynchronizeAsync()
    {
        if (!IsSupported)
            return new(SystemTimeSyncOutcome.Unsupported);

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(_windowsDirectory, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-WindowStyle");
        startInfo.ArgumentList.Add("Hidden");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(SynchronizationScript);

        try
        {
            var exitCode = await _run(startInfo).ConfigureAwait(false);
            return new(exitCode switch
            {
                0 => SystemTimeSyncOutcome.Succeeded,
                TimeoutError => SystemTimeSyncOutcome.TimedOut,
                _ => SystemTimeSyncOutcome.Failed
            }, exitCode);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == CancelledError)
        {
            return new(SystemTimeSyncOutcome.Cancelled, CancelledError);
        }
        catch (Win32Exception ex)
        {
            return new(SystemTimeSyncOutcome.Failed, ex.NativeErrorCode);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new(SystemTimeSyncOutcome.Failed);
        }
    }

    private static Task<int> RunAsync(ProcessStartInfo startInfo) => Task.Run(async () =>
    {
        // UAC の応答待ちは UI スレッド外で行う。実行した helper 自身の終了だけを待つ。
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("時刻同期プロセスを起動できませんでした。");
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    });
}
