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
}
