using System.Globalization;
using System.Net;

namespace ModernFTP.Config.Import;

public sealed record ImportResult(ModernFtpConfig Config, IReadOnlyList<string> Warnings)
{
    public int DirectoryCount => Config.Users.Sum(u => u.Directories.Count);
}

/// <summary>
/// Reads the original TYPSoft FTP Server config.ini and users.ini and produces a ModernFTP config.
/// Passwords are never migrated; every imported user needs a new password before it can be enabled.
/// </summary>
public static class TypsoftImporter
{
    private const char Reserved = '_';

    public static ImportResult Import(string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceDirectory);
        var configPath = Path.Combine(sourceDirectory, "config.ini");
        var usersPath = Path.Combine(sourceDirectory, "users.ini");
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"config.ini not found in {sourceDirectory}", configPath);
        }

        if (!File.Exists(usersPath))
        {
            throw new FileNotFoundException($"users.ini not found in {sourceDirectory}", usersPath);
        }

        var configIni = IniFile.Parse(configPath);
        var usersIni = IniFile.Parse(usersPath);
        var warnings = new List<string>();
        var config = new ModernFtpConfig();

        ImportSettings(configIni, configPath, sourceDirectory, config, warnings);
        ImportUsers(usersIni, usersPath, config, warnings);
        return new ImportResult(config, warnings);
    }

    private static void ImportSettings(IniFile ini, string path, string sourceDirectory, ModernFtpConfig config, List<string> warnings)
    {
        var setup = ini.Find("Setup");
        if (setup is not null)
        {
            var low = 0;
            var high = 0;
            foreach (var entry in setup.Entries)
            {
                switch (entry.Key.ToUpperInvariant())
                {
                    case "PORT":
                        config.Port = ParseInt(entry, path, 1, 65535);
                        break;
                    case "MAXUSER":
                        config.MaxConnections = ParseInt(entry, path, 0, int.MaxValue);
                        break;
                    case "TIME-OUT":
                        // The original stores minutes (per user time-out is minutes, maximum 600).
                        config.IdleTimeoutSeconds = ParseInt(entry, path, 0, 600) * 60;
                        break;
                    case "LOWPASV":
                        low = ParseInt(entry, path, 0, 65535);
                        break;
                    case "HIGHPASV":
                        high = ParseInt(entry, path, 0, 65535);
                        break;
                    case "PASVIP":
                        if (entry.Value.Length > 0)
                        {
                            if (!IPAddress.TryParse(entry.Value, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                warnings.Add($"Setup PASVIP '{entry.Value}' is not an IPv4 address; not imported.");
                            }
                            else
                            {
                                config.PassivePublicAddress = entry.Value;
                            }
                        }

                        break;
                    case "HIDEVERSION":
                        config.HideServerName = ParseBool(entry, path);
                        break;
                    case "ENTERMESSAGE":
                        ImportMessage(entry, sourceDirectory, "welcome", text => config.WelcomeMessage = text, warnings);
                        break;
                    case "EXITMESSAGE":
                        ImportMessage(entry, sourceDirectory, "goodbye", text => config.GoodbyeMessage = text, warnings);
                        break;
                    case "DISABLEEXITMESSAGE":
                        if (ParseBool(entry, path))
                        {
                            warnings.Add("Setup DisableExitMessage: ModernFTP always sends a goodbye reply; the default text is kept.");
                        }

                        break;
                    default:
                        warnings.Add($"Setup {entry.Key}: no ModernFTP equivalent; ignored.");
                        break;
                }
            }

            if (low != 0 || high != 0)
            {
                if (low < 1 || high < low)
                {
                    warnings.Add($"Setup LowPASV/HighPASV ({low} to {high}) is not a usable range; ModernFTP defaults kept.");
                }
                else
                {
                    config.PassivePortMin = low;
                    config.PassivePortMax = high;
                }
            }
        }

        var deny = ini.Find("IP Deny");
        if (deny is not null)
        {
            foreach (var entry in deny.Entries.Where(e => e.Value.Length > 0))
            {
                if (ModernFTP.Engine.BanList.IsValidEntry(entry.Value))
                {
                    config.BannedAddresses.Add(entry.Value);
                }
                else
                {
                    warnings.Add($"IP Deny {entry.Key} '{entry.Value}' is not an IP address or CIDR range; not imported.");
                }
            }
        }

        var allow = ini.Find("IP Allow");
        if (allow is not null && allow.Entries.Any(e => e.Value.Length > 0))
        {
            warnings.Add("IP Allow: ModernFTP has no allow list; the list was not imported.");
        }

        foreach (var section in ini.Sections)
        {
            if (section.Name.ToUpperInvariant() is "WINDOW" or "COLOR" or "FONT" or "SOUND")
            {
                warnings.Add($"[{section.Name}] section: the original's display settings have no ModernFTP equivalent; ignored.");
            }
            else if (section.Name.ToUpperInvariant() is not ("SETUP" or "IP DENY" or "IP ALLOW"))
            {
                warnings.Add($"[{section.Name}] section in config.ini is unknown; ignored.");
            }
        }
    }

    private static void ImportMessage(IniEntry entry, string sourceDirectory, string label, Action<string> assign, List<string> warnings)
    {
        if (entry.Value.Length == 0)
        {
            return;
        }

        string text;
        try
        {
            var file = entry.Value.Replace('\\', Path.DirectorySeparatorChar);
            text = File.ReadAllText(Path.GetFullPath(file, sourceDirectory)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            warnings.Add($"Setup {entry.Key}: could not read the {label} message file '{entry.Value}'; default text kept.");
            return;
        }

        if (text.Length > 0)
        {
            assign(text);
        }
    }

    private static void ImportUsers(IniFile ini, string path, ModernFtpConfig config, List<string> warnings)
    {
        foreach (var section in ini.Sections)
        {
            var name = section.Name;
            var label = $"User {name}";
            var isAnonymous = string.Equals(name, "anonymous", StringComparison.OrdinalIgnoreCase);
            var user = new UserConfig { Username = name, Enabled = false };
            var disabledInSource = false;
            string? homePath = null;
            var rules = new List<(DirectoryConfig Dir, int Line)>();

            foreach (var entry in section.Entries)
            {
                var key = entry.Key;
                if (key.StartsWith("Dir", StringComparison.OrdinalIgnoreCase) && int.TryParse(key.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    rules.Add((ParseDirectory(entry, path, label, warnings), entry.Line));
                    continue;
                }

                switch (key.ToUpperInvariant())
                {
                    case "HOMEPATH":
                        homePath = entry.Value;
                        break;
                    case "USERDISABLE":
                        disabledInSource = ParseBool(entry, path);
                        break;
                    case "MAXSPEED":
                        user.DownloadRateKBps = ParseInt(entry, path, 0, int.MaxValue);
                        break;
                    case "MAXUSER":
                        user.MaxConnections = ParseInt(entry, path, 0, int.MaxValue);
                        break;
                    case "MAXUSERIP":
                        user.MaxConnectionsPerIp = ParseInt(entry, path, 0, int.MaxValue);
                        break;
                    case "TIME-OUT":
                        // Minutes in the original, maximum 600.
                        user.IdleTimeoutSeconds = ParseInt(entry, path, 0, 600) * 60;
                        break;
                    case "PASSWORD":
                        break;
                    default:
                        warnings.Add($"{label}: {key} has no ModernFTP equivalent; ignored.");
                        break;
                }
            }

            BuildDirectories(user, homePath, rules, label, warnings);

            if (isAnonymous)
            {
                if (!disabledInSource)
                {
                    user.Enabled = true;
                    config.AllowAnonymous = true;
                    warnings.Add("Anonymous login is enabled in the source and was imported as enabled. ModernFTP ships with anonymous off and recommends keeping it off.");
                }
            }
            else
            {
                warnings.Add($"{label}: set a password and enable the account");
            }

            config.Users.Add(user);
        }
    }

    private static void BuildDirectories(UserConfig user, string? homePath, List<(DirectoryConfig Dir, int Line)> rules, string label, List<string> warnings)
    {
        DirectoryConfig? home = null;
        if (!string.IsNullOrWhiteSpace(homePath))
        {
            var match = rules.Find(r => r.Dir.Alias is null && SamePath(r.Dir.Path, homePath));
            if (match.Dir is not null)
            {
                home = match.Dir;
                rules.Remove(match);
                home.Path = homePath;
            }
            else
            {
                home = new DirectoryConfig { Path = homePath };
                warnings.Add($"{label}: no rights rule matches the home directory '{homePath}'; it was imported read only. Review it.");
            }

            user.Directories.Add(home);
        }
        else
        {
            warnings.Add($"{label}: no HomePath; the account has no home directory until you set one.");
        }

        foreach (var (dir, _) in rules)
        {
            if (dir.Alias is null)
            {
                warnings.Add($"{label}: rule for '{dir.Path}' applies inside another directory; ModernFTP does not enforce per directory rules yet.");
            }
            else
            {
                warnings.Add($"{label}: virtual link '{dir.Alias}' to '{dir.Path}' was imported but ModernFTP does not mount links yet.");
            }

            user.Directories.Add(dir);
        }
    }

    private static DirectoryConfig ParseDirectory(IniEntry entry, string path, string label, List<string> warnings)
    {
        var parts = entry.Value.Split('|');
        if (parts.Length < 2 || parts[0].Length == 0)
        {
            throw new InvalidDataException($"{path} line {entry.Line}: {entry.Key} must look like path|rights|link name.");
        }

        var rights = parts[1].Trim();
        var dir = new DirectoryConfig { Path = parts[0].Trim() };
        bool Has(int position, char letter)
        {
            if (position > rights.Length)
            {
                return false;
            }

            var c = char.ToUpperInvariant(rights[position - 1]);
            if (c == letter)
            {
                return true;
            }

            if (c != Reserved)
            {
                warnings.Add($"{label}: {entry.Key} rights position {position} has unexpected character '{rights[position - 1]}'; treated as not granted.");
            }

            return false;
        }

        var noAccess = Has(3, 'A');
        dir.IncludeSubdirectories = Has(9, 'S');
        var link = Has(10, 'V');
        if (link)
        {
            var alias = parts.Length > 2 ? parts[2].Trim() : string.Empty;
            if (alias.Length == 0)
            {
                warnings.Add($"{label}: {entry.Key} is flagged as a virtual link but has no link name; treated as a normal rule.");
            }
            else
            {
                dir.Alias = alias;
            }
        }

        if (noAccess)
        {
            dir.Permissions = new PermissionsConfig { Download = false, List = false };
            return dir;
        }

        dir.Permissions = new PermissionsConfig
        {
            Download = Has(1, 'D'),
            Upload = Has(2, 'U'),
            Delete = Has(4, 'E'),
            MakeDir = Has(5, 'M'),
            RemoveDir = Has(6, 'R'),
            Rename = Has(7, 'Y'),
            List = true,
        };
        return dir;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('\\', '/'), b.Trim().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static int ParseInt(IniEntry entry, string path, int min, int max)
    {
        if (!int.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
        {
            throw new InvalidDataException($"{path} line {entry.Line}: {entry.Key} must be a number from {min} to {max}.");
        }

        return value;
    }

    private static bool ParseBool(IniEntry entry, string path) => entry.Value switch
    {
        "0" or "" => false,
        "1" => true,
        _ => throw new InvalidDataException($"{path} line {entry.Line}: {entry.Key} must be 0 or 1."),
    };
}
