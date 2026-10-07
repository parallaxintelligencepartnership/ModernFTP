namespace ModernFTP.Config;

public static class ConfigPaths
{
    public const string AppFolderName = "ModernFTP";
    public const string UnixFolderName = "modernftp";
    public const string PortableMarker = "portable";
    public const string ConfigFileName = "config.json";

    /// <summary>
    /// Portable mode (a file named "portable" next to the exe) keeps config beside the exe.
    /// Otherwise %AppData%\ModernFTP on Windows and ~/.config/modernftp elsewhere.
    /// </summary>
    public static string ResolveConfigDirectory(string? executableDirectory = null)
    {
        executableDirectory ??= AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(executableDirectory, PortableMarker)))
        {
            return Path.GetFullPath(executableDirectory);
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", UnixFolderName);
    }

    /// <summary>The machine wide config folder used by the Windows service: %ProgramData%\ModernFTP.</summary>
    public static string MachineConfigDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppFolderName);

    public static string MachineConfigPath() => Path.Combine(MachineConfigDirectory(), ConfigFileName);

    public static string DefaultConfigPath(string? executableDirectory = null) =>
        Path.Combine(ResolveConfigDirectory(executableDirectory), ConfigFileName);
}
