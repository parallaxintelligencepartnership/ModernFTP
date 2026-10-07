using System.IO;
using System.Net;
using ModernFTP.Config;
using ModernFTP.Engine;

namespace ModernFTP.App;

/// <summary>What the Users page needs from the running server.</summary>
public interface ISessionController
{
    /// <summary>A snapshot of the open sessions. Empty when no server is running.</summary>
    IReadOnlyList<SessionInfo> Sessions { get; }

    /// <summary>Replies 421 and closes the session. False when no such session is open.</summary>
    bool Disconnect(long sessionId);

    /// <summary>Cancels the session's running transfer. False when there is none.</summary>
    bool AbortTransfer(long sessionId);

    /// <summary>
    /// Bans the address on the running server and saves it to bannedAddresses in config.json.
    /// False when no server is running. Throws <see cref="IOException"/> when the save fails; the ban stays in effect.
    /// </summary>
    bool BanIp(IPAddress address);
}

/// <summary>The engine's session API plus the config store for persisted bans.</summary>
public sealed class EngineSessionController(Func<FtpServer?> serverAccessor, ConfigStore store) : ISessionController
{
    public IReadOnlyList<SessionInfo> Sessions => serverAccessor()?.Sessions ?? [];

    public bool Disconnect(long sessionId) => serverAccessor()?.Disconnect(sessionId) == true;

    public bool AbortTransfer(long sessionId) => serverAccessor()?.AbortTransfer(sessionId) == true;

    public bool BanIp(IPAddress address)
    {
        var server = serverAccessor();
        if (server is null)
        {
            return false;
        }

        var entry = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        server.Options.BanList.Add(entry);
        var saved = File.Exists(store.Path) ? store.Load().BannedAddresses : [];
        if (!saved.Contains(entry, StringComparer.OrdinalIgnoreCase))
        {
            store.SaveBannedAddresses([.. saved, entry]);
        }

        return true;
    }
}
