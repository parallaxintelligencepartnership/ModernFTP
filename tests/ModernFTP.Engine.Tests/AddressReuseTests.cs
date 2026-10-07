using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ModernFTP.Engine.Tests;

/// <summary>Per platform address reuse on listeners, and the round robin passive pool.</summary>
[Collection("Console")]
public class AddressReuseTests
{
    private const string NoReuseOnMac =
        "macOS sets no address reuse on listeners (ReuseAddress there adds SO_REUSEPORT), so TIME_WAIT blocks an immediate rebind.";

    [Fact]
    public void PassivePoolStartsAfterTheLastAllocatedPort()
    {
        var p = TestServer.PickPassiveBase(20000, 30000, 3);
        var pool = new PassivePortPool(p, p + 2);
        using var a = pool.TryOpen(IPAddress.Loopback, IPAddress.Loopback)!;
        var b = pool.TryOpen(IPAddress.Loopback, IPAddress.Loopback)!;
        var c = pool.TryOpen(IPAddress.Loopback, IPAddress.Loopback)!;
        Assert.Equal([p, p + 1, p + 2], new[] { a.Port, b.Port, c.Port });
        b.Dispose();
        c.Dispose();

        // The last allocation was p + 2, so the search wraps to p (leased) and takes p + 1.
        var d = pool.TryOpen(IPAddress.Loopback, IPAddress.Loopback)!;
        Assert.Equal(p + 1, d.Port);
        d.Dispose();

        // Round robin: p + 1 was just used, so the next one is p + 2 even though p + 1 is free.
        using var e = pool.TryOpen(IPAddress.Loopback, IPAddress.Loopback)!;
        Assert.Equal(p + 2, e.Port);
    }

    [Fact]
    public void TwoPoolsOnTheSamePortNeverBothListen()
    {
        var p = TestServer.PickPassiveBase(20000, 30000, 1);
        using var first = new PassivePortPool(p, p).TryOpen(IPAddress.Loopback, IPAddress.Loopback);
        Assert.NotNull(first);
        Assert.Null(new PassivePortPool(p, p).TryOpen(IPAddress.Loopback, IPAddress.Loopback));
    }

    [PlatformFact(windows: false, linux: true, macOS: false, reason: "Linux only: SO_REUSEADDR is set only on Linux.")]
    public void LinuxListenersUseReuseAddressAndStillRefuseASecondListener()
    {
        using var first = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        ListenerSockets.Configure(first);
        var option = new byte[4];
        first.GetRawSocketOption(ListenerSockets.LinuxSolSocket, ListenerSockets.LinuxSoReuseAddr, option);
        Assert.NotEqual(0, BitConverter.ToInt32(option));
        first.GetRawSocketOption(ListenerSockets.LinuxSolSocket, ListenerSockets.LinuxSoReusePort, option);
        Assert.Equal(0, BitConverter.ToInt32(option));
        first.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        first.Listen(1);
        var port = ((IPEndPoint)first.LocalEndPoint!).Port;

        using var second = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        ListenerSockets.Configure(second);
        var ex = Assert.Throws<SocketException>(() =>
        {
            second.Bind(new IPEndPoint(IPAddress.Loopback, port));
            second.Listen(1);
        });
        Assert.Equal(SocketError.AddressAlreadyInUse, ex.SocketErrorCode);
    }

    [PlatformFact(windows: true, linux: true, macOS: false, reason: NoReuseOnMac)]
    public async Task ServerRestartsOnTheSamePortRightAfterClosingAClient()
    {
        int port;
        await using (var first = await TestServer.StartAsync())
        {
            port = first.Port;
            await using var client = await first.ConnectAsync();
            Assert.Equal(221, (await client.SendAsync("QUIT")).Code);

            // The server closes first, so its side of the connection sits in TIME_WAIT on the port.
            Assert.Null(await client.ReadLineOrNullAsync(TimeSpan.FromSeconds(5)));
            await first.Server.StopAsync(TimeSpan.FromSeconds(2));
        }

        await using var second = new FtpServer(new FtpServerOptions { ListenAddress = IPAddress.Loopback, Port = port });
        await second.StartAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        using var reader = new StreamReader(tcp.GetStream(), Encoding.ASCII);
        Assert.StartsWith("220", await reader.ReadLineAsync(), StringComparison.Ordinal);
    }

    [PlatformFact(windows: true, linux: true, macOS: false, reason: NoReuseOnMac)]
    public async Task TwentyPassiveTransfersCycleThroughAPoolOfFive()
    {
        await using var server = await TestServer.StartAsync(configure: o => o.PassivePortMax = o.PassivePortMin + 4);
        await File.WriteAllTextAsync(Path.Combine(server.Home, "cycle.txt"), "cycle");
        await using var client = await server.ConnectAsync();
        var ports = new List<int>();
        for (var i = 0; i < 20; i++)
        {
            var reply = await client.SendAsync("EPSV");
            Assert.True(reply.Code == 229, $"transfer {i + 1}: EPSV replied {reply.Text}");
            var start = reply.Text.IndexOf("|||", StringComparison.Ordinal) + 3;
            var port = int.Parse(reply.Text[start..reply.Text.IndexOf('|', start)], System.Globalization.CultureInfo.InvariantCulture);
            ports.Add(port);
            using var data = new TcpClient();
            await data.ConnectAsync(IPAddress.Loopback, port);
            Assert.Equal(150, (await client.SendAsync("RETR cycle.txt")).Code);
            using (var reader = new StreamReader(data.GetStream()))
            {
                Assert.Equal("cycle", await reader.ReadToEndAsync());
            }

            Assert.Equal(226, (await client.ReadReplyAsync()).Code);
        }

        Assert.Equal(5, ports.Distinct().Count());
    }
}
