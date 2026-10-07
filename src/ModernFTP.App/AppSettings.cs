using System.IO;
using System.Text.Json;
using ModernFTP.Config;

namespace ModernFTP.App;

/// <summary>Options that belong to the desktop app, not to the server. Stored beside config.json.</summary>
public sealed class AppSettings
{
    public const string FileName = "app-settings.json";

    public bool StartMinimizedToTray { get; set; }

    public bool StartWithWindows { get; set; }

    /// <summary>Start the FTP server as soon as the app opens. Always on when <see cref="StartWithWindows"/> is.</summary>
    public bool StartServerOnLaunch { get; set; } = true;

    public static string DefaultPath() => Path.Combine(ConfigPaths.ResolveConfigDirectory(), FileName);

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath();
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), ConfigLoader.JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged settings file falls back to defaults.
        }

        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, ConfigLoader.JsonOptions));
    }
}
