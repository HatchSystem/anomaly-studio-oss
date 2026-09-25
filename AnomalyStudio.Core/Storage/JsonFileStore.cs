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
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = Encoder }))
        {
            JsonSerializer.Serialize(writer, value, typeInfo);
        }

        File.Move(temp, path, overwrite: true);
    }
}
