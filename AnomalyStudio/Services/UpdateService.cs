using System.ComponentModel;
using System.Runtime.InteropServices;
using Velopack;
using Velopack.Sources;

namespace AnomalyStudio.Services;

/// <summary>更新の状態。</summary>
public enum UpdateState
{
    /// <summary>インストール版でない（開発中の実行など）。更新できない。</summary>
    NotInstalled,

    /// <summary>まだ確認していない。</summary>
    Idle,

    Checking,

    /// <summary>最新の版。</summary>
    UpToDate,

    Downloading,

    /// <summary>ダウンロード済み。再起動（または次の起動）で更新される。</summary>
    ReadyToRestart,

    Failed,
}

/// <summary>アプリの更新（確認・ダウンロード・再起動して更新）。画面はこのインターフェースを通して使う。</summary>
public interface IUpdateService : INotifyPropertyChanged
{
    UpdateState State { get; }

    /// <summary>状態の説明（「最新の版です」「0.2.0 をダウンロード中 40%」など）。</summary>
    string StatusText { get; }

    /// <summary>ダウンロード済みの新しい版。なければ null。</summary>
    string? AvailableVersion { get; }

    /// <summary>「更新を確認」を押せる（インストール版で、確認・ダウンロード中でなく、ダウンロード済みの更新もない）。</summary>
    bool CanCheck { get; }

    /// <summary>ダウンロード済みで、再起動すれば更新される。</summary>
    bool IsReadyToRestart { get; }

    /// <summary>更新を確認し、新しい版があればダウンロードする。ダウンロード済みの更新は次の起動でも反映される。</summary>
    Task CheckAsync();

    /// <summary>ダウンロード済みの更新を反映してアプリを再起動する。</summary>
    void RestartToApply();
}

/// <summary>
/// Velopack による更新。配布元は公開リポジトリ（HatchSystem/anomaly-studio-oss）の GitHub Releases で、
/// 動いている OS と CPU に合うチャネル（win-x64、win-arm64、osx-arm64、osx-x64）の版だけを見る。
/// プレリリースの版（0.2.0-beta.1 など）で動いているときだけ、プレリリースも受け取る。
/// ダウンロードした更新は、「再起動して更新」を押すか、次にアプリを起動したときに反映される（Velopack の起動処理が行う）。
/// 更新しても、データ（%LOCALAPPDATA%\AnomalyStudio、macOS は ~/Library/Application Support/AnomalyStudio）はインストール先の外にあるので消えない。
/// </summary>
public sealed partial class UpdateService : ObservableObject, IUpdateService
{
    /// <summary>更新の配布元（公開リポジトリ）。</summary>
    public const string RepositoryUrl = "https://github.com/HatchSystem/anomaly-studio-oss";

    private readonly ILogger<UpdateService> _logger;
    private readonly UpdateManager? _manager;
    private UpdateInfo? _pending;

    public UpdateService(ILogger<UpdateService> logger)
    {
        _logger = logger;
        try
        {
            _manager = new UpdateManager(CreateSource(), new UpdateOptions { ExplicitChannel = Channel });
            State = _manager.IsInstalled ? UpdateState.Idle : UpdateState.NotInstalled;
        }
        catch (Exception ex)
        {
            // 更新の仕組みが使えなくてもアプリは動かす
            _logger.LogWarning(ex, "更新の確認を準備できません");
            State = UpdateState.NotInstalled;
        }

        StatusText = State == UpdateState.NotInstalled ? "インストールした版でだけ更新を確認できます" : "まだ確認していません";
    }

    /// <summary>
    /// 動いている OS と CPU のチャネル（リリースの CI が同じ名前で作る）。Windows の x64 版を ARM の Windows で動かしているときも、
    /// プロセスの CPU で決めるので、同じ種類の版を受け取る。
    /// </summary>
    public static string Channel =>
        (OperatingSystem.IsMacOS() ? "osx" : "win") + "-" + RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };

    private static IUpdateSource CreateSource()
    {
#if DEBUG
        // 更新の確認用: ANOMALYSTUDIO_UPDATE_SOURCE にローカルのフォルダーを指定すると、そこに置いたパッケージから更新する
        if (Environment.GetEnvironmentVariable("ANOMALYSTUDIO_UPDATE_SOURCE") is { Length: > 0 } folder)
        {
            return new SimpleFileSource(new DirectoryInfo(folder));
        }
#endif
        return new GithubSource(RepositoryUrl, accessToken: null, prerelease: ProductInfo.IsPrerelease);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheck), nameof(IsReadyToRestart))]
    public partial UpdateState State { get; private set; }

    public bool CanCheck => State is UpdateState.Idle or UpdateState.UpToDate or UpdateState.Failed;

    public bool IsReadyToRestart => State == UpdateState.ReadyToRestart;

    [ObservableProperty]
    public partial string StatusText { get; private set; }

    [ObservableProperty]
    public partial string? AvailableVersion { get; private set; }

    public async Task CheckAsync()
    {
        if (_manager is null || State is UpdateState.NotInstalled or UpdateState.Checking or UpdateState.Downloading or UpdateState.ReadyToRestart)
        {
            return;
        }

        try
        {
            State = UpdateState.Checking;
            StatusText = "更新を確認中…";
            var update = await _manager.CheckForUpdatesAsync();
            if (update is null)
            {
                State = UpdateState.UpToDate;
                StatusText = $"最新の版です（{DateTime.Now:MM/dd HH:mm} 確認）";
                return;
            }

            var version = update.TargetFullRelease.Version.ToString();
            State = UpdateState.Downloading;
            StatusText = $"{version} をダウンロード中…";
            await _manager.DownloadUpdatesAsync(update, percent => StatusText = $"{version} をダウンロード中 {percent}%");
            _pending = update;
            AvailableVersion = version;
            State = UpdateState.ReadyToRestart;
            StatusText = $"{version} の準備ができました。再起動すると更新されます（次にアプリを起動したときにも更新されます）";
            _logger.LogInformation("更新 {Version} をダウンロードしました", version);
        }
        catch (Exception ex)
        {
            // 通信できないときなど。次の確認で再び試す
            _logger.LogWarning(ex, "更新の確認に失敗しました");
            State = UpdateState.Failed;
            StatusText = $"更新を確認できませんでした（{ex.Message}）";
        }
    }

    public void RestartToApply()
    {
        if (_manager is null || _pending is null)
        {
            return;
        }

        _logger.LogInformation("更新 {Version} を反映して再起動します", AvailableVersion);
        _manager.ApplyUpdatesAndRestart(_pending);
    }
}
