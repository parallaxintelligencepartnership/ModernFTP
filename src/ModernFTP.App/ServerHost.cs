using System.IO;
using System.Windows.Threading;
using ModernFTP.Config;
using ModernFTP.Engine;

namespace ModernFTP.App;

/// <summary>Owns the engine's <see cref="FtpServer"/> for the app and hands its events to the UI thread in batches.</summary>
public sealed class ServerHost
{
    private readonly EventBatcher _events;
    private FtpServer? _server;
    private IDisposable? _subscription;

    public ServerHost()
    {
        Store = new ConfigStore(ConfigPath);
        Controller = new EngineSessionController(() => _server, Store);
        _events = new EventBatcher(Dispatcher.CurrentDispatcher, (batch, dropped) => EventsReceived?.Invoke(batch, dropped));
    }

    /// <summary>
    /// Raised on the UI thread at most once per <see cref="EventBatcher.Interval"/> with the engine events since
    /// the last call and how many were dropped because the queue was full.
    /// </summary>
    public event Action<IReadOnlyList<ServerEvent>, long>? EventsReceived;

    /// <summary>Raised on the UI thread after the server started or stopped.</summary>
    public event Action? StateChanged;

    public ISessionController Controller { get; }

    public ConfigStore Store { get; }

    public int ActiveConnections => _server?.ActiveConnections ?? 0;

    public long TotalBytesSent => _server?.TotalBytesSent ?? 0;

    public long TotalBytesReceived => _server?.TotalBytesReceived ?? 0;

    public bool IsRunning => _server?.IsRunning == true;

    public string ConfigPath { get; } = ConfigPaths.DefaultConfigPath();

    public ModernFtpConfig LoadConfig() =>
        File.Exists(ConfigPath) ? ConfigLoader.Load(ConfigPath) : new ModernFtpConfig();

    /// <summary>Starts the server. Returns an error message, or null on success.</summary>
    public async Task<string?> StartAsync()
    {
        if (IsRunning)
        {
            return null;
        }

        try
        {
            var config = LoadConfig();
            var options = ConfigLoader.ToServerOptions(config, Path.GetDirectoryName(ConfigPath)!);
            var server = new FtpServer(options);
            _subscription = _events.Attach(server);
            _server = server;
            await server.StartAsync();
            ListeningOn = server.LocalEndPoint?.ToString();
        }
        catch (Exception ex)
        {
            _subscription?.Dispose();
            _subscription = null;
            _server = null;
            return ex.Message;
        }

        StateChanged?.Invoke();
        return null;
    }

    public async Task StopAsync()
    {
        var server = _server;
        if (server is null)
        {
            return;
        }

        _server = null;
        await server.StopAsync();
        _subscription?.Dispose();
        _subscription = null;
        await server.DisposeAsync();
        _events.Flush();
        StateChanged?.Invoke();
    }

    /// <summary>Stops the event timer; call when the window that owns this host closes.</summary>
    public void DisposeEvents() => _events.Dispose();

    public string? ListeningOn { get; private set; }
}
