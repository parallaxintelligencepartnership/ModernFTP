namespace ModernFTP.Engine;

/// <summary>What a user may do inside a directory tree. Every handler checks one of these flags.</summary>
public sealed record FtpPermissions(
    bool Download,
    bool Upload,
    bool Delete,
    bool MakeDir,
    bool RemoveDir,
    bool Rename,
    bool List)
{
    public static FtpPermissions All { get; } = new(true, true, true, true, true, true, true);

    public static FtpPermissions ReadOnly { get; } = new(true, false, false, false, false, false, true);

    public static FtpPermissions None { get; } = new(false, false, false, false, false, false, false);
}

/// <summary>
/// Resolves the effective permissions for a virtual path. Phase 0 has a single rule for the
/// whole home directory; per subdirectory rules (longest prefix wins) slot in here later
/// without any handler changes, because handlers only ever call <see cref="Resolve"/>.
/// </summary>
public sealed class PermissionRules
{
    public PermissionRules(FtpPermissions home)
    {
        ArgumentNullException.ThrowIfNull(home);
        Home = home;
    }

    public FtpPermissions Home { get; }

    public FtpPermissions Resolve(VirtualPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Home;
    }
}
