using System.Globalization;
using System.Runtime.InteropServices;
using ModernFTP.Config;
using ModernFTP.Engine;

namespace ModernFTP.Host.Console;

internal static class Program
{
    private const string Usage = """
        ModernFTP console host

        Usage:
          modernftp serve [--config <path>]          Run the server until Ctrl+C
          modernftp check-config [--config <path>]   Validate a config file and exit
          modernftp hash-password                    Read a password from stdin, print hash fields
          modernftp --version

        Without --config the default location is used (portable folder, %AppData%\ModernFTP or ~/.config/modernftp).
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            System.Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        if (args[0] is "--version" or "version")
        {
            System.Console.WriteLine(FtpServer.ServerName);
            return 0;
        }

        string? configPath = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "--config" or "-c" && i + 1 < args.Length)
            {
                configPath = args[++i];
            }
            else
            {
                System.Console.Error.WriteLine($"Unknown argument: {args[i]}");
                return 1;
            }
        }

        try
        {
            return args[0] switch
            {
                "serve" => await ServeAsync(configPath).ConfigureAwait(false),
                "check-config" => CheckConfig(configPath),
                "hash-password" => HashPassword(),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            System.Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
    }

    private static int UnknownCommand(string command)
    {
        System.Console.Error.WriteLine($"Unknown command: {command}");
        System.Console.Error.WriteLine(Usage);
        return 1;
    }

    private static (ModernFtpConfig Config, string Directory, IReadOnlyList<string> Errors) LoadConfig(string? configPath)
    {
        var path = Path.GetFullPath(configPath ?? ConfigPaths.DefaultConfigPath());
        if (!File.Exists(path))
        {
            throw new IOException($"Config file not found: {path}");
        }

        var directory = Path.GetDirectoryName(path)!;
        var config = ConfigLoader.Load(path);
        return (config, directory, ConfigLoader.Validate(config, directory));
    }

    private static int CheckConfig(string? configPath)
    {
        var (config, directory, errors) = LoadConfig(configPath);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                System.Console.Error.WriteLine($"Invalid: {error}");
            }

            return 2;
        }

        System.Console.WriteLine($"Config OK ({directory})");
        System.Console.WriteLine($"  listen {config.ListenAddress}:{config.Port}, passive ports {config.PassivePortMin} to {config.PassivePortMax}");
        System.Console.WriteLine($"  users {config.Users.Count}, anonymous {(config.AllowAnonymous ? "on" : "off")}, TLS {(config.Tls.Enabled ? "on" : "off")}");
        return 0;
    }

    private static int HashPassword()
    {
        if (!System.Console.IsInputRedirected)
        {
            System.Console.Error.Write("Password: ");
        }

        var password = System.Console.In.ReadLine();
        if (string.IsNullOrEmpty(password))
        {
            System.Console.Error.WriteLine("No password given.");
            return 1;
        }

        var credential = Pbkdf2Credential.Create(password);
        System.Console.WriteLine($"\"passwordHash\": \"{Convert.ToBase64String(credential.Hash)}\",");
        System.Console.WriteLine($"\"passwordSalt\": \"{Convert.ToBase64String(credential.Salt)}\",");
        System.Console.WriteLine($"\"passwordIterations\": {credential.Iterations.ToString(CultureInfo.InvariantCulture)}");
        return 0;
    }

    private static async Task<int> ServeAsync(string? configPath)
    {
        var (config, directory, errors) = LoadConfig(configPath);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                System.Console.Error.WriteLine($"Invalid: {error}");
            }

            return 2;
        }

        var options = ConfigLoader.ToServerOptions(config, directory);
        using var stop = new CancellationTokenSource();
        System.Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stop.Cancel();
        });

        await using var server = new FtpServer(options);
        using var subscription = server.Subscribe(Log);
        await server.StartAsync().ConfigureAwait(false);
        WriteLine($"{FtpServer.ServerName} listening on {server.LocalEndPoint}, passive ports {options.PassivePortMin} to {options.PassivePortMax}, TLS {(server.TlsAvailable ? "available" : "off")}");

        try
        {
            await Task.Delay(Timeout.Infinite, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        WriteLine("Stopping.");
        await server.StopAsync().ConfigureAwait(false);
        WriteLine("Stopped.");
        return 0;
    }

    private static void Log(ServerEvent e)
    {
        var who = e.UserName is null ? string.Empty : $" {e.UserName}";
        System.Console.Out.WriteLine(
            $"{e.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff} [{e.SessionId}]{who} {e.Kind}: {e.Describe()}");
    }

    private static void WriteLine(string text) =>
        System.Console.Out.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {text}");
}
