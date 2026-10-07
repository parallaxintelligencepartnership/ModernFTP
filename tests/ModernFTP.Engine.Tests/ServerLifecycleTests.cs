using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ModernFTP.Engine.Tests;

[Collection("Console")]
public class ServerLifecycleTests
{
    [Fact]
    public async Task SecondServerOnTheSamePortFailsToStart()
    {
        await using var first = await TestServer.StartAsync();
        await using var second = new FtpServer(new FtpServerOptions { ListenAddress = IPAddress.Loopback, Port = first.Port });
        var ex = await Assert.ThrowsAsync<PortInUseException>(() => second.StartAsync());
        Assert.Equal(first.Port, ex.Port);
        Assert.Equal($"Port {first.Port} is already in use.", ex.Message);
    }

    [Fact]
    public async Task ConsoleHostExitsWith3WhenThePortIsInUse()
    {
        await using var first = await TestServer.StartAsync();
        var config = Path.Combine(first.Root, "config.json");
        await File.WriteAllTextAsync(config, $$"""{ "listenAddress": "127.0.0.1", "port": {{first.Port}}, "tls": { "enabled": false } }""");
        var error = new StringWriter();
        var previous = Console.Error;
        Console.SetError(error);
        int code;
        try
        {
            code = await ModernFTP.Host.Console.Program.Main(["serve", "--config", config]);
        }
        finally
        {
            Console.SetError(previous);
        }

        Assert.Equal(3, code);
        Assert.Contains($"Port {first.Port} is already in use", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShutdownTakesUnderThreeSecondsWithAClientThatStopsReading()
    {
        var server = await TestServer.StartAsync();
        try
        {
            await using var polite = await server.ConnectAsync();
            using var tcp = new TcpClient { ReceiveBufferSize = 1024 };
            await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
            var stream = tcp.GetStream();

            // Pipeline FEAT requests and never read: the server's replies fill both socket buffers
            // and its write blocks while holding the session's write lock.
            var feat = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("FEAT\r\n", 200)));
            using var stopWriting = new CancellationTokenSource();
            var writer = Task.Run(async () =>
            {
                try
                {
                    while (!stopWriting.IsCancellationRequested)
                    {
                        await stream.WriteAsync(feat, stopWriting.Token);
                    }
                }
                catch (Exception)
                {
                }
            });
            await Task.Delay(2000);

            var watch = Stopwatch.StartNew();
            await server.Server.StopAsync(TimeSpan.FromSeconds(2));
            watch.Stop();
            await stopWriting.CancelAsync();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"StopAsync took {watch.Elapsed}");

            var reply = await polite.ReadReplyAsync();
            Assert.Equal(421, reply.Code);
            await writer;
        }
        finally
        {
            await server.DisposeAsync();
        }
    }
}
