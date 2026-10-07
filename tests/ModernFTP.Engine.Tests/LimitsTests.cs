using System.Diagnostics;
using System.Net;

namespace ModernFTP.Engine.Tests;

public class LimitsTests
{
    [Fact]
    public async Task PerAddressLimitRejectsWith421()
    {
        await using var server = await TestServer.StartAsync(configure: o => o.MaxConnectionsPerIp = 1);
        await using var first = await server.ConnectAsync(login: false);
        await using var second = await FtpTestClient.ConnectAsync(server.Port);
        Assert.Equal(421, (await second.ReadReplyAsync()).Code);
        Assert.Null(await second.ReadLineOrNullAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(200, (await first.SendAsync("NOOP")).Code);
    }

    [Fact]
    public async Task TotalLimitRejectsWith421()
    {
        await using var server = await TestServer.StartAsync(configure: o => o.MaxConnections = 1);
        await using var first = await server.ConnectAsync(login: false);
        await using var second = await FtpTestClient.ConnectAsync(server.Port);
        Assert.Equal(421, (await second.ReadReplyAsync()).Code);
    }

    [Fact]
    public async Task SlotIsReleasedWhenSessionEnds()
    {
        await using var server = await TestServer.StartAsync(configure: o => o.MaxConnections = 1);
        await using (var first = await server.ConnectAsync(login: false))
        {
            await first.SendAsync("QUIT");
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (server.Server.SessionCount > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await using var again = await server.ConnectAsync(login: false);
        Assert.Equal(200, (await again.SendAsync("NOOP")).Code);
    }

    [Fact]
    public async Task PerUserLimitRejectsSecondLoginWith421()
    {
        await using var server = await TestServer.StartAsync(configure: o => o.MaxConnectionsPerUser = 1);
        await using var first = await server.ConnectAsync();
        await using var second = await server.ConnectAsync(login: false);
        await second.SendAsync($"USER {TestServer.UserName}");
        Assert.Equal(421, (await second.SendAsync($"PASS {TestServer.Password}")).Code);
    }

    [Fact]
    public async Task IdleSessionGets421()
    {
        await using var server = await TestServer.StartAsync(configure: o => o.IdleTimeout = TimeSpan.FromMilliseconds(600));
        await using var client = await server.ConnectAsync();
        var reply = await client.ReadReplyAsync();
        Assert.Equal(421, reply.Code);
        Assert.Contains("Idle", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionThatNeverLogsInIsClosedAtTheLoginTimeoutDespiteActivity()
    {
        await using var server = await TestServer.StartAsync(configure: o => o.LoginTimeout = TimeSpan.FromMilliseconds(1500));
        await using var client = await server.ConnectAsync(login: false);

        // Stay busy for most of the login window, then go quiet so no command of ours is unread when
        // the server closes (Windows resets such a connection and drops the 421 before it is read).
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromMilliseconds(900))
        {
            Assert.Equal(200, (await client.SendAsync("NOOP")).Code);
            await Task.Delay(150);
        }

        var reply = await client.ReadReplyAsync();
        Assert.Equal(421, reply.Code);
        Assert.Contains("Login timeout", reply.Text, StringComparison.Ordinal);
        Assert.Null(await client.ReadLineOrNullAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task LoggedInSessionOutlivesTheLoginTimeout()
    {
        await using var server = await TestServer.StartAsync(configure: o => o.LoginTimeout = TimeSpan.FromMilliseconds(300));
        await using var client = await server.ConnectAsync();
        await Task.Delay(800);
        Assert.Equal(200, (await client.SendAsync("NOOP")).Code);
    }

    [Fact]
    public async Task UnauthenticatedConnectionsPerAddressAreCapped()
    {
        await using var server = await TestServer.StartAsync(configure: o =>
        {
            o.MaxUnauthenticatedPerIp = 2;
            o.MaxConnectionsPerIp = 0;
        });
        await using var first = await server.ConnectAsync(login: false);
        await using var second = await server.ConnectAsync(login: false);
        await using (var third = await FtpTestClient.ConnectAsync(server.Port))
        {
            Assert.Equal(421, (await third.ReadReplyAsync()).Code);
        }

        // Once one of them logs in it no longer counts, so a new client gets in.
        Assert.Equal(331, (await first.SendAsync($"USER {TestServer.UserName}")).Code);
        Assert.Equal(230, (await first.SendAsync($"PASS {TestServer.Password}")).Code);
        await using var fourth = await server.ConnectAsync(login: false);
        Assert.Equal(200, (await fourth.SendAsync("NOOP")).Code);
    }

    [Fact]
    public async Task BannedAddressIsRefusedAtAccept()
    {
        await using var server = await TestServer.StartAsync(configure: o => o.BanList.Add("127.0.0.0/8"));
        await using var client = await FtpTestClient.ConnectAsync(server.Port);
        var reply = await client.ReadReplyAsync();
        Assert.Equal(421, reply.Code);
        Assert.Contains("denied", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadRateCapIsApplied()
    {
        await using var server = await TestServer.StartAsync(downloadRateKBps: 64);
        await File.WriteAllBytesAsync(Path.Combine(server.Home, "big.bin"), new byte[128 * 1024]);
        await using var client = await server.ConnectAsync();
        using var data = await client.OpenPassiveAsync();
        var watch = Stopwatch.StartNew();
        Assert.Equal(150, (await client.SendAsync("RETR big.bin")).Code);
        var buffer = new MemoryStream();
        await data.GetStream().CopyToAsync(buffer);
        watch.Stop();
        Assert.Equal(128 * 1024, buffer.Length);
        Assert.Equal(226, (await client.ReadReplyAsync()).Code);
        // 128 KB at 64 KB/s is two seconds; allow for the first chunk going out immediately.
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1.7), $"transfer took {watch.Elapsed}");
    }

    [Fact]
    public async Task ActiveModeCanBeDisabled()
    {
        await using var server = await TestServer.StartAsync(configure: o => o.AllowActiveMode = false);
        await using var client = await server.ConnectAsync();
        Assert.Equal(502, (await client.SendAsync("PORT 127,0,0,1,200,10")).Code);
        Assert.Equal(502, (await client.SendAsync("EPRT |1|127.0.0.1|51210|")).Code);
    }

    [Fact]
    public async Task PortToAnotherHostIsRefused()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync();
        Assert.Equal(501, (await client.SendAsync("PORT 10,0,0,1,200,10")).Code);
        Assert.Equal(501, (await client.SendAsync("PORT 127,0,0,1,0,21")).Code);
    }

    [Fact]
    public async Task PassiveReplyUsesPublicAddressOverride()
    {
        await using var server = await TestServer.StartAsync(configure: o => o.PassivePublicAddress = IPAddress.Parse("198.51.100.20"));
        await using var client = await server.ConnectAsync();
        var reply = await client.SendAsync("PASV");
        Assert.Equal(227, reply.Code);
        Assert.Contains("(198,51,100,20,", reply.Text, StringComparison.Ordinal);
    }
}
