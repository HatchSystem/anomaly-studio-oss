namespace AnomalyStudio.Core;

/// <summary>表示済みのセルを、カンマ・引用符・改行を壊さずに CSV の 1 レコードへ変換する。</summary>
public static class CsvRow
{
    private static readonly char[] QuotedCharacters = [',', '"', '\r', '\n'];

    /// <summary>行末の改行は付けない。null は空欄として出力する。</summary>
    public static string Format(params string?[] fields) => string.Join(',', fields.Select(Escape));

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.IndexOfAny(QuotedCharacters) >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}
