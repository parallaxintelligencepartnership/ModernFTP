using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using ModernFTP.Config;
using ModernFTP.Engine;

namespace ModernFTP.Conformance;

/// <summary>
/// One in-process server for the whole class: 127.0.0.1, ephemeral control port, a passive range
/// three ports wide, a temp home, a full access user and an upload denied user, FTPS enabled.
/// </summary>
public sealed class ServerFixture : IAsyncLifetime
{
    public const string User = "alice";
    public const string Password = "wonder land";
    public const string ReaderUser = "reader";
    public const string ReaderPassword = "readonly";

    private readonly ConcurrentQueue<ServerEvent> _events = new();
    private IDisposable? _subscription;

    public FtpServer Server { get; private set; } = null!;

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "modernftp-conformance-" + Guid.NewGuid().ToString("N"));

    public string Home => Path.Combine(Root, "home");

    public string ReaderHome => Path.Combine(Root, "reader");

    public string Work => Path.Combine(Root, "work");

    public int PassiveMin { get; private set; }

    public int PassiveMax => PassiveMin + 2;

    public string Url => $"ftp://127.0.0.1:{Server.LocalEndPoint!.Port}/";

    public string UserPass => $"{User}:{Password}";

    public IReadOnlyCollection<ServerEvent> Events => _events;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(ReaderHome);
        Directory.CreateDirectory(Work);
        PassiveMin = Random.Shared.Next(41000, 59000);
        var certificate = X509CertificateLoader.LoadPkcs12(CertificateProvider.CreateSelfSignedPfx(), password: null);
        Server = new FtpServer(new FtpServerOptions
        {
            ListenAddress = IPAddress.Loopback,
            Port = 0,
            PassivePortMin = PassiveMin,
            PassivePortMax = PassiveMax,
            Certificate = certificate,
            MaxConnectionsPerIp = 0,
            MaxConnectionsPerUser = 0,
            Users =
            [
                new FtpUser
                {
                    UserName = User,
                    Credential = Pbkdf2Credential.Create(Password, iterations: 1000),
                    HomeDirectory = Home,
                    Permissions = new PermissionRules(FtpPermissions.All),
                },
                new FtpUser
                {
                    UserName = ReaderUser,
                    Credential = Pbkdf2Credential.Create(ReaderPassword, iterations: 1000),
                    HomeDirectory = ReaderHome,
                    Permissions = new PermissionRules(FtpPermissions.ReadOnly),
                },
            ],
        });
        _subscription = Server.Subscribe(_events.Enqueue);
        await Server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        _subscription?.Dispose();
        await Server.StopAsync(TimeSpan.FromSeconds(2));
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Events reach subscribers on a background task, so wait (with a deadline) until at least
    /// <paramref name="count"/> transfers of <paramref name="path"/> were reported, then return the last.
    /// </summary>
    public async Task<TransferStartedEvent> LastTransferStartedAsync(string path, int count = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var matches = _events.OfType<TransferStartedEvent>().Where(e => e.Path == path).ToList();
            if (matches.Count >= count || DateTime.UtcNow > deadline)
            {
                Assert.True(matches.Count >= count, $"expected {count} transfer events for {path}, saw {matches.Count}");
                return matches[^1];
            }

            await Task.Delay(20);
        }
    }

    public string WorkFile(string name) => Path.Combine(Work, name);

    public static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }
}
