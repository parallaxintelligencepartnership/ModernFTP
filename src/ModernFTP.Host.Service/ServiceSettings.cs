using ModernFTP.Config;

namespace ModernFTP.Host.Service;

/// <summary>Where the service reads its config: --config, else %ProgramData%\ModernFTP\config.json.</summary>
internal sealed record ServiceSettings(string ConfigPath)
{
    public const string ServiceName = "ModernFTP";

    public static ServiceSettings FromArgs(string[] args)
    {
        string? path = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--config" or "-c")
            {
                path = args[i + 1];
            }
        }

        return new ServiceSettings(Path.GetFullPath(path ?? ConfigPaths.MachineConfigPath()));
    }
}
