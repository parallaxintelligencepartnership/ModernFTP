using System.Windows;

namespace ModernFTP.App;

public static class ThemeHelper
{
    /// <summary>True when the effective theme of the window is dark. System mode asks Windows.</summary>
    public static bool IsDark(Window window)
    {
        var mode = window.ThemeMode == ThemeMode.None ? Application.Current.ThemeMode : window.ThemeMode;
        if (mode == ThemeMode.Dark)
        {
            return true;
        }

        if (mode == ThemeMode.Light || !OperatingSystem.IsWindows())
        {
            return false;
        }

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    public static void Apply(Window window, ThemeMode mode)
    {
        Application.Current.ThemeMode = mode;
        window.ThemeMode = mode;
    }
}
