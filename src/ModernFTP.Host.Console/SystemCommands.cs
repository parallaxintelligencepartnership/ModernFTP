using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using ModernFTP.Config;

namespace ModernFTP.Host.Console;

/// <summary>Launches a process and returns its exit code and combined output. Tests replace it so nothing runs.</summary>
internal interface IProcessLauncher
{
    int Run(string fileName, string arguments, out string output);
}

internal interface IElevationCheck
{
    bool IsElevated();
}

internal sealed class ProcessLauncher : IProcessLauncher
{
    public int Run(string fileName, string arguments, out string output)
    {
        var info = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(info) ?? throw new IOException($"Could not start {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        output = (stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()).Trim();
        return process.ExitCode;
    }
}

internal sealed class WindowsElevationCheck : IElevationCheck
{
    public bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>The parsed form of a "service" or "firewall" command line.</summary>
internal sealed record SystemCommand(string Group, string Action, string? ConfigPath, string? ProgramPath);

/// <summary>
/// "modernftp-cli service install|uninstall|start|stop|status" (sc.exe) and
/// "modernftp-cli firewall add|remove" (netsh advfirewall).
/// </summary>
internal static class SystemCommands
{
    public const int ExitUsage = 1;
    public const int ExitFailed = 2;
    public const int ExitNotAllowed = 4;

    public const string ServiceName = "ModernFTP";
    public const string ServiceExeName = "modernftp-service.exe";
    public const string ServiceDescription = "ModernFTP FTP and FTPS server. Serves the folders and users in the machine wide config.";
    public const string ServiceAccount = @"NT AUTHORITY\NetworkService";
    public const string ControlRuleName = "ModernFTP control";
    public const string PassiveRuleName = "ModernFTP passive";

    private static readonly string[] ServiceActions = ["install", "uninstall", "start", "stop", "status"];
    private static readonly string[] FirewallActions = ["add", "remove"];

    public static bool TryParse(string[] args, out SystemCommand? command, out string? error)
    {
        command = null;
        error = null;
        if (args.Length < 2 || args[0] is not ("service" or "firewall"))
        {
            error = "Missing action.";
            return false;
        }

        var group = args[0];
        var action = args[1];
        if (!(group == "service" ? ServiceActions : FirewallActions).Contains(action))
        {
            error = $"Unknown {group} action: {action}";
            return false;
        }

        string? config = null;
        string? program = null;
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] is "--config" or "-c" && i + 1 < args.Length)
            {
                config = args[++i];
            }
            else if (args[i] == "--program" && group == "firewall" && action == "add" && i + 1 < args.Length)
            {
                program = args[++i];
            }
            else
            {
                error = $"Unknown argument: {args[i]}";
                return false;
            }
        }

        if (config is not null && !(group == "service" && action == "install") && !(group == "firewall" && action == "add"))
        {
            error = "--config is only used by service install and firewall add.";
            return false;
        }

        command = new SystemCommand(group, action, config, program);
        return true;
    }

    public static string ServiceExePath(string consoleDirectory) => Path.Combine(consoleDirectory, ServiceExeName);

    /// <summary>sc.exe arguments for "service install". The space after each "=" is required by sc.exe.</summary>
    public static string ScCreateArguments(string serviceExePath, string? configPath)
    {
        var binPath = $"\\\"{serviceExePath}\\\"";
        if (configPath is not null)
        {
            binPath += $" --config \\\"{configPath}\\\"";
        }

        return $"create {ServiceName} binPath= \"{binPath}\" start= auto obj= \"{ServiceAccount}\" DisplayName= \"{ServiceName}\"";
    }

    public static string ScDescriptionArguments() => $"description {ServiceName} \"{ServiceDescription}\"";

    public static string ScSimpleArguments(string action) => action switch
    {
        "uninstall" => $"delete {ServiceName}",
        "start" => $"start {ServiceName}",
        "stop" => $"stop {ServiceName}",
        "status" => $"query {ServiceName}",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public static string NetshControlAddArguments(int port, string programPath) =>
        $"advfirewall firewall add rule name=\"{ControlRuleName}\" dir=in action=allow protocol=TCP localport={port.ToString(CultureInfo.InvariantCulture)} program=\"{programPath}\" enable=yes";

    public static string NetshPassiveAddArguments(int min, int max) =>
        $"advfirewall firewall add rule name=\"{PassiveRuleName}\" dir=in action=allow protocol=TCP localport={min.ToString(CultureInfo.InvariantCulture)}-{max.ToString(CultureInfo.InvariantCulture)} enable=yes";

    public static string NetshDeleteArguments(string ruleName) =>
        $"advfirewall firewall delete rule name=\"{ruleName}\"";

    public static int Run(
        string[] args,
        IProcessLauncher launcher,
        IElevationCheck elevation,
        bool isWindows,
        string consoleDirectory,
        TextWriter output,
        TextWriter error)
    {
        if (!TryParse(args, out var command, out var parseError))
        {
            error.WriteLine(parseError);
            return ExitUsage;
        }

        if (!isWindows)
        {
            error.WriteLine("Windows only");
            return ExitNotAllowed;
        }

        if (!elevation.IsElevated())
        {
            error.WriteLine("Run this from an elevated prompt");
            return ExitNotAllowed;
        }

        return command!.Group == "service"
            ? RunService(command, launcher, consoleDirectory, output, error)
            : RunFirewall(command, launcher, consoleDirectory, output, error);
    }

    private static int RunService(SystemCommand command, IProcessLauncher launcher, string consoleDirectory, TextWriter output, TextWriter error)
    {
        if (command.Action == "install")
        {
            var exe = ServiceExePath(consoleDirectory);
            var configPath = command.ConfigPath is null ? null : Path.GetFullPath(command.ConfigPath);
            var code = Exec(launcher, "sc.exe", ScCreateArguments(exe, configPath), output, error);
            if (code != 0)
            {
                return code;
            }

            return Exec(launcher, "sc.exe", ScDescriptionArguments(), output, error);
        }

        return Exec(launcher, "sc.exe", ScSimpleArguments(command.Action), output, error);
    }

    private static int RunFirewall(SystemCommand command, IProcessLauncher launcher, string consoleDirectory, TextWriter output, TextWriter error)
    {
        if (command.Action == "remove")
        {
            var first = Exec(launcher, "netsh.exe", NetshDeleteArguments(ControlRuleName), output, error);
            var second = Exec(launcher, "netsh.exe", NetshDeleteArguments(PassiveRuleName), output, error);
            return first != 0 ? first : second;
        }

        var path = Path.GetFullPath(command.ConfigPath ?? ConfigPaths.DefaultConfigPath());
        if (!File.Exists(path))
        {
            error.WriteLine($"Config file not found: {path}");
            return ExitFailed;
        }

        var config = ConfigLoader.Load(path);
        var program = Path.GetFullPath(command.ProgramPath ?? ServiceExePath(consoleDirectory));
        var control = Exec(launcher, "netsh.exe", NetshControlAddArguments(config.Port, program), output, error);
        if (control != 0)
        {
            return control;
        }

        return Exec(launcher, "netsh.exe", NetshPassiveAddArguments(config.PassivePortMin, config.PassivePortMax), output, error);
    }

    private static int Exec(IProcessLauncher launcher, string fileName, string arguments, TextWriter output, TextWriter error)
    {
        var code = launcher.Run(fileName, arguments, out var text);
        if (text.Length > 0)
        {
            (code == 0 ? output : error).WriteLine(text);
        }

        return code == 0 ? 0 : ExitFailed;
    }
}
