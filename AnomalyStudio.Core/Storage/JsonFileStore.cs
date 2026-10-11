using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AnomalyStudio.Core.Storage;

/// <summary>設定・モード・銘柄を人が読める JSON で保存する。書き込みは一時ファイル経由で置き換え、途中で落ちても壊れない。</summary>
public static class JsonFileStore
{
    /// <summary>日本語をエスケープせずに書き出す（ファイルを人が読めるように）。</summary>
    public static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>ファイルがなければ、または読めなければ <paramref name="fallback"/> を返す。</summary>
    public static T Load<T>(string path, JsonTypeInfo<T> typeInfo, Func<T> fallback)
    {
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                return JsonSerializer.Deserialize(stream, typeInfo) ?? fallback();
            }
        }
        catch (JsonException)
        {
            // 壊れたファイルは退避して既定値で起動する
            File.Copy(path, path + ".broken", overwrite: true);
        }

        return fallback();
    }

    public static void Save<T>(string path, T value, JsonTypeInfo<T> typeInfo)
        => Save(path, value, typeInfo, (source, destination) => File.Move(source, destination, overwrite: true), Thread.Sleep);

    /// <summary>置換と待機の境界だけを差し替え、失敗時の既存ファイル保持も実ファイルで検証する。</summary>
    internal static void Save<T>(string path, T value, JsonTypeInfo<T> typeInfo,
        Action<string, string> replace, Action<int> delay)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // 保存が重なっても、別の保存の途中にある一時ファイルを上書きしない。
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temp))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = Encoder }))
            {
                JsonSerializer.Serialize(writer, value, typeInfo);
            }

            // Windows の短いファイル保持に限って再試行する。最大 5 回・待機計 375 ms。
            // 同じアクセス拒否でも恒久的な権限不足は最後の例外をそのまま呼出元へ返す。
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    replace(temp, path);
                    break;
                }
                catch (Exception ex) when (attempt < 4 && IsRetryableReplacementFailure(ex))
                {
                    delay(25 << attempt);
                }
            }
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 後片付けの失敗で、保存失敗の本来の例外を上書きしない。
            }
        }
    }

    private static bool IsRetryableReplacementFailure(Exception ex) =>
        OperatingSystem.IsWindows() && (ex is IOException or UnauthorizedAccessException) &&
        // ERROR_ACCESS_DENIED / ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION。
        // 容量不足やパス不正など、ほかの I/O エラーは直ちに返す。
        (uint)ex.HResult is 0x80070005 or 0x80070020 or 0x80070021;
}
