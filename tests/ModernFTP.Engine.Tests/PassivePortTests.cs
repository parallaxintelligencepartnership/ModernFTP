using System.Net;
using System.Net.Sockets;

namespace ModernFTP.Engine.Tests;

public class PassivePortTests
{
    [Fact]
    public void PoolNeverLeasesAPortTwice()
    {
        var port = TestServer.PickPassiveBase(20000, 30000, 1);
        var pool = new PassivePortPool(port, port);
        using var first = pool.TryOpen(IPAddress.Loopback, IPAddress.Loopback);
        Assert.NotNull(first);
        Assert.Null(pool.TryOpen(IPAddress.Loopback, IPAddress.Loopback));
        first.Dispose();
        using var again = pool.TryOpen(IPAddress.Loopback, IPAddress.Loopback);
        Assert.NotNull(again);
        Assert.Equal(port, again.Port);
    }

    [Fact]
    public async Task ConcurrentSessionsOnAPoolOfTwoGetDistinctPortsAndTheirOwnFiles()
    {
        var root = Directory.CreateTempSubdirectory("modernftp-tests-").FullName;
        try
        {
            var homeA = Directory.CreateDirectory(Path.Combine(root, "a")).FullName;
            var homeB = Directory.CreateDirectory(Path.Combine(root, "b")).FullName;
            await File.WriteAllTextAsync(Path.Combine(homeA, "file.txt"), "ALICE-PRIVATE");
            await File.WriteAllTextAsync(Path.Combine(homeB, "file.txt"), "BOB-PRIVATE");
            var p = TestServer.PickPassiveBase(20000, 30000, 2);
            await using var server = new FtpServer(new FtpServerOptions
            {
                ListenAddress = IPAddress.Loopback,
                Port = 0,
                PassivePortMin = p,
                PassivePortMax = p + 1,
                FailedLoginDelay = TimeSpan.Zero,
                Users =
                [
                    new FtpUser { UserName = "alice", Credential = new PlaintextCredential("a"), HomeDirectory = homeA },
                    new FtpUser { UserName = "bob", Credential = new PlaintextCredential("b"), HomeDirectory = homeB },
                ],
            });
            await server.StartAsync();
            var control = server.LocalEndPoint!.Port;

            // One round only: on macOS a port whose transfer the server closed sits in TIME_WAIT and
            // cannot be rebound for a while (no address reuse there), so a second round could see 425.
            await using var alice = await LoginAsync(control, "alice", "a");
            await using var bob = await LoginAsync(control, "bob", "b");

            // Both sessions hold a listener at once, and bob asks again so the round robin search
            // wraps onto alice's port: the pool must not hand it out a second time.
            var aliceReply = await alice.SendAsync("EPSV");
            Assert.Equal(229, (await bob.SendAsync("EPSV")).Code);
            var bobReply = await bob.SendAsync("EPSV");
            Assert.Equal(229, aliceReply.Code);
            Assert.Equal(229, bobReply.Code);
            var alicePort = EpsvPort(aliceReply.Text);
            var bobPort = EpsvPort(bobReply.Text);
            Assert.NotEqual(alicePort, bobPort);

            Assert.Equal(150, (await bob.SendAsync("RETR file.txt")).Code);
            Assert.Equal(150, (await alice.SendAsync("RETR file.txt")).Code);
            var aliceGot = ReadAllAsync(alicePort);
            var bobGot = ReadAllAsync(bobPort);
            Assert.Equal("ALICE-PRIVATE", await aliceGot);
            Assert.Equal("BOB-PRIVATE", await bobGot);
            Assert.Equal(226, (await alice.ReadReplyAsync()).Code);
            Assert.Equal(226, (await bob.ReadReplyAsync()).Code);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<FtpTestClient> LoginAsync(int port, string user, string password)
    {
        var client = await FtpTestClient.ConnectAsync(port);
        Assert.Equal(220, (await client.ReadReplyAsync()).Code);
        Assert.Equal(331, (await client.SendAsync($"USER {user}")).Code);
        Assert.Equal(230, (await client.SendAsync($"PASS {password}")).Code);
        return client;
    }

    private static int EpsvPort(string text)
    {
        var start = text.IndexOf("|||", StringComparison.Ordinal) + 3;
        return int.Parse(text[start..text.IndexOf('|', start)], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ReadAllAsync(int port)
    {
        using var data = new TcpClient();
        await data.ConnectAsync(IPAddress.Loopback, port);
        using var reader = new StreamReader(data.GetStream());
        return await reader.ReadToEndAsync();
    }
}
