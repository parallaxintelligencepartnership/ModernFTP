using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ModernFTP.Engine.Tests;

/// <summary>An in-process server on 127.0.0.1 with an ephemeral control port and a temp home directory.</summary>
internal sealed class TestServer : IAsyncDisposable
{
    public const string UserName = "tester";
    public const string Password = "s3cret";

    private TestServer(FtpServer server, string root)
    {
        Server = server;
        Root = root;
    }

    public FtpServer Server { get; }

    public string Root { get; }

    public string Home => Path.Combine(Root, "home");

    public int Port => Server.LocalEndPoint!.Port;

    public static async Task<TestServer> StartAsync(
        FtpPermissions? permissions = null,
        Action<FtpServerOptions>? configure = null,
        int downloadRateKBps = 0)
    {
        var root = Path.Combine(Path.GetTempPath(), "modernftp-tests-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        var basePort = Random.Shared.Next(40000, 60000);
        var options = new FtpServerOptions
        {
            ListenAddress = IPAddress.Loopback,
            Port = 0,
            PassivePortMin = basePort,
            PassivePortMax = basePort + 9,
            FailedLoginDelay = TimeSpan.Zero,
            Users =
            [
                new FtpUser
                {
                    UserName = UserName,
                    Credential = Pbkdf2Credential.Create(Password, iterations: 1000),
                    HomeDirectory = home,
                    Permissions = new PermissionRules(permissions ?? FtpPermissions.All),
                    DownloadRateKBps = downloadRateKBps,
                },
            ],
        };
        configure?.Invoke(options);
        var server = new FtpServer(options);
        await server.StartAsync();
        return new TestServer(server, root);
    }

    public async Task<FtpTestClient> ConnectAsync(bool login = true)
    {
        var client = await FtpTestClient.ConnectAsync(Port);
        var banner = await client.ReadReplyAsync();
        Assert.Equal(220, banner.Code);
        if (login)
        {
            Assert.Equal(331, (await client.SendAsync($"USER {UserName}")).Code);
            Assert.Equal(230, (await client.SendAsync($"PASS {Password}")).Code);
        }

        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await Server.StopAsync(TimeSpan.FromSeconds(2));
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed record FtpReply(int Code, string Text);

/// <summary>Raw control connection client: sends exact lines and parses (multi line) replies.</summary>
internal sealed class FtpTestClient : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly StreamReader _reader;

    private FtpTestClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _reader = new StreamReader(_stream, Encoding.UTF8);
    }

    public static async Task<FtpTestClient> ConnectAsync(int port)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        return new FtpTestClient(tcp);
    }

    public async Task WriteAsync(string raw)
    {
        var bytes = Encoding.UTF8.GetBytes(raw);
        await _stream.WriteAsync(bytes);
    }

    public async Task WriteBytesAsync(byte[] bytes) => await _stream.WriteAsync(bytes);

    public async Task<FtpReply> SendAsync(string line)
    {
        await WriteAsync(line + "\r\n");
        return await ReadReplyAsync();
    }

    public async Task<FtpReply> ReadReplyAsync()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var first = await _reader.ReadLineAsync(cts.Token) ?? throw new IOException("Connection closed before a reply.");
        var text = new StringBuilder(first);
        if (first.Length >= 4 && first[3] == '-')
        {
            var code = first[..3];
            while (true)
            {
                var line = await _reader.ReadLineAsync(cts.Token) ?? throw new IOException("Connection closed inside a reply.");
                text.Append('\n').Append(line);
                if (line.StartsWith(code + " ", StringComparison.Ordinal))
                {
                    break;
                }
            }
        }

        return new FtpReply(int.Parse(first[..3], System.Globalization.CultureInfo.InvariantCulture), text.ToString());
    }

    /// <summary>Returns null when the server closed the connection.</summary>
    public async Task<string?> ReadLineOrNullAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await _reader.ReadLineAsync(cts.Token);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task<TcpClient> OpenPassiveAsync()
    {
        var reply = await SendAsync("EPSV");
        Assert.Equal(229, reply.Code);
        var start = reply.Text.IndexOf("|||", StringComparison.Ordinal) + 3;
        var end = reply.Text.IndexOf('|', start);
        var port = int.Parse(reply.Text[start..end], System.Globalization.CultureInfo.InvariantCulture);
        var data = new TcpClient();
        await data.ConnectAsync(IPAddress.Loopback, port);
        return data;
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await _stream.DisposeAsync();
        _tcp.Dispose();
    }
}
