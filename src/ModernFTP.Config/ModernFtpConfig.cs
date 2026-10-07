namespace ModernFTP.Config;

/// <summary>On disk configuration (config.json). Every property has a safe default.</summary>
public sealed class ModernFtpConfig
{
    public string ListenAddress { get; set; } = "0.0.0.0";

    public int Port { get; set; } = 21;

    public int PassivePortMin { get; set; } = 50000;

    public int PassivePortMax { get; set; } = 50100;

    /// <summary>Address to advertise in PASV replies when the server sits behind NAT.</summary>
    public string? PassivePublicAddress { get; set; }

    public bool AllowActiveMode { get; set; } = true;

    public int MaxConnections { get; set; } = 100;

    public int MaxConnectionsPerUser { get; set; } = 5;

    public int MaxConnectionsPerIp { get; set; } = 10;

    public int IdleTimeoutSeconds { get; set; } = 300;

    public string WelcomeMessage { get; set; } = "Welcome to ModernFTP.";

    public string GoodbyeMessage { get; set; } = "Goodbye.";

    public bool HideServerName { get; set; }

    /// <summary>When false (the default) a user named "anonymous" is ignored.</summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>Development only: accept the plain "password" field on users.</summary>
    public bool AllowPlaintextPasswords { get; set; }

    public TlsConfig Tls { get; set; } = new();

    public List<UserConfig> Users { get; set; } = [];

    /// <summary>Addresses or CIDR ranges refused at connect time.</summary>
    public List<string> BannedAddresses { get; set; } = [];
}

public sealed class TlsConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>PFX file. When empty a self signed certificate is generated in the config directory.</summary>
    public string? CertificatePath { get; set; }

    public string? CertificatePassword { get; set; }
}

public sealed class UserConfig
{
    public string Username { get; set; } = string.Empty;

    /// <summary>Base64 PBKDF2 SHA256 hash. Create with "modernftp hash-password".</summary>
    public string? PasswordHash { get; set; }

    public string? PasswordSalt { get; set; }

    public int PasswordIterations { get; set; } = ModernFTP.Engine.Pbkdf2Credential.DefaultIterations;

    /// <summary>Plain text password, only honored when AllowPlaintextPasswords is true.</summary>
    public string? Password { get; set; }

    public bool Enabled { get; set; } = true;

    public string HomeDirectory { get; set; } = string.Empty;

    public int DownloadRateKBps { get; set; }

    public PermissionsConfig Permissions { get; set; } = new();

    /// <summary>
    /// Directory rules in the style of the original. The home entry (alias null, or the first entry when
    /// none has a null alias) overrides <see cref="HomeDirectory"/> and <see cref="Permissions"/>. Entries
    /// with an alias are virtual links shown at the root; the engine does not mount them yet.
    /// </summary>
    public List<DirectoryConfig> Directories { get; set; } = [];

    /// <summary>The home directory and permissions that feed the engine's home rule.</summary>
    public (string Path, PermissionsConfig Permissions) EffectiveHome()
    {
        if (Directories.Count == 0)
        {
            return (HomeDirectory, Permissions);
        }

        var home = Directories.Find(d => d.Alias is null) ?? Directories[0];
        return (home.Path, home.Permissions);
    }
}

public sealed class DirectoryConfig
{
    public string Path { get; set; } = string.Empty;

    /// <summary>Null means the home directory; otherwise a virtual link name shown at the root.</summary>
    public string? Alias { get; set; }

    public PermissionsConfig Permissions { get; set; } = new();

    public bool IncludeSubdirectories { get; set; } = true;
}

/// <summary>Defaults are read only: download and list.</summary>
public sealed class PermissionsConfig
{
    public bool Download { get; set; } = true;

    public bool Upload { get; set; }

    public bool Delete { get; set; }

    public bool MakeDir { get; set; }

    public bool RemoveDir { get; set; }

    public bool Rename { get; set; }

    public bool List { get; set; } = true;
}
