using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace ModernFTP.Engine;

public sealed class FtpServerOptions
{
    public IPAddress ListenAddress { get; set; } = IPAddress.Any;

    /// <summary>Control port. 0 picks an ephemeral port (tests).</summary>
    public int Port { get; set; } = 21;

    public int PassivePortMin { get; set; } = 50000;

    public int PassivePortMax { get; set; } = 50100;

    /// <summary>Address advertised in PASV replies (for NAT). Null uses the control connection's local address.</summary>
    public IPAddress? PassivePublicAddress { get; set; }

    public bool AllowActiveMode { get; set; } = true;

    /// <summary>0 means unlimited for each of the three limits.</summary>
    public int MaxConnections { get; set; } = 100;

    public int MaxConnectionsPerUser { get; set; } = 5;

    public int MaxConnectionsPerIp { get; set; } = 10;

    /// <summary>Connections from one address that have not logged in yet. 0 means unlimited.</summary>
    public int MaxUnauthenticatedPerIp { get; set; } = 5;

    /// <summary>A session that has not logged in this long after connecting gets 421. Zero or negative disables.</summary>
    public TimeSpan LoginTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Zero or negative disables the idle timeout.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan DataConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan TlsHandshakeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan FailedLoginDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The session is closed with 421 after this many failed logins. 0 disables.</summary>
    public int MaxFailedLogins { get; set; } = 3;

    public string WelcomeMessage { get; set; } = "Welcome to ModernFTP.";

    public string GoodbyeMessage { get; set; } = "Goodbye.";

    public bool HideServerName { get; set; }

    /// <summary>Certificate with private key for AUTH TLS. Null disables FTPS.</summary>
    public X509Certificate2? Certificate { get; set; }

    public IReadOnlyList<FtpUser> Users { get; set; } = [];

    public BanList BanList { get; set; } = new();
}
