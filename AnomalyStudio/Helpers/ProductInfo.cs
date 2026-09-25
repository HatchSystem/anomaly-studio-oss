using System.Reflection;

namespace AnomalyStudio.Helpers;

/// <summary>アプリの名前と版（タイトルバー・起動画面・設定・更新確認で共通）。</summary>
public static class ProductInfo
{
    public const string Name = "AnomalyStudio";

    /// <summary>
    /// アプリの版（例 0.2.0、0.2.0-beta.1）。唯一の元は git のタグで、リリースの CI が VersionPrefix / VersionSuffix として渡す。
    /// 開発中のビルドは csproj の VersionPrefix。値は csproj がアセンブリのメタデータ AppVersion に埋め込む（Uno の SDK は版の属性を生成しないため）。
    /// </summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>プレリリース（0.2.0-beta.1 など）。プレリリースの版だけが、更新確認でプレリリースも受け取る。</summary>
    public static bool IsPrerelease => Version.Contains('-', StringComparison.Ordinal);

    private static string ReadVersion()
    {
        var assembly = typeof(ProductInfo).Assembly;
        var version = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "AppVersion")?.Value
                      ?? assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? "0.0.0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? version[..plus] : version;
    }
}
