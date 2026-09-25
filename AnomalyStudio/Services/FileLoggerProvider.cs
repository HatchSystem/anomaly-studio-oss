using System.Globalization;
using System.Text;
using AnomalyStudio.Core;

namespace AnomalyStudio.Services;

/// <summary>
/// 日別のテキストファイルにログを書く（%LOCALAPPDATA%\AnomalyStudio\logs\anomalystudio-yyyyMMdd.log、JST）。
/// 利用者の環境で起きた失敗を後から追えるようにするためのもので、古いファイルは起動時に消す。
/// 秘密値（API キーなど）はログに書かない（呼び出し側の責務。例外メッセージにも含めない設計にしている）。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private DateOnly _writerDay;

    public FileLoggerProvider(string directory, int keepDays = 14)
    {
        _directory = directory;
        try
        {
            Directory.CreateDirectory(directory);
            var cutoff = Jst.Today.AddDays(-keepDays);
            foreach (var file in Directory.EnumerateFiles(directory, "anomalystudio-*.log"))
            {
                var stamp = Path.GetFileNameWithoutExtension(file)["anomalystudio-".Length..];
                if (DateOnly.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ログが書けなくてもアプリは動かす
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var now = Jst.Now;
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ').Append(Level(level))
            .Append(' ').Append(category)
            .Append(": ").Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        lock (_gate)
        {
            try
            {
                var day = DateOnly.FromDateTime(now);
                if (_writer is null || day != _writerDay)
                {
                    _writer?.Dispose();
                    _writer = new StreamWriter(
                        Path.Combine(_directory, $"anomalystudio-{day:yyyyMMdd}.log"), append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    _writerDay = day;
                }

                _writer.WriteLine(line.ToString());
                _writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // ログが書けなくてもアプリは動かす
            }
        }
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "FATAL",
        _ => "NONE ",
    };

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
