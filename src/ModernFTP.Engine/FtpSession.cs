using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace ModernFTP.Engine;

/// <summary>
/// One control connection. Commands run one at a time on the read loop; a data transfer runs as
/// a background task so ABOR, STAT and NOOP can be served while it is in flight. Any other
/// command waits for the running transfer to finish, which keeps replies in order.
/// </summary>
internal sealed partial class FtpSession
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);

    private readonly FtpServer _server;
    private readonly FtpServerOptions _options;
    private readonly Socket _socket;
    private readonly NetworkStream _network;
    private readonly ControlLineReader _reader = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts;
    private readonly Dictionary<string, CommandSpec> _commands;

    private Stream _control;
    private long _lastActivity = Environment.TickCount64;
    private string _closeReason = "client closed the connection";

    private string? _pendingUser;
    private FtpUser? _user;
    private int _failedLogins;
    private VirtualPath _cwd = VirtualPath.Root;
    private bool _binary = true;
    private bool _tls;
    private bool _pbszReceived;
    private bool _protectData;
    private bool _epsvAll;
    private long _restartOffset;
    private VirtualPath? _renameFrom;
    private VirtualPath? _renameSource;
    private IDataChannel? _dataChannel;
    private Task? _transfer;
    private CancellationTokenSource? _transferCts;
    private volatile bool _transferAborted;
    private TransferMeter? _meter;
    private long _bytesSent;
    private long _bytesReceived;

    public FtpSession(FtpServer server, long id, Socket socket, CancellationToken serverToken)
    {
        _server = server;
        _options = server.Options;
        _socket = socket;
        Id = id;
        RemoteEndPoint = (IPEndPoint)socket.RemoteEndPoint!;
        LocalEndPoint = (IPEndPoint)socket.LocalEndPoint!;
        _network = new NetworkStream(socket, ownsSocket: true);
        _control = _network;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        _commands = BuildCommandTable();
        ConnectedAt = DateTimeOffset.UtcNow;
    }

    private delegate Task<bool> CommandHandler(string argument);

    public long Id { get; }

    public IPEndPoint RemoteEndPoint { get; }

    public IPEndPoint LocalEndPoint { get; }

    public FtpUser? User => _user;

    public string CloseReason => _closeReason;

    public DateTimeOffset ConnectedAt { get; }

    /// <summary>A point in time view for <see cref="FtpServer.Sessions"/>; safe from any thread.</summary>
    public SessionInfo Snapshot()
    {
        var quietMs = Environment.TickCount64 - Interlocked.Read(ref _lastActivity);
        return new SessionInfo
        {
            Id = Id,
            User = Volatile.Read(ref _user)?.UserName,
            RemoteAddress = RemoteAddress,
            ConnectedAt = ConnectedAt,
            LastActivity = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Math.Max(0, quietMs)),
            CurrentTransfer = Volatile.Read(ref _meter)?.Snapshot(),
            BytesSent = Interlocked.Read(ref _bytesSent),
            BytesReceived = Interlocked.Read(ref _bytesReceived),
        };
    }

    /// <summary>Server operator disconnect: 421, then the control connection is closed.</summary>
    public async Task DisconnectAsync()
    {
        _closeReason = "disconnected by the server administrator";
        await TryReplyAsync(421, "Disconnected by the server administrator.").ConfigureAwait(false);
        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        CloseSocket();
    }

    /// <summary>
    /// Server operator abort of the running transfer. The transfer replies 426 and then 226, as if the
    /// client had sent ABOR, and the session stays open. False when no transfer is running.
    /// </summary>
    public bool AbortTransferFromServer()
    {
        var meter = Volatile.Read(ref _meter);
        if (meter is null)
        {
            return false;
        }

        _transferAborted = true;
        meter.AbortFromServer();
        return true;
    }

    private IPAddress RemoteAddress => NetUtil.Normalize(RemoteEndPoint.Address);

    private TimeSpan EffectiveIdleTimeout => Volatile.Read(ref _user)?.IdleTimeout ?? _options.IdleTimeout;

    public async Task RunAsync()
    {
        var token = _cts.Token;
        Task? watchdog = null;
        try
        {
            await ReplyAsync(220, BuildBanner()).ConfigureAwait(false);
            watchdog = Task.WhenAll(WatchIdleAsync(token), WatchLoginAsync(token));
            while (!token.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_control, token).ConfigureAwait(false);
                if (line.Kind == ControlLineKind.EndOfStream)
                {
                    break;
                }

                Touch();
                if (line.Kind == ControlLineKind.TooLong)
                {
                    await ReplyAsync(500, "Command line too long.").ConfigureAwait(false);
                    continue;
                }

                if (!await DispatchAsync(line.Text).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // Connection closed, idle timeout or server shutdown; the reason is already recorded.
        }
        catch (AuthenticationException ex)
        {
            _closeReason = "TLS negotiation failed";
            PublishError($"TLS negotiation failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            _closeReason = "internal error";
            PublishError($"session error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            await CleanupAsync(watchdog).ConfigureAwait(false);
        }
    }

    public async Task RequestShutdownAsync(string message)
    {
        _closeReason = "server stopping";
        await TryReplyAsync(421, message).ConfigureAwait(false);
        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Session already finished.
        }
    }

    /// <summary>Closes the control socket so pending reads and writes fail at once (server shutdown).</summary>
    public void CloseSocket()
    {
        try
        {
            _socket.Close();
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task CleanupAsync(Task? watchdog)
    {
        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        await AbortTransferAsync().ConfigureAwait(false);
        ReplaceDataChannel(null);
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }

        try
        {
            await _control.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A broken TLS stream can throw on dispose; the socket is closed below regardless.
        }

        await _network.DisposeAsync().ConfigureAwait(false);
        if (watchdog is not null)
        {
            try
            {
                await watchdog.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);

    /// <summary>Uses the logged in user's idle timeout when it has one, otherwise the server's.</summary>
    private async Task WatchIdleAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var idle = EffectiveIdleTimeout;
                var period = idle > TimeSpan.Zero
                    ? TimeSpan.FromMilliseconds(Math.Clamp(idle.TotalMilliseconds / 4, 50, 1000))
                    : TimeSpan.FromSeconds(1);
                await Task.Delay(period, token).ConfigureAwait(false);
                idle = EffectiveIdleTimeout;
                if (idle <= TimeSpan.Zero)
                {
                    continue;
                }

                var quiet = Environment.TickCount64 - Interlocked.Read(ref _lastActivity);
                if (quiet >= idle.TotalMilliseconds)
                {
                    _closeReason = "idle timeout";
                    await TryReplyAsync(421, "Idle timeout reached, closing control connection.").ConfigureAwait(false);
                    await _cts.CancelAsync().ConfigureAwait(false);
                    _socket.Close();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Closes a session that has not logged in within the login timeout, whatever it sends.</summary>
    private async Task WatchLoginAsync(CancellationToken token)
    {
        var limit = _options.LoginTimeout;
        if (limit <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            await Task.Delay(limit, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (Volatile.Read(ref _user) is null)
        {
            _closeReason = "login timeout";
            await TryReplyAsync(421, "Login timeout reached, closing control connection.").ConfigureAwait(false);
            await _cts.CancelAsync().ConfigureAwait(false);
            _socket.Close();
        }
    }

    private string BuildBanner()
    {
        var lines = new List<string>();
        if (!_options.HideServerName)
        {
            lines.Add($"{FtpServer.ServerName} ready.");
        }

        lines.AddRange(SplitMessage(_options.WelcomeMessage));
        if (lines.Count == 0)
        {
            lines.Add("Ready.");
        }

        return string.Join('\n', lines);
    }

    private static IEnumerable<string> SplitMessage(string? message) =>
        string.IsNullOrWhiteSpace(message)
            ? []
            : message.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Select(Sanitize);

    /// <summary>Removes control characters so configured or echoed text can never inject reply lines.</summary>
    private static string Sanitize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        return builder.ToString();
    }

    private async Task<bool> DispatchAsync(string line)
    {
        var space = line.IndexOf(' ');
        var verb = (space < 0 ? line : line[..space]).ToUpperInvariant();
        var argument = space < 0 ? string.Empty : line[(space + 1)..];

        _server.Publish(new CommandReceivedEvent
        {
            SessionId = Id,
            RemoteEndPoint = RemoteEndPoint,
            UserName = _user?.UserName,
            Command = Sanitize(verb.Length > 16 ? verb[..16] : verb),
            Argument = verb == "PASS" ? "****" : Sanitize(argument.Length > 256 ? argument[..256] + "..." : argument),
        });

        _renameSource = _renameFrom;
        _renameFrom = null;

        if (!_commands.TryGetValue(verb, out var spec))
        {
            await ReplyAsync(500, "Command not understood.").ConfigureAwait(false);
            return true;
        }

        if (spec.RequiresLogin && _user is null)
        {
            await ReplyAsync(530, "Please log in with USER and PASS first.").ConfigureAwait(false);
            return true;
        }

        if (!spec.AllowedDuringTransfer)
        {
            await WaitForTransferAsync().ConfigureAwait(false);
        }

        try
        {
            return await spec.Handler(argument).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            PublishError($"{verb} failed: {ex.Message}");
            await ReplyAsync(550, "Requested action not taken.").ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            PublishError($"{verb} failed: {ex.GetType().Name}: {ex.Message}");
            await ReplyAsync(451, "Requested action aborted: local error in processing.").ConfigureAwait(false);
            return true;
        }
    }

    private void PublishError(string message) => _server.Publish(new ErrorEvent
    {
        SessionId = Id,
        RemoteEndPoint = RemoteEndPoint,
        UserName = _user?.UserName,
        Message = message,
    });

    private Task ReplyAsync(int code, string text)
    {
        var lines = text.Split('\n');
        var builder = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            builder.Append(code).Append(i == lines.Length - 1 ? ' ' : '-').Append(Sanitize(lines[i])).Append("\r\n");
        }

        return WriteRawAsync(builder.ToString());
    }

    private async Task WriteRawAsync(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_server.WriteLingerToken);
        timeout.CancelAfter(WriteTimeout);
        await _writeLock.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            await _control.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            await _control.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task TryReplyAsync(int code, string text)
    {
        try
        {
            await ReplyAsync(code, text).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort: the connection may already be gone.
        }
    }

    private async Task WaitForTransferAsync()
    {
        if (_transfer is { } transfer)
        {
            try
            {
                await transfer.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Transfer failures are replied to and reported by the transfer itself.
            }

            _transfer = null;
        }
    }

    private async Task AbortTransferAsync()
    {
        if (_transfer is { IsCompleted: false })
        {
            _transferAborted = true;
            try
            {
                _transferCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        await WaitForTransferAsync().ConfigureAwait(false);
    }

    private void ReplaceDataChannel(IDataChannel? channel)
    {
        var previous = _dataChannel;
        _dataChannel = channel;
        previous?.Dispose();
    }

    private long TakeRestartOffset()
    {
        var offset = _restartOffset;
        _restartOffset = 0;
        return offset;
    }

    private async Task<bool> StartTransferAsync(
        TransferDirection direction,
        VirtualPath path,
        Func<Stream, CancellationToken, Task<long>> body,
        IDisposable? resource,
        long? totalBytes)
    {
        var channel = _dataChannel;
        _dataChannel = null;
        if (channel is null)
        {
            resource?.Dispose();
            await ReplyAsync(425, "Use PASV, EPSV, PORT or EPRT first.").ConfigureAwait(false);
            return true;
        }

        _transferAborted = false;
        _transferCts?.Dispose();
        _transferCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var mode = direction == TransferDirection.Listing ? "ASCII" : _binary ? "BINARY" : "ASCII";
        await ReplyAsync(150, $"Opening {mode} mode data connection for {path}.").ConfigureAwait(false);
        var meter = new TransferMeter(path.ToString(), direction, totalBytes, _transferCts);
        Volatile.Write(ref _meter, meter);
        _transfer = RunTransferAsync(channel, direction, path, body, resource, meter, _transferCts.Token);
        return true;
    }

    private async Task RunTransferAsync(
        IDataChannel channel,
        TransferDirection direction,
        VirtualPath path,
        Func<Stream, CancellationToken, Task<long>> body,
        IDisposable? resource,
        TransferMeter meter,
        CancellationToken token)
    {
        await Task.Yield();
        var started = Stopwatch.GetTimestamp();
        long bytes = 0;
        Stream? data = null;
        int code;
        string reply;
        string? error = null;
        try
        {
            DataConnection connection;
            try
            {
                connection = await channel.OpenAsync(_options.DataConnectionTimeout, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException)
            {
                throw new DataConnectionException(ex.Message);
            }

            data = connection.Stream;
            using var registration = token.Register(static state => ((Stream)state!).Dispose(), data);
            if (_protectData)
            {
                data = await AuthenticateTlsAsync(data, token).ConfigureAwait(false);
            }

            _server.Publish(new TransferStartedEvent
            {
                SessionId = Id,
                RemoteEndPoint = RemoteEndPoint,
                UserName = _user?.UserName,
                Direction = direction,
                Path = path.ToString(),
                DataPort = connection.LocalPort,
                Secure = _protectData,
            });

            var sent = direction != TransferDirection.Upload;
            bytes = await body(new MeteredStream(data, count => CountBytes(meter, sent, count)), token).ConfigureAwait(false);
            await data.FlushAsync(token).ConfigureAwait(false);
            if (data is SslStream ssl)
            {
                await ssl.ShutdownAsync().ConfigureAwait(false);
            }

            code = 226;
            reply = "Transfer complete.";
        }
        catch (Exception) when (_transferAborted)
        {
            code = 426;
            reply = "Connection closed; transfer aborted.";
            error = "aborted";
        }
        catch (Exception) when (_cts.IsCancellationRequested)
        {
            code = 0;
            reply = string.Empty;
            error = "session closed";
        }
        catch (DataConnectionException ex)
        {
            code = 425;
            reply = "Can't open data connection.";
            error = ex.Message;
        }
        catch (AuthenticationException ex)
        {
            code = 425;
            reply = "TLS negotiation on the data connection failed.";
            error = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            code = 426;
            reply = "Connection closed; transfer aborted.";
            error = ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            code = 550;
            reply = "Requested action not taken.";
            error = ex.Message;
        }
        catch (Exception ex)
        {
            code = 451;
            reply = "Requested action aborted: local error in processing.";
            error = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            if (data is not null)
            {
                try
                {
                    await data.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            channel.Dispose();
            resource?.Dispose();
            Interlocked.CompareExchange(ref _meter, null, meter);
        }

        _server.Publish(new TransferCompletedEvent
        {
            SessionId = Id,
            RemoteEndPoint = RemoteEndPoint,
            UserName = _user?.UserName,
            Direction = direction,
            Path = path.ToString(),
            Bytes = bytes,
            Duration = Stopwatch.GetElapsedTime(started),
            Success = code == 226,
            Error = error,
        });

        if (code != 0)
        {
            await TryReplyAsync(code, reply).ConfigureAwait(false);
        }

        if (code == 426 && meter.AbortedByServer)
        {
            // Completes the abort the way ABOR would, so the client sees 426 then 226 and can go on.
            await TryReplyAsync(226, "Abort successful.").ConfigureAwait(false);
        }
    }

    private void CountBytes(TransferMeter meter, bool sent, int count)
    {
        meter.Add(count);
        if (sent)
        {
            Interlocked.Add(ref _bytesSent, count);
        }
        else
        {
            Interlocked.Add(ref _bytesReceived, count);
        }
    }

    private async Task<SslStream> AuthenticateTlsAsync(Stream inner, CancellationToken token)
    {
        var options = _server.CreateTlsOptions() ?? throw new AuthenticationException("TLS is not configured.");
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_options.TlsHandshakeTimeout);
            await ssl.AuthenticateAsServerAsync(options, timeout.Token).ConfigureAwait(false);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<long> CopyAsync(Stream source, Stream destination, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                total += read;
                Touch();
            }

            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed record CommandSpec(CommandHandler Handler, bool RequiresLogin, bool AllowedDuringTransfer);

    private sealed class DataConnectionException(string message) : Exception(message);
}
