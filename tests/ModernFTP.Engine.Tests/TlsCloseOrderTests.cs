using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ModernFTP.Config;

namespace ModernFTP.Engine.Tests;

/// <summary>
/// A strict TLS client answers the server's close_notify with its own (RFC 8446 6.1) and then reads on
/// until EOF. The server must take that close_notify before it closes the socket, so the client sees a
/// clean EOF and never a reset. Curl on Schannel closes this way; this drives the same order on every OS.
/// </summary>
public class TlsCloseOrderTests
{
    private const int Runs = 20;

    [Fact]
    public async Task DataConnectionCloseNotifyIsAnsweredWithoutAReset()
    {
        await using var server = await StartTlsServerAsync();
        var payload = new byte[250_000];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(server.Home, "tls.bin"), payload);

        var resets = 0;
        for (var i = 0; i < Runs; i++)
        {
            await using var control = await TlsControl.ConnectAsync(server.Port);
            await control.LoginAsync();
            var port = await control.EpsvAsync();
            using var data = new TcpClient();
            await data.ConnectAsync(IPAddress.Loopback, port);
            await control.WriteLineAsync("RETR tls.bin");
            await using var ssl = new SslStream(data.GetStream(), leaveInnerStreamOpen: true, static (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync("localhost");
            Assert.Equal(150, (await control.ReadReplyAsync()).Code);

            var received = await ReadToEndAsync(ssl);
            Assert.Equal(payload.Length, received);
            if (!await AnswerCloseNotifyAndReadOnAsync(ssl, data.Client))
            {
                resets++;
            }

            Assert.Equal(226, (await control.ReadReplyAsync()).Code);
        }

        Assert.Equal(0, resets);
    }

    [Fact]
    public async Task ControlConnectionCloseNotifyAtQuitIsAnsweredWithoutAReset()
    {
        await using var server = await StartTlsServerAsync();
        var resets = 0;
        for (var i = 0; i < Runs; i++)
        {
            await using var control = await TlsControl.ConnectAsync(server.Port);
            await control.LoginAsync();
            Assert.Equal(221, (await control.SendAsync("QUIT")).Code);
            if (!await control.CloseLikeAStrictClientAsync())
            {
                resets++;
            }
        }

        Assert.Equal(0, resets);
    }

    private static async Task<TestServer> StartTlsServerAsync()
    {
        var certificate = X509CertificateLoader.LoadPkcs12(CertificateProvider.CreateSelfSignedPfx(), password: null);
        return await TestServer.StartAsync(configure: o => o.Certificate = certificate);
    }

    private static async Task<int> ReadToEndAsync(Stream stream)
    {
        var buffer = new byte[65536];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            total += read;
        }

        return total;
    }

    /// <summary>
    /// After the server's close_notify: send ours, give the server a moment, then read the raw socket.
    /// True for a clean EOF, false for a reset.
    /// </summary>
    internal static async Task<bool> AnswerCloseNotifyAndReadOnAsync(SslStream ssl, Socket socket)
    {
        try
        {
            await ssl.ShutdownAsync();
            await Task.Delay(5);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var buffer = new byte[16];
            while (await socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token) > 0)
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            return false;
        }
    }

    /// <summary>A control connection that runs AUTH TLS, then speaks FTP over the TLS stream.</summary>
    private sealed class TlsControl : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly SslStream _ssl;
        private readonly StreamReader _reader;

        private TlsControl(TcpClient tcp, SslStream ssl)
        {
            _tcp = tcp;
            _ssl = ssl;
            _reader = new StreamReader(ssl, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1, leaveOpen: true);
        }

        public static async Task<TlsControl> ConnectAsync(int port)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var network = tcp.GetStream();
            await ReadPlainReplyAsync(network);
            await network.WriteAsync("AUTH TLS\r\n"u8.ToArray());
            Assert.StartsWith("234", await ReadPlainReplyAsync(network), StringComparison.Ordinal);
            var ssl = new SslStream(network, leaveInnerStreamOpen: true, static (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync("localhost");
            return new TlsControl(tcp, ssl);
        }

        public async Task LoginAsync()
        {
            Assert.Equal(331, (await SendAsync($"USER {TestServer.UserName}")).Code);
            Assert.Equal(230, (await SendAsync($"PASS {TestServer.Password}")).Code);
            Assert.Equal(200, (await SendAsync("PBSZ 0")).Code);
            Assert.Equal(200, (await SendAsync("PROT P")).Code);
            Assert.Equal(200, (await SendAsync("TYPE I")).Code);
        }

        public async Task<int> EpsvAsync()
        {
            var reply = await SendAsync("EPSV");
            Assert.Equal(229, reply.Code);
            var start = reply.Text.IndexOf("|||", StringComparison.Ordinal) + 3;
            return int.Parse(reply.Text[start..reply.Text.IndexOf('|', start)], System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task WriteLineAsync(string line)
        {
            await _ssl.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"));
            await _ssl.FlushAsync();
        }

        public async Task<FtpReply> SendAsync(string line)
        {
            await WriteLineAsync(line);
            return await ReadReplyAsync();
        }

        public async Task<FtpReply> ReadReplyAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var first = await _reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Connection closed before a reply.");
            var text = new StringBuilder(first);
            if (first.Length >= 4 && first[3] == '-')
            {
                while (await _reader.ReadLineAsync(timeout.Token) is { } line)
                {
                    text.Append('\n').Append(line);
                    if (line.StartsWith(first[..3] + " ", StringComparison.Ordinal))
                    {
                        break;
                    }
                }
            }

            return new FtpReply(int.Parse(first[..3], System.Globalization.CultureInfo.InvariantCulture), text.ToString());
        }

        /// <summary>Reads to the server's close_notify, answers it, then reads the raw socket. True for a clean EOF.</summary>
        public async Task<bool> CloseLikeAStrictClientAsync()
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (await _ssl.ReadAsync(new byte[16], timeout.Token) > 0)
                {
                }
            }
            catch (IOException)
            {
                return false;
            }

            return await AnswerCloseNotifyAndReadOnAsync(_ssl, _tcp.Client);
        }

        public async ValueTask DisposeAsync()
        {
            _reader.Dispose();
            await _ssl.DisposeAsync();
            _tcp.Dispose();
        }

        /// <summary>Reads one plain text reply (multi line included) byte by byte, so nothing past it is buffered.</summary>
        private static async Task<string> ReadPlainReplyAsync(NetworkStream stream)
        {
            var first = await ReadPlainLineAsync(stream);
            if (first.Length >= 4 && first[3] == '-')
            {
                while (!(await ReadPlainLineAsync(stream)).StartsWith(first[..3] + " ", StringComparison.Ordinal))
                {
                }
            }

            return first;
        }

        private static async Task<string> ReadPlainLineAsync(NetworkStream stream)
        {
            var line = new StringBuilder();
            var one = new byte[1];
            while (await stream.ReadAsync(one) == 1 && one[0] != '\n')
            {
                line.Append((char)one[0]);
            }

            return line.ToString().TrimEnd('\r');
        }
    }
}
