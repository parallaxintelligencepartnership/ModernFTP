namespace ModernFTP.Engine.Tests;

/// <summary>Per user connection and idle limits, and how they combine with the server wide ones.</summary>
public class UserLimitTests
{
    private static Action<FtpServerOptions> WithUser(
        int? maxConnections = null, int? maxConnectionsPerIp = null, TimeSpan? idleTimeout = null, Action<FtpServerOptions>? more = null) => o =>
    {
        var u = o.Users[0];
        o.Users =
        [
            new FtpUser
            {
                UserName = u.UserName,
                Credential = u.Credential,
                HomeDirectory = u.HomeDirectory,
                Permissions = u.Permissions,
                MaxConnections = maxConnections,
                MaxConnectionsPerIp = maxConnectionsPerIp,
                IdleTimeout = idleTimeout,
            },
        ];
        more?.Invoke(o);
    };

    private static async Task<FtpReply> SecondLoginAsync(TestServer server)
    {
        var second = await server.ConnectAsync(login: false);
        await using (second)
        {
            Assert.Equal(331, (await second.SendAsync($"USER {TestServer.UserName}")).Code);
            return await second.SendAsync($"PASS {TestServer.Password}");
        }
    }

    [Fact]
    public async Task UserMaxConnectionsRefusesTheSecondLogin()
    {
        await using var server = await TestServer.StartAsync(configure: WithUser(maxConnections: 1, more: o => o.MaxConnectionsPerUser = 5));
        await using var first = await server.ConnectAsync();
        Assert.Equal(421, (await SecondLoginAsync(server)).Code);
    }

    [Fact]
    public async Task TheLowerOfServerAndUserLimitsApplies()
    {
        await using var server = await TestServer.StartAsync(configure: WithUser(maxConnections: 3, more: o => o.MaxConnectionsPerUser = 1));
        await using var first = await server.ConnectAsync();
        Assert.Equal(421, (await SecondLoginAsync(server)).Code);
    }

    [Fact]
    public async Task UserLimitAboveTheServerOneStillAllowsUpToTheServerOne()
    {
        await using var server = await TestServer.StartAsync(configure: WithUser(maxConnections: 5, more: o => o.MaxConnectionsPerUser = 2));
        await using var first = await server.ConnectAsync();
        await using var second = await server.ConnectAsync();
        Assert.Equal(421, (await SecondLoginAsync(server)).Code);
    }

    [Fact]
    public async Task UserMaxConnectionsPerIpRefusesTheSecondLoginFromTheSameAddress()
    {
        await using var server = await TestServer.StartAsync(configure: WithUser(maxConnectionsPerIp: 1, more: o => o.MaxConnectionsPerIp = 10));
        await using var first = await server.ConnectAsync();
        Assert.Equal(421, (await SecondLoginAsync(server)).Code);
    }

    [Fact]
    public async Task UserIdleTimeoutReplacesTheServerOneAfterLogin()
    {
        await using var server = await TestServer.StartAsync(configure: WithUser(idleTimeout: TimeSpan.FromMilliseconds(600), more: o => o.IdleTimeout = TimeSpan.Zero));
        await using var client = await server.ConnectAsync();
        var reply = await client.ReadReplyAsync();
        Assert.Equal(421, reply.Code);
        Assert.Contains("Idle", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserIdleTimeoutZeroDisablesTheServerOne()
    {
        await using var server = await TestServer.StartAsync(configure: WithUser(idleTimeout: TimeSpan.Zero, more: o => o.IdleTimeout = TimeSpan.FromMilliseconds(400)));
        await using var client = await server.ConnectAsync();
        await Task.Delay(1200);
        Assert.Equal(200, (await client.SendAsync("NOOP")).Code);
    }
}
