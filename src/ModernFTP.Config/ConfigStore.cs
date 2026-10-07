using ModernFTP.Engine;

namespace ModernFTP.Config;

/// <summary>
/// Reads and writes one config.json. Saves are atomic (a temporary file replaces the old one) and
/// serialized, so a ban persisted from a server thread cannot interleave with a save from the UI.
/// </summary>
public sealed class ConfigStore(string path)
{
    private readonly object _gate = new();

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    /// <summary>The store for the default config location (see <see cref="ConfigPaths"/>).</summary>
    public static ConfigStore Default() => new(ConfigPaths.DefaultConfigPath());

    public ModernFtpConfig Load()
    {
        lock (_gate)
        {
            return ConfigLoader.Load(Path);
        }
    }

    public void Save(ModernFtpConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate)
        {
            Write(config);
        }
    }

    /// <summary>
    /// Replaces only bannedAddresses in the file on disk, keeping every other setting as it is there.
    /// A missing file is created with defaults plus the bans.
    /// </summary>
    public void SaveBannedAddresses(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_gate)
        {
            var config = File.Exists(Path) ? ConfigLoader.Load(Path) : new ModernFtpConfig();
            config.BannedAddresses = [.. entries.Select(ToConfigEntry)];
            Write(config);
        }
    }

    /// <summary>
    /// Keeps bannedAddresses in config.json in step with the running server's <see cref="BanList"/>,
    /// which stays the runtime copy. Dispose the result to stop. Save failures go to
    /// <paramref name="onError"/> instead of the code that changed the list.
    /// </summary>
    public IDisposable PersistBans(BanList banList, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(banList);
        void OnChanged(object? sender, EventArgs e)
        {
            try
            {
                SaveBannedAddresses(banList.Entries);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                onError?.Invoke(ex);
            }
        }

        banList.Changed += OnChanged;
        return new Subscription(() => banList.Changed -= OnChanged);
    }

    /// <summary>A single address is written without its /32 or /128, the way people type it.</summary>
    private static string ToConfigEntry(string entry) =>
        entry.EndsWith("/32", StringComparison.Ordinal) && !entry.Contains(':', StringComparison.Ordinal) ? entry[..^3]
        : entry.EndsWith("/128", StringComparison.Ordinal) && entry.Contains(':', StringComparison.Ordinal) ? entry[..^4]
        : entry;

    private void Write(ModernFtpConfig config) => ConfigLoader.Save(config, Path);

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
