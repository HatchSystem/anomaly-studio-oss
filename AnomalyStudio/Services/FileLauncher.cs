using System.Diagnostics;

namespace AnomalyStudio.Services;

/// <summary>保存したファイルを既定のアプリで開く、または保存先のフォルダをファイルを選んだ状態で開く。</summary>
public static class FileLauncher
{
    public static void OpenFile(string path) => Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });

    public static void OpenFolder(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", ArgumentList = { "/select,", path }, UseShellExecute = false });
        }
        else if (OperatingSystem.IsMacOS())
        {
            // Finder でファイルを選んだ状態で開く
            Process.Start(new ProcessStartInfo { FileName = "open", ArgumentList = { "-R", path }, UseShellExecute = false });
        }
        else
        {
            Process.Start(new ProcessStartInfo { FileName = Path.GetDirectoryName(path)!, UseShellExecute = true });
        }
    }
}
