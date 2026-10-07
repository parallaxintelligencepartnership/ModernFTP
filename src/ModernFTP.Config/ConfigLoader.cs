using System.Net;
using System.Text.Json;
using ModernFTP.Engine;

namespace ModernFTP.Config;

public static class ConfigLoader
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static ModernFtpConfig Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<ModernFtpConfig>(stream, JsonOptions)
            ?? throw new InvalidDataException("The config file is empty.");
    }

    public static ModernFtpConfig Parse(string json) =>
        JsonSerializer.Deserialize<ModernFtpConfig>(json, JsonOptions)
        ?? throw new InvalidDataException("The config is empty.");

    public static void Save(ModernFtpConfig config, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
    }

    /// <summary>Returns every problem found; an empty list means the config is usable.</summary>
    public static IReadOnlyList<string> Validate(ModernFtpConfig config, string configDirectory)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = new List<string>();
        if (!IPAddress.TryParse(config.ListenAddress, out _))
        {
            errors.Add($"listenAddress '{config.ListenAddress}' is not an IP address.");
        }

        if (config.Port is < 1 or > 65535)
        {
            errors.Add("port must be between 1 and 65535.");
        }

        if (config.PassivePortMin is < 1 or > 65535 || config.PassivePortMax is < 1 or > 65535 || config.PassivePortMin > config.PassivePortMax)
        {
            errors.Add("passivePortMin and passivePortMax must form a range inside 1 to 65535.");
        }

        if (config.PassivePublicAddress is { Length: > 0 } publicAddress
            && (!IPAddress.TryParse(publicAddress, out var parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
        {
            errors.Add("passivePublicAddress must be an IPv4 address.");
        }

        if (config.MaxConnections < 0 || config.MaxConnectionsPerIp < 0 || config.MaxConnectionsPerUser < 0)
        {
            errors.Add("connection limits must be 0 (unlimited) or more.");
        }

        if (config.IdleTimeoutSeconds < 0 || config.IdleTimeoutSeconds > 600 * 60)
        {
            errors.Add("idleTimeoutSeconds must be between 0 and 36000.");
        }

        foreach (var entry in config.BannedAddresses)
        {
            if (!BanList.IsValidEntry(entry))
            {
                errors.Add($"bannedAddresses entry '{entry}' is not an IP address or CIDR range.");
            }
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var user in config.Users)
        {
            var label = string.IsNullOrWhiteSpace(user.Username) ? "(unnamed user)" : user.Username;
            if (string.IsNullOrWhiteSpace(user.Username))
            {
                errors.Add("every user needs a username.");
            }
            else if (!names.Add(user.Username))
            {
                errors.Add($"user '{label}' is listed more than once.");
            }

            if (string.IsNullOrWhiteSpace(user.HomeDirectory))
            {
                errors.Add($"user '{label}' needs a homeDirectory.");
            }
            else if (!Directory.Exists(Path.GetFullPath(user.HomeDirectory, configDirectory)))
            {
                errors.Add($"user '{label}' homeDirectory does not exist.");
            }

            if (user.DownloadRateKBps < 0)
            {
                errors.Add($"user '{label}' downloadRateKBps must be 0 (unlimited) or more.");
            }

            if (IsAnonymous(user))
            {
                continue;
            }

            var hasHash = !string.IsNullOrEmpty(user.PasswordHash) || !string.IsNullOrEmpty(user.PasswordSalt);
            if (hasHash)
            {
                if (!IsBase64(user.PasswordHash) || !IsBase64(user.PasswordSalt) || user.PasswordIterations < 1)
                {
                    errors.Add($"user '{label}' needs a valid base64 passwordHash, passwordSalt and positive passwordIterations.");
                }
            }
            else if (user.Password is not null)
            {
                if (!config.AllowPlaintextPasswords)
                {
                    errors.Add($"user '{label}' has a plain password but allowPlaintextPasswords is false.");
                }
            }
            else
            {
                errors.Add($"user '{label}' has no password.");
            }
        }

        if (config.Tls.Enabled && !string.IsNullOrWhiteSpace(config.Tls.CertificatePath)
            && !File.Exists(Path.GetFullPath(config.Tls.CertificatePath, configDirectory)))
        {
            errors.Add("tls.certificatePath does not exist.");
        }

        return errors;
    }

    /// <summary>Builds engine options. Call <see cref="Validate"/> first; this throws on invalid input.</summary>
    public static FtpServerOptions ToServerOptions(ModernFtpConfig config, string configDirectory)
    {
        var errors = Validate(config, configDirectory);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }

        var banList = new BanList();
        foreach (var entry in config.BannedAddresses)
        {
            banList.Add(entry);
        }

        var users = new List<FtpUser>();
        foreach (var user in config.Users)
        {
            PasswordCredential credential;
            if (IsAnonymous(user))
            {
                if (!config.AllowAnonymous)
                {
                    continue;
                }

                credential = AnyPasswordCredential.Instance;
            }
            else if (!string.IsNullOrEmpty(user.PasswordHash))
            {
                credential = new Pbkdf2Credential(
                    Convert.FromBase64String(user.PasswordHash),
                    Convert.FromBase64String(user.PasswordSalt!),
                    user.PasswordIterations);
            }
            else
            {
                credential = new PlaintextCredential(user.Password!);
            }

            var p = user.Permissions;
            users.Add(new FtpUser
            {
                UserName = user.Username,
                Credential = credential,
                Enabled = user.Enabled,
                HomeDirectory = Path.GetFullPath(user.HomeDirectory, configDirectory),
                DownloadRateKBps = user.DownloadRateKBps,
                Permissions = new PermissionRules(new FtpPermissions(
                    p.Download, p.Upload, p.Delete, p.RenameFile, p.RenameDir, p.MakeDir, p.List)),
            });
        }

        return new FtpServerOptions
        {
            ListenAddress = IPAddress.Parse(config.ListenAddress),
            Port = config.Port,
            PassivePortMin = config.PassivePortMin,
            PassivePortMax = config.PassivePortMax,
            PassivePublicAddress = string.IsNullOrWhiteSpace(config.PassivePublicAddress) ? null : IPAddress.Parse(config.PassivePublicAddress),
            AllowActiveMode = config.AllowActiveMode,
            MaxConnections = config.MaxConnections,
            MaxConnectionsPerUser = config.MaxConnectionsPerUser,
            MaxConnectionsPerIp = config.MaxConnectionsPerIp,
            IdleTimeout = TimeSpan.FromSeconds(config.IdleTimeoutSeconds),
            WelcomeMessage = config.WelcomeMessage,
            GoodbyeMessage = config.GoodbyeMessage,
            HideServerName = config.HideServerName,
            Certificate = config.Tls.Enabled ? CertificateProvider.LoadOrCreate(config.Tls, configDirectory) : null,
            Users = users,
            BanList = banList,
        };
    }

    private static bool IsAnonymous(UserConfig user) =>
        string.Equals(user.Username, "anonymous", StringComparison.OrdinalIgnoreCase);

    private static bool IsBase64(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var buffer = new byte[value.Length];
        return Convert.TryFromBase64String(value, buffer, out var written) && written > 0;
    }
}
