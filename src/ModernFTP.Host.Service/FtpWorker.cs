using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModernFTP.Config;
using ModernFTP.Engine;

namespace ModernFTP.Host.Service;

/// <summary>Hosts the FTP engine for the lifetime of the Windows service.</summary>
internal sealed class FtpWorker : BackgroundService
{
    private readonly ILogger<FtpWorker> _logger;
    private readonly ServiceSettings _settings;

    public FtpWorker(ILogger<FtpWorker> logger, ServiceSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = _settings.ConfigPath;
        var directory = Path.GetDirectoryName(path)!;
        using var file = new RollingFileLog(directory);
        try
        {
            if (!File.Exists(path))
            {
                Fail($"Config file not found: {path}", file);
                return;
            }

            var config = ConfigLoader.Load(path);
            var errors = ConfigLoader.Validate(config, directory);
            if (errors.Count > 0)
            {
                Fail("Config is invalid: " + string.Join("; ", errors), file);
                return;
            }

            await using var server = new FtpServer(ConfigLoader.ToServerOptions(config, directory));
            using var subscription = server.Subscribe(e => file.WriteLine(Format(e)));
            await server.StartAsync(stoppingToken).ConfigureAwait(false);
            var started = $"{FtpServer.ServerName} listening on {server.LocalEndPoint}, config {path}";
            file.WriteLine(Stamp(started));
            _logger.LogInformation("{Message}", started);

            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await server.StopAsync().ConfigureAwait(false);
            file.WriteLine(Stamp("Stopped."));
            _logger.LogInformation("ModernFTP stopped.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or PortInUseException)
        {
            Fail($"ModernFTP could not run: {ex.Message}", file);
        }
    }

    private void Fail(string message, RollingFileLog file)
    {
        file.WriteLine(Stamp(message));
        _logger.LogError("{Message}", message);
        Environment.ExitCode = 1;
    }

    internal static string Format(ServerEvent e)
    {
        var who = e.UserName is null ? string.Empty : $" {e.UserName}";
        return $"{e.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff} [{e.SessionId}]{who} {e.Kind}: {e.Describe()}";
    }

    private static string Stamp(string text) => $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {text}";
}
