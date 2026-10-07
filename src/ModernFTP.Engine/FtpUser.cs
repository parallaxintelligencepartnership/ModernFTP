namespace ModernFTP.Engine;

public sealed class FtpUser
{
    public required string UserName { get; init; }

    public required PasswordCredential Credential { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Absolute physical directory that the virtual root "/" maps to.</summary>
    public required string HomeDirectory { get; init; }

    public PermissionRules Permissions { get; init; } = new(FtpPermissions.ReadOnly);

    /// <summary>Download cap in KB/s (1 KB = 1024 bytes). 0 means unlimited.</summary>
    public int DownloadRateKBps { get; init; }

    /// <summary>
    /// Sessions this user may have open at once. Null uses the server's per user limit; when both are
    /// set the lower one applies. 0 means no limit of its own.
    /// </summary>
    public int? MaxConnections { get; init; }

    /// <summary>Sessions this user may have open from one address. Null or 0 means no limit of its own.</summary>
    public int? MaxConnectionsPerIp { get; init; }

    /// <summary>Idle timeout once this user is logged in. Null uses the server's; zero or negative disables.</summary>
    public TimeSpan? IdleTimeout { get; init; }
}
