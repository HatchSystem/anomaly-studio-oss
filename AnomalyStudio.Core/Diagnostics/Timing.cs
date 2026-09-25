using System.Diagnostics;

namespace AnomalyStudio.Core.Diagnostics;

/// <summary>段階ごとの所要時間の記録。<see cref="Detail"/> は件数など補足（空なら省略）。</summary>
public readonly record struct TimingEvent(string Category, string Name, TimeSpan Elapsed, string Detail);

/// <summary>
/// 処理の段階ごとの所要時間を計測して通知する（高速化の判断は推測でなく計測値で行うため）。
/// Core はログの仕組みを持たないので、購読側（アプリ）が <see cref="Recorded"/> をログに書く。購読がなければ何もしない。
/// カテゴリーは analysis（分析）、walkforward（ウォークフォワード）、ingest（取込）、storage（保存・読込）、ui（画面の読み直し）。
/// </summary>
public static class Timing
{
    public static event Action<TimingEvent>? Recorded;

    /// <summary>計測を始める。<see cref="Scope.Dispose"/> で記録する。</summary>
    public static Scope Start(string category, string name) => new(category, name);

    /// <summary>計測済みの時間を記録する（Stopwatch を自分で持つ場合）。</summary>
    public static void Record(string category, string name, TimeSpan elapsed, string detail = "")
    {
        Recorded?.Invoke(new TimingEvent(category, name, elapsed, detail));
    }

    /// <summary>1 つの段階の計測。<see cref="Detail"/> は記録の前に設定できる。</summary>
    public sealed class Scope(string category, string name) : IDisposable
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private bool _done;

        public string Detail { get; set; } = string.Empty;

        public TimeSpan Elapsed => _stopwatch.Elapsed;

        public void Dispose()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            Record(category, name, _stopwatch.Elapsed, Detail);
        }
    }
}
