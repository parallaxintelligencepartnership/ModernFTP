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
}
