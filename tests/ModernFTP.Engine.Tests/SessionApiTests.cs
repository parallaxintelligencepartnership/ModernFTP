using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace ModernFTP.Engine.Tests;

/// <summary>The session API hosts use: snapshot, disconnect, abort, counters and progress events.</summary>
public class SessionApiTests
{
    [Fact]
    public async Task SnapshotShowsTheLoggedInSessionWithUserAndAddress()
    {
        await using var server = await TestServer.StartAsync();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await using var client = await server.ConnectAsync();
        var session = await WaitForAsync(server, s => s.User == TestServer.UserName);
        Assert.Equal(IPAddress.Loopback, session.RemoteAddress);
        Assert.InRange(session.ConnectedAt, before, DateTimeOffset.UtcNow);
        Assert.InRange(session.LastActivity, session.ConnectedAt, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Null(session.CurrentTransfer);
        Assert.Equal(0, session.BytesSent);
        Assert.Equal(1, server.Server.ActiveConnections);
    }

    [Fact]
    public async Task DisconnectSends421AndClosesTheSession()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync();
        var session = await WaitForAsync(server, s => s.User is not null);
        Assert.False(server.Server.Disconnect(session.Id + 1000));
        Assert.True(server.Server.Disconnect(session.Id));
        Assert.Equal(421, (await client.ReadReplyAsync()).Code);
        Assert.Null(await client.ReadLineOrNullAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ServerAbortDuringASlowRetrRepliesWith426OnlyAndTheSessionGoesOn()
    {
        await using var server = await TestServer.StartAsync(downloadRateKBps: 16);
        await File.WriteAllBytesAsync(Path.Combine(server.Home, "slow.bin"), new byte[1024 * 1024]);
        await using var client = await server.ConnectAsync();
        var session = await WaitForAsync(server, s => s.User is not null);
        Assert.False(server.Server.AbortTransfer(session.Id));
        Assert.False(server.Server.AbortTransfer(session.Id + 1000));

        using var data = await client.OpenPassiveAsync();
        Assert.Equal(150, (await client.SendAsync("RETR slow.bin")).Code);
        var running = await WaitForAsync(server, s => s.CurrentTransfer is { BytesDone: > 0 });
        Assert.Equal("/slow.bin", running.CurrentTransfer!.Path);
        Assert.Equal(TransferDirection.Download, running.CurrentTransfer.Direction);
        Assert.Equal(1024 * 1024, running.CurrentTransfer.TotalBytes);

        Assert.True(server.Server.AbortTransfer(session.Id));
        Assert.Equal(426, (await client.ReadReplyAsync()).Code);
        Assert.Equal(200, (await client.SendAsync("NOOP")).Code);
        Assert.Null((await WaitForAsync(server, s => s.CurrentTransfer is null)).CurrentTransfer);
    }

    [Fact]
    public async Task AfterAServerAbortTheNextCommandGetsExactlyOneReply()
    {
        await using var server = await TestServer.StartAsync(downloadRateKBps: 16);
        await File.WriteAllBytesAsync(Path.Combine(server.Home, "slow.bin"), new byte[1024 * 1024]);
        await using var client = await server.ConnectAsync();
        var session = await WaitForAsync(server, s => s.User is not null);
        using var data = await client.OpenPassiveAsync();
        Assert.Equal(150, (await client.SendAsync("RETR slow.bin")).Code);
        await WaitForAsync(server, s => s.CurrentTransfer is { BytesDone: > 0 });
        Assert.True(server.Server.AbortTransfer(session.Id));
        Assert.Equal(426, (await client.ReadReplyAsync()).Code);

        Assert.Equal(200, (await client.SendAsync("NOOP")).Code);
        Assert.Equal(257, (await client.SendAsync("PWD")).Code);
    }

    [Fact]
    public async Task CountersMatchTheBytesMoved()
    {
        await using var server = await TestServer.StartAsync();
        await File.WriteAllBytesAsync(Path.Combine(server.Home, "down.bin"), new byte[50_000]);
        await using var client = await server.ConnectAsync();

        using (var up = await client.OpenPassiveAsync())
        {
            Assert.Equal(150, (await client.SendAsync("STOR up.bin")).Code);
            await up.GetStream().WriteAsync(new byte[100_000]);
            up.Client.Shutdown(SocketShutdown.Send);
            Assert.Equal(226, (await client.ReadReplyAsync()).Code);
        }

        using (var down = await client.OpenPassiveAsync())
        {
            Assert.Equal(150, (await client.SendAsync("RETR down.bin")).Code);
            await down.GetStream().CopyToAsync(Stream.Null);
            Assert.Equal(226, (await client.ReadReplyAsync()).Code);
        }

        Assert.Equal(50_000, server.Server.TotalBytesSent);
        Assert.Equal(100_000, server.Server.TotalBytesReceived);
        var session = Assert.Single(server.Server.Sessions);
        Assert.Equal(50_000, session.BytesSent);
        Assert.Equal(100_000, session.BytesReceived);
    }

    [Fact]
    public async Task ProgressEventsArriveAtMostEvery500MsDuringA5MBTransfer()
    {
        const int size = 5 * 1024 * 1024;
        await using var server = await TestServer.StartAsync(downloadRateKBps: 2048);
        var progress = new ConcurrentQueue<TransferProgressEvent>();
        using var subscription = server.Server.Subscribe(e =>
        {
            if (e is TransferProgressEvent p)
            {
                progress.Enqueue(p);
            }
        });
        await File.WriteAllBytesAsync(Path.Combine(server.Home, "big.bin"), new byte[size]);
        await using var client = await server.ConnectAsync();
        using (var data = await client.OpenPassiveAsync())
        {
            Assert.Equal(150, (await client.SendAsync("RETR big.bin")).Code);
            await data.GetStream().CopyToAsync(Stream.Null);
            Assert.Equal(226, (await client.ReadReplyAsync()).Code);
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (progress.Count < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        var events = progress.ToArray();
        Assert.True(events.Length >= 3, $"expected at least 3 progress events, saw {events.Length}");
        Assert.All(events, e =>
        {
            Assert.Equal("/big.bin", e.Path);
            Assert.Equal(size, e.TotalBytes);
            Assert.InRange(e.BytesDone, 1, size);
        });
        for (var i = 1; i < events.Length; i++)
        {
            Assert.True(events[i].BytesDone > events[i - 1].BytesDone);
            var gap = events[i].Timestamp - events[i - 1].Timestamp;
            Assert.True(gap >= TimeSpan.FromMilliseconds(450), $"progress events {gap.TotalMilliseconds:0} ms apart");
        }

        Assert.True(events[^1].TotalBytesSent >= events[^1].BytesDone);
        Assert.Equal(1, events[^1].ActiveConnections);
    }

    private static async Task<SessionInfo> WaitForAsync(TestServer server, Func<SessionInfo, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var match = server.Server.Sessions.FirstOrDefault(condition);
            if (match is not null)
            {
                return match;
            }

            Assert.True(DateTime.UtcNow < deadline, "no session matched in time");
            await Task.Delay(20);
        }
    }
}
