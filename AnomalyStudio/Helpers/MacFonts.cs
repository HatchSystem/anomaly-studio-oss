using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;
using Uno.UI;

namespace AnomalyStudio.Helpers;

/// <summary>
/// macOS 用の書体の差し替え。Windows では呼ばない（Windows の書体は Typography.xaml のまま）。
/// Typography.xaml の書体（Yu Gothic UI・Segoe UI Variable・Cascadia Mono）は Mac に無く、代わりの書体で文字幅が変わって配置が崩れるので、Mac の書体に置き換える。
/// また Mac では ms-appx:/// で指定したフォント（Phosphor と Uno の Symbols）が読み込まれず、アイコンが「?」の箱になるため、
/// 同梱フォントを CoreText へ直接登録し、ファイルではなく書体名で参照させる（Mac のシステム書体と同じ経路で読まれる）。
/// </summary>
internal static class MacFonts
{
    private const string TextFamily = "Hiragino Sans";

    /// <summary>リソースのキーと、Mac で使う書体名。</summary>
    private static readonly (string Key, string Family)[] TextFonts =
    [
        ("AsFontFamily", TextFamily),
        ("AsDisplayFontFamily", TextFamily),
        ("ContentControlThemeFontFamily", TextFamily),
        ("AsClockFontFamily", "Helvetica Neue"),
        ("AsMonoFontFamily", "Menlo"),
    ];

    /// <summary>同梱のアイコンフォント（発行先からの相対パス）、登録後の書体名、確認に使う文字。Key が null のものは Uno の既定の記号フォント。</summary>
    private static readonly (string? Key, string File, string Family, int Probe)[] IconFonts =
    [
        ("AsIconFontFamily", "Assets/Fonts/Phosphor.ttf", "Phosphor", 0xE464),
        ("AsIconFillFontFamily", "Assets/Fonts/Phosphor-Fill.ttf", "Phosphor-Fill", 0xE464),
        (null, "Uno.Fonts.Fluent/Fonts/uno-fluentui-assets.ttf", "Symbols", 0xE787),
    ];

    /// <summary>画面を作る前（App の InitializeComponent 直後）に呼ぶ。使えないと分かった書体は差し替えずに元の指定を残す。</summary>
    public static void Apply(ResourceDictionary resources, ILogger logger)
    {
        var installed = new HashSet<string>(SKFontManager.Default.FontFamilies, StringComparer.OrdinalIgnoreCase);
        if (installed.Contains(TextFamily))
        {
            FeatureConfiguration.Font.DefaultTextFontFamily = TextFamily;
        }

        foreach (var (key, family) in TextFonts)
        {
            if (!installed.Contains(family))
            {
                logger.LogWarning("Mac の書体 {Family} が見つからないため {Key} は元の指定のまま", family, key);
                continue;
            }

            Replace(resources, key, family, logger);
        }

        foreach (var (key, file, family, probe) in IconFonts)
        {
            var path = Path.Combine(AppContext.BaseDirectory, file);
            if (!File.Exists(path))
            {
                logger.LogWarning("アイコンフォント {Path} が見つからない", path);
                continue;
            }

            // 原因の切り分け用: ファイルから直接読めるか（読めるなら Uno の ms-appx の読み込み側の問題）
            using (var direct = SKTypeface.FromFile(path))
            {
                logger.LogInformation("アイコンフォント {File}: ファイルから直接の読み込み {Result}", file, direct is null ? "失敗" : "成功");
            }

            if (!Register(path, logger) || !HasGlyph(family, probe))
            {
                logger.LogWarning("アイコンフォント {Family} を書体名で使えないため、元の指定のまま", family);
                continue;
            }

            if (key is null)
            {
                FeatureConfiguration.Font.SymbolsFont = family;
                logger.LogInformation("記号フォントを書体名 {Family} に切り替え", family);
            }
            else
            {
                Replace(resources, key, family, logger);
            }
        }
    }

    /// <summary>
    /// キーを定義している辞書（Typography.xaml）の値を置き換える。XAML の StaticResource は実行時に引くので、まだ作っていない画面とスタイルに効く。
    /// 最上位の辞書に同じキーを足すだけでは、Typography.xaml 内のスタイル（自分の辞書を先に引く）に効かない。
    /// </summary>
    private static void Replace(ResourceDictionary resources, string key, string family, ILogger logger)
    {
        if (FindOwner(resources, key) is { } owner)
        {
            owner[key] = new FontFamily(family);
            logger.LogInformation("{Key} を {Family} に切り替え", key, family);
        }
        else
        {
            logger.LogWarning("リソース {Key} が見つからない", key);
        }
    }

    private static ResourceDictionary? FindOwner(ResourceDictionary dictionary, string key)
    {
        // ContainsKey は結合した辞書の中まで探すので、その辞書自身のキー（Keys）で判定する
        if (dictionary.Keys.Contains(key))
        {
            return dictionary;
        }

        for (var i = dictionary.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            if (FindOwner(dictionary.MergedDictionaries[i], key) is { } owner)
            {
                return owner;
            }
        }

        return null;
    }

    /// <summary>書体名で引いたフォントが目的の書体そのもので、確認用の文字を持っているか（CoreText は無い書体名に別の書体を返すため）。</summary>
    private static bool HasGlyph(string family, int codepoint)
    {
        using var typeface = SKTypeface.FromFamilyName(family);
        if (typeface is null || !string.Equals(typeface.FamilyName, family, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        using var font = typeface.ToFont();
        return font.ContainsGlyph(codepoint);
    }

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreText = "/System/Library/Frameworks/CoreText.framework/CoreText";

    /// <summary>kCTFontManagerScopeProcess（このプロセスの中だけで使える登録）。</summary>
    private const uint ScopeProcess = 1;

    /// <summary>kCTFontManagerErrorAlreadyRegistered。</summary>
    private const nint AlreadyRegistered = 105;

    /// <summary>フォントファイルを CoreText に登録する（このプロセスの間だけ）。</summary>
    private static bool Register(string path, ILogger logger)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        var url = CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, bytes, bytes.Length, false);
        if (url == IntPtr.Zero)
        {
            logger.LogWarning("フォントの URL を作れない: {Path}", path);
            return false;
        }

        try
        {
            if (CTFontManagerRegisterFontsForURL(url, ScopeProcess, out var error))
            {
                return true;
            }

            var code = error == IntPtr.Zero ? 0 : CFErrorGetCode(error);
            if (error != IntPtr.Zero)
            {
                CFRelease(error);
            }

            if (code == AlreadyRegistered)
            {
                return true;
            }

            logger.LogWarning("フォントを CoreText に登録できない（エラー {Code}）: {Path}", code, path);
            return false;
        }
        finally
        {
            CFRelease(url);
        }
    }

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFURLCreateFromFileSystemRepresentation(IntPtr allocator, byte[] buffer, nint bufferLength, [MarshalAs(UnmanagedType.I1)] bool isDirectory);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);

    [DllImport(CoreFoundation)]
    private static extern nint CFErrorGetCode(IntPtr error);

    [DllImport(CoreText)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CTFontManagerRegisterFontsForURL(IntPtr fontUrl, uint scope, out IntPtr error);
}
