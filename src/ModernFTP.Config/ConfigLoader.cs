using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModernFTP.Engine;

namespace ModernFTP.Config;

public static class ConfigLoader
{
    public const int MinHashBytes = 32;
    public const int MinSaltBytes = 16;
    public const int MinIterations = 100_000;

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

    /// <summary>
    /// Writes <paramref name="config"/> to <paramref name="path"/>. When the file exists, keys this version does not
    /// know (and the users' unknown keys) are kept: the new values are applied onto the old JSON tree. Comments
    /// cannot be kept by the JSON reader and are lost. The write goes to a temporary file in the same folder that
    /// then replaces the old one, with the old file's Unix permissions.
    /// </summary>
    public static void Save(ModernFtpConfig config, string path)
    {
        ArgumentNullException.ThrowIfNull(config);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var tree = JsonSerializer.SerializeToNode(config, JsonOptions)!;
        UnixFileMode? mode = null;
        if (File.Exists(full))
        {
            if (!OperatingSystem.IsWindows())
            {
                mode = File.GetUnixFileMode(full);
            }

            try
            {
                var old = JsonNode.Parse(
                    File.ReadAllText(full),
                    documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (old is JsonObject oldObject)
                {
                    MergeInto(oldObject, (JsonObject)tree);
                    tree = oldObject;
                }
            }
            catch (JsonException)
            {
                // The old file is unreadable; the new settings replace it.
            }
        }

        var temporary = full + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows() && mode is { } createMode)
        {
            options.UnixCreateMode = createMode;
        }

        using (var stream = new FileStream(temporary, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(tree.ToJsonString(JsonOptions));
        }

        if (!OperatingSystem.IsWindows() && mode is { } keep)
        {
            File.SetUnixFileMode(temporary, keep);
        }

        File.Move(temporary, full, overwrite: true);
    }

    /// <summary>Sets every property of <paramref name="source"/> on <paramref name="target"/>, keeping target's other properties.</summary>
    private static void MergeInto(JsonObject target, JsonObject source)
    {
        var properties = source.ToList();
        source.Clear();
        foreach (var (name, value) in properties)
        {
            var existingName = target.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;
            var existing = target[existingName];
            if (existing is JsonObject existingObject && value is JsonObject valueObject)
            {
                MergeInto(existingObject, valueObject);
            }
            else if (existing is JsonArray existingArray && value is JsonArray valueArray)
            {
                target[existingName] = MergeArray(existingArray, valueArray);
            }
            else
            {
                target[existingName] = value;
            }
        }
    }

    /// <summary>Object elements are matched to the old ones by username when they have one, otherwise by position.</summary>
    private static JsonArray MergeArray(JsonArray old, JsonArray next)
    {
        var oldItems = old.ToList();
        old.Clear();
        var nextItems = next.ToList();
        next.Clear();
        var used = new HashSet<int>();
        var result = new JsonArray();
        for (var i = 0; i < nextItems.Count; i++)
        {
            var item = nextItems[i];
            if (item is JsonObject itemObject)
            {
                var match = -1;
                if (UserName(itemObject) is { } userName)
                {
                    match = oldItems.FindIndex(o => o is JsonObject oo
                        && string.Equals(UserName(oo), userName, StringComparison.OrdinalIgnoreCase));
                }
                else if (i < oldItems.Count && oldItems[i] is JsonObject)
                {
                    match = i;
                }

                if (match >= 0 && used.Add(match))
                {
                    var oldObject = (JsonObject)oldItems[match]!;
                    MergeInto(oldObject, itemObject);
                    result.Add(oldObject);
                    continue;
                }
            }

            result.Add(item);
        }

        return result;
    }

    private static string? UserName(JsonObject item) =>
        item["username"] is JsonValue value && value.TryGetValue<string>(out var name) ? name : null;

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

        if (config.MaxConnections < 0 || config.MaxConnectionsPerIp < 0 || config.MaxConnectionsPerUser < 0 || config.MaxUnauthenticatedPerIp < 0)
        {
            errors.Add("connection limits must be 0 (unlimited) or more.");
        }

        if (config.IdleTimeoutSeconds < 0 || config.IdleTimeoutSeconds > 600 * 60)
        {
            errors.Add("idleTimeoutSeconds must be between 0 and 36000.");
        }

        if (config.LoginTimeoutSeconds < 0 || config.LoginTimeoutSeconds > 3600)
        {
            errors.Add("loginTimeoutSeconds must be between 0 and 3600.");
        }

        if (config.LoginFailureLimit is < 0 or > 1000)
        {
            errors.Add("loginFailureLimit must be between 0 and 1000.");
        }

        if (config.LoginFailureWindowMinutes is < 1 or > 10080 || config.LoginBanMinutes is < 1 or > 10080)
        {
            errors.Add("loginFailureWindowMinutes and loginBanMinutes must be between 1 and 10080.");
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

            var effectiveHome = user.EffectiveHome().Path;
            if (string.IsNullOrWhiteSpace(effectiveHome))
            {
                errors.Add($"user '{label}' needs a homeDirectory.");
            }
            // A home directory that does not exist is a warning, not an error: the engine logs it at start
            // and refuses that user's login until the folder exists.

            if (user.DownloadRateKBps < 0)
            {
                errors.Add($"user '{label}' downloadRateKBps must be 0 (unlimited) or more.");
            }

            if (user.MaxConnections < 0 || user.MaxConnectionsPerIp < 0)
            {
                errors.Add($"user '{label}' maxConnections and maxConnectionsPerIp must be 0 (unlimited) or more.");
            }

            if (user.IdleTimeoutSeconds is < 0 or > 600 * 60)
            {
                errors.Add($"user '{label}' idleTimeoutSeconds must be between 0 and 36000.");
            }

            if (IsAnonymous(user))
            {
                continue;
            }

            var hasHash = !string.IsNullOrEmpty(user.PasswordHash) || !string.IsNullOrEmpty(user.PasswordSalt);
            if (hasHash)
            {
                var hashBytes = Base64Length(user.PasswordHash);
                var saltBytes = Base64Length(user.PasswordSalt);
                if (hashBytes is null || saltBytes is null)
                {
                    errors.Add($"user '{label}' needs a valid base64 passwordHash and passwordSalt.");
                }
                else if (hashBytes < MinHashBytes || saltBytes < MinSaltBytes || user.PasswordIterations < MinIterations)
                {
                    errors.Add($"user '{label}' password parameters are too weak: passwordHash must be at least {MinHashBytes} bytes (it is {hashBytes}), passwordSalt at least {MinSaltBytes} bytes (it is {saltBytes}) and passwordIterations at least {MinIterations:N0} (it is {user.PasswordIterations:N0}). Create new values with \"modernftp-cli hash-password\".");
                }
            }
            else if (user.Password is not null)
            {
                if (!config.AllowPlaintextPasswords)
                {
                    errors.Add($"user '{label}' has a plain password but allowPlaintextPasswords is false.");
                }
            }
            else if (user.Enabled)
            {
                // Disabled users (imported ones arrive disabled) may wait for a password.
                errors.Add($"user '{label}' has no password.");
            }
        }

        if (config.Tls.Enabled && !string.IsNullOrWhiteSpace(config.Tls.CertificatePath))
        {
            var certificate = Path.GetFullPath(config.Tls.CertificatePath, configDirectory);
            if (!File.Exists(certificate))
            {
                errors.Add("tls.certificatePath does not exist.");
            }
            else
            {
                try
                {
                    X509CertificateLoader.LoadPkcs12FromFile(certificate, config.Tls.CertificatePassword).Dispose();
                }
                catch (CryptographicException ex)
                {
                    errors.Add($"tls.certificatePath is not a usable PFX file or tls.certificatePassword is wrong ({ex.Message}).");
                }
            }
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
            else if (user.Password is not null)
            {
                credential = new PlaintextCredential(user.Password);
            }
            else
            {
                credential = NoPasswordCredential.Instance;
            }

            var (homePath, p) = user.EffectiveHome();
            users.Add(new FtpUser
            {
                UserName = user.Username,
                Credential = credential,
                Enabled = user.Enabled,
                HomeDirectory = Path.GetFullPath(homePath, configDirectory),
                DownloadRateKBps = user.DownloadRateKBps,
                MaxConnections = user.MaxConnections,
                MaxConnectionsPerIp = user.MaxConnectionsPerIp,
                IdleTimeout = user.IdleTimeoutSeconds is { } idle ? TimeSpan.FromSeconds(idle) : null,
                Permissions = new PermissionRules(new FtpPermissions(
                    p.Download, p.Upload, p.Delete, p.MakeDir, p.RemoveDir, p.Rename, p.List)),
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
            MaxUnauthenticatedPerIp = config.MaxUnauthenticatedPerIp,
            LoginFailureLimit = config.LoginFailureLimit,
            LoginFailureWindow = TimeSpan.FromMinutes(config.LoginFailureWindowMinutes),
            LoginBanDuration = TimeSpan.FromMinutes(config.LoginBanMinutes),
            LoginTimeout = TimeSpan.FromSeconds(config.LoginTimeoutSeconds),
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

    /// <summary>Decoded length of a base64 string, or null when it is empty or not base64.</summary>
    private static int? Base64Length(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var buffer = new byte[value.Length];
        return Convert.TryFromBase64String(value, buffer, out var written) && written > 0 ? written : null;
    }

    /// <summary>A disabled user without a password yet: no password ever matches.</summary>
    private sealed class NoPasswordCredential : PasswordCredential
    {
        public static NoPasswordCredential Instance { get; } = new();

        public override bool Verify(string password) => false;
    }
}
