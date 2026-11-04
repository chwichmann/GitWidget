using System.Diagnostics;

namespace GitStatusWidget.Desktop.Services;

/// <summary>Opens a local folder in the platform file manager (arguments passed as a list, no shell parsing).</summary>
public static class FolderOpener
{
    public static bool TryOpen(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        var start = OperatingSystem.IsWindows() ? new ProcessStartInfo("explorer.exe")
            : OperatingSystem.IsMacOS() ? new ProcessStartInfo("/usr/bin/open")
            : new ProcessStartInfo("xdg-open");
        start.ArgumentList.Add(Path.GetFullPath(path));
        start.UseShellExecute = false;
        try
        {
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
