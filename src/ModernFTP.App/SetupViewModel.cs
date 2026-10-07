using System.Globalization;
using System.IO;
using ModernFTP.Config;

namespace ModernFTP.App;

/// <summary>
/// Logic behind the FTP Setup window. It edits only the fields the window shows, directly on the loaded
/// config object, so settings the window does not show (login timeout and others) survive a save.
/// </summary>
public sealed class SetupViewModel
{
    private readonly ModernFtpConfig _config;
    private readonly AppSettings _settings;

    public SetupViewModel(ModernFtpConfig config, AppSettings settings)
    {
        _config = config;
        _settings = settings;
        ListenAddress = config.ListenAddress;
        Port = Num(config.Port);
        PassivePortMin = Num(config.PassivePortMin);
        PassivePortMax = Num(config.PassivePortMax);
        PassivePublicAddress = config.PassivePublicAddress ?? string.Empty;
        MaxConnections = Num(config.MaxConnections);
        MaxConnectionsPerIp = Num(config.MaxConnectionsPerIp);
        IdleTimeoutSeconds = Num(config.IdleTimeoutSeconds);
        WelcomeMessage = config.WelcomeMessage;
        GoodbyeMessage = config.GoodbyeMessage;
        HideServerName = config.HideServerName;
        StartMinimizedToTray = settings.StartMinimizedToTray;
        StartWithWindows = settings.StartWithWindows;
        StartServerOnLaunch = settings.StartServerOnLaunch || settings.StartWithWindows;
    }

    public string ListenAddress { get; set; }

    public string Port { get; set; }

    public string PassivePortMin { get; set; }

    public string PassivePortMax { get; set; }

    public string PassivePublicAddress { get; set; }

    public string MaxConnections { get; set; }

    public string MaxConnectionsPerIp { get; set; }

    public string IdleTimeoutSeconds { get; set; }

    public string WelcomeMessage { get; set; }

    public string GoodbyeMessage { get; set; }

    public bool HideServerName { get; set; }

    public bool StartMinimizedToTray { get; set; }

    public bool StartWithWindows { get; set; }

    /// <summary>Forced on at save when <see cref="StartWithWindows"/> is on: starting with Windows means serving.</summary>
    public bool StartServerOnLaunch { get; set; }

    /// <summary>The config object that Save writes; the same instance that was passed in.</summary>
    public ModernFtpConfig Config => _config;

    /// <summary>Which field failed to parse, for focusing. Null when there was no number error.</summary>
    public string? ErrorField { get; private set; }

    /// <summary>Copies the shown fields onto the config. Returns an error message, or null on success.</summary>
    public string? Apply()
    {
        ErrorField = null;
        if (!TryNumber(Port, nameof(Port), Strings.SetupPort, out var port)
            || !TryNumber(PassivePortMin, nameof(PassivePortMin), Strings.SetupPasvRange, out var pasvMin)
            || !TryNumber(PassivePortMax, nameof(PassivePortMax), Strings.SetupPasvRange, out var pasvMax)
            || !TryNumber(MaxConnections, nameof(MaxConnections), Strings.SetupMaxConnections, out var maxConn)
            || !TryNumber(MaxConnectionsPerIp, nameof(MaxConnectionsPerIp), Strings.SetupMaxPerIp, out var maxIp)
            || !TryNumber(IdleTimeoutSeconds, nameof(IdleTimeoutSeconds), Strings.SetupIdleTimeout, out var idle))
        {
            return NumberError;
        }

        _config.ListenAddress = ListenAddress.Trim();
        _config.Port = port;
        _config.PassivePortMin = pasvMin;
        _config.PassivePortMax = pasvMax;
        _config.PassivePublicAddress = string.IsNullOrWhiteSpace(PassivePublicAddress) ? null : PassivePublicAddress.Trim();
        _config.MaxConnections = maxConn;
        _config.MaxConnectionsPerIp = maxIp;
        _config.IdleTimeoutSeconds = idle;
        _config.WelcomeMessage = WelcomeMessage;
        _config.GoodbyeMessage = GoodbyeMessage;
        _config.HideServerName = HideServerName;
        return null;
    }

    /// <summary>Applies, validates and writes the config and app settings. Returns an error message or null.</summary>
    public string? Save(string configPath)
    {
        var error = Apply();
        if (error is not null)
        {
            return error;
        }

        var errors = ConfigLoader.Validate(_config, Path.GetDirectoryName(Path.GetFullPath(configPath))!);
        if (errors.Count > 0)
        {
            return errors[0];
        }

        ConfigLoader.Save(_config, configPath);
        _settings.StartMinimizedToTray = StartMinimizedToTray;
        _settings.StartWithWindows = StartWithWindows;
        _settings.StartServerOnLaunch = StartServerOnLaunch || StartWithWindows;
        return null;
    }

    private string? NumberError { get; set; }

    private bool TryNumber(string text, string field, string label, out int value)
    {
        if (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        ErrorField = field;
        NumberError = string.Format(CultureInfo.CurrentCulture, Strings.SetupNumberFormat, label.Replace("_", string.Empty, StringComparison.Ordinal));
        return false;
    }

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);
}
