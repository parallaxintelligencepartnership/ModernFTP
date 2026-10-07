using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using ModernFTP.Config;

namespace ModernFTP.App;

/// <summary>Runs "modernftp firewall add" elevated (UAC prompt) and turns the outcome into a status line.</summary>
public static class FirewallHelper
{
    private const int ErrorCancelled = 1223;

    public static string ConsoleExePath() => Path.Combine(AppContext.BaseDirectory, "modernftp.exe");

    public static string BuildArguments(string configPath, string appExePath) =>
        $"firewall add --config \"{configPath}\" --program \"{appExePath}\"";

    public static string Run(string configPath)
    {
        var appExe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ModernFTP.exe");
        var info = new ProcessStartInfo(ConsoleExePath(), BuildArguments(configPath, appExe))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return string.Format(CultureInfo.CurrentCulture, Strings.SetupFirewallLaunchFailedFormat, ConsoleExePath());
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return string.Format(CultureInfo.CurrentCulture, Strings.SetupFirewallFailedFormat, process.ExitCode);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return Strings.SetupFirewallDeclined;
        }
        catch (Win32Exception ex)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.SetupFirewallLaunchFailedFormat, ex.Message);
        }

        var config = ConfigLoader.Load(configPath);
        return string.Format(CultureInfo.CurrentCulture, Strings.SetupFirewallDone, config.Port, config.PassivePortMin, config.PassivePortMax);
    }
}
