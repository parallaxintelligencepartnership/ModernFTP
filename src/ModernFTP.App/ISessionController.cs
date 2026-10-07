using System.Net;
using ModernFTP.Engine;

namespace ModernFTP.App;

/// <summary>
/// What the Users page buttons need from the engine. The engine currently has no public API to
/// disconnect a session or abort a transfer, so those two return false until it does.
/// </summary>
public interface ISessionController
{
    /// <summary>Closes the control connection of the session. Returns false when unsupported.</summary>
    bool Disconnect(long sessionId);

    /// <summary>Aborts the running transfer of the session. Returns false when unsupported.</summary>
    bool AbortTransfer(long sessionId);

    /// <summary>Adds the address to the running server's ban list. Returns false when no server is running.</summary>
    bool BanIp(IPAddress address);
}

/// <summary>Bridges to the engine where it can; stubs the rest.</summary>
public sealed class EngineSessionController(Func<FtpServer?> serverAccessor) : ISessionController
{
    // TODO(engine): replace with FtpServer session APIs once they exist.
    public bool Disconnect(long sessionId) => false;

    public bool AbortTransfer(long sessionId) => false;

    public bool BanIp(IPAddress address)
    {
        var server = serverAccessor();
        if (server is null)
        {
            return false;
        }

        server.Options.BanList.Add(address.ToString());
        return true;
    }
}
