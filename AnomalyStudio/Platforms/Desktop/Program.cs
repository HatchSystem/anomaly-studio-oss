using Uno.UI.Hosting;

namespace AnomalyStudio;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 更新の仕組み（Velopack）の起動処理。インストール・更新・アンインストールの途中で呼ばれたときはここで処理して終わる。
        // ダウンロード済みの更新があれば、ここで反映してから起動する。画面を作る前に呼ぶ必要がある
        Velopack.VelopackApp.Build().Run();

        App.InitializeLogging();

        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseX11()
            .UseLinuxFrameBuffer()
            .UseMacOS()
            .UseWin32()
            .Build();

        host.Run();
    }
}
