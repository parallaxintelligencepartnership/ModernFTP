using System.Net;
using ModernFTP.Config;

namespace ModernFTP.Engine.Tests;

/// <summary>Saved settings reach the running server through <see cref="ConfigApply.ApplyConfig"/>.</summary>
public sealed class ApplyConfigTests : IAsyncDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("modernftp-apply-").FullName;
    private FtpServer? _server;

    [Fact]
    public async Task DisablingALoggedInUserDisconnectsItWith421AndRefusesTheNextLogin()
    {
        var config = NewConfig();
        await StartAsync(config);
        await using var client = await LoginAsync("alice", "pw-alice");

        config.Users.Single(u => u.Username == "alice").Enabled = false;
        Assert.Empty(_server!.ApplyConfig(config, _directory));

        var reply = await client.ReadReplyAsync();
        Assert.Equal(421, reply.Code);
        Assert.Null(await client.ReadLineOrNullAsync(TimeSpan.FromSeconds(5)));

        await using var again = await ConnectAsync();
        Assert.Equal(331, (await again.SendAsync("USER alice")).Code);
        Assert.Equal(530, (await again.SendAsync("PASS pw-alice")).Code);
    }

    [Fact]
    public async Task RemovingAUserDisconnectsItAndOtherUsersStay()
    {
        var config = NewConfig();
        await StartAsync(config);
        await using var alice = await LoginAsync("alice", "pw-alice");
        await using var bob = await LoginAsync("bob", "pw-bob");

        config.Users.RemoveAll(u => u.Username == "bob");
        Assert.Empty(_server!.ApplyConfig(config, _directory));

        Assert.Equal(421, (await bob.ReadReplyAsync()).Code);
        Assert.Equal(257, (await alice.SendAsync("PWD")).Code);
    }

    [Fact]
    public async Task PasswordAndPermissionChangesApplyWithoutARestart()
    {
        var config = NewConfig();
        await StartAsync(config);
        await using var alice = await LoginAsync("alice", "pw-alice");
        Assert.Equal(257, (await alice.SendAsync("MKD first")).Code);

        var user = config.Users.Single(u => u.Username == "alice");
        user.Permissions.MakeDir = false;
        user.Password = "changed";
        config.Users.Add(new UserConfig { Username = "carol", Password = "pw-carol", HomeDirectory = "home" });
        Assert.Empty(_server!.ApplyConfig(config, _directory));

        Assert.Equal(550, (await alice.SendAsync("MKD second")).Code);
        await using var carol = await LoginAsync("carol", "pw-carol");
        await using var stale = await ConnectAsync();
        Assert.Equal(331, (await stale.SendAsync("USER alice")).Code);
        Assert.Equal(530, (await stale.SendAsync("PASS pw-alice")).Code);
        await using var fresh = await LoginAsync("alice", "changed");
    }

    [Fact]
    public async Task AddingABanRefusesTheNextConnection()
    {
        var config = NewConfig();
        await StartAsync(config);
        await using (var before = await ConnectAsync())
        {
        }

        config.BannedAddresses = ["127.0.0.1"];
        Assert.Empty(_server!.ApplyConfig(config, _directory));
        await using var refused = await FtpTestClient.ConnectAsync(_server!.LocalEndPoint!.Port);
        Assert.Equal(421, (await refused.ReadReplyAsync()).Code);

        config.BannedAddresses = [];
        Assert.Empty(_server!.ApplyConfig(config, _directory));
        await using var allowed = await ConnectAsync();
    }

    [Fact]
    public async Task MessagesAndLimitsApplyAndNetworkChangesAreListedForRestart()
    {
        var config = NewConfig();
        await StartAsync(config);

        config.WelcomeMessage = "Hello again.";
        config.IdleTimeoutSeconds = 77;
        config.Port += 1;
        config.PassivePortMax += 1;
        config.ListenAddress = "0.0.0.0";
        var restart = _server!.ApplyConfig(config, _directory);

        Assert.Equal([RestartSetting.BindAddress, RestartSetting.Port, RestartSetting.PassivePortRange], restart);
        Assert.Equal(TimeSpan.FromSeconds(77), _server!.Options.IdleTimeout);
        Assert.NotEqual(config.Port, _server.Options.Port);
        await using var client = await FtpTestClient.ConnectAsync(_server!.LocalEndPoint!.Port);
        Assert.Contains("Hello again.", (await client.ReadReplyAsync()).Text, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ModernFtpConfig NewConfig()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "home"));
        var passive = TestServer.PickPassiveBase(20000, 30000, 10);
        return new ModernFtpConfig
        {
            ListenAddress = "127.0.0.1",
            Port = FreePort(),
            PassivePortMin = passive,
            PassivePortMax = passive + 9,
            AllowPlaintextPasswords = true,
            MaxConnectionsPerIp = 0,
            MaxConnectionsPerUser = 0,
            MaxUnauthenticatedPerIp = 0,
            Tls = { Enabled = false },
            Users =
            [
                new UserConfig { Username = "alice", Password = "pw-alice", HomeDirectory = "home", Permissions = { MakeDir = true, List = true } },
                new UserConfig { Username = "bob", Password = "pw-bob", HomeDirectory = "home" },
            ],
        };
    }

    private async Task StartAsync(ModernFtpConfig config)
    {
        var options = ConfigLoader.ToServerOptions(config, _directory);
        options.FailedLoginDelay = TimeSpan.Zero;
        _server = new FtpServer(options);
        await _server.StartAsync();
    }

    private async Task<FtpTestClient> ConnectAsync()
    {
        var client = await FtpTestClient.ConnectAsync(_server!.LocalEndPoint!.Port);
        Assert.Equal(220, (await client.ReadReplyAsync()).Code);
        return client;
    }

    private async Task<FtpTestClient> LoginAsync(string user, string password)
    {
        var client = await ConnectAsync();
        Assert.Equal(331, (await client.SendAsync($"USER {user}")).Code);
        Assert.Equal(230, (await client.SendAsync($"PASS {password}")).Code);
        return client;
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
