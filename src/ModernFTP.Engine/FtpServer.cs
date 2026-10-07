using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Text;

namespace ModernFTP.Engine;

/// <summary>The FTP server: one listener, one <see cref="FtpSession"/> per control connection.</summary>
public sealed class FtpServer : IAsyncDisposable
{
    private readonly EventHub _events = new();
    private readonly object _applyGate = new();
    private volatile Dictionary<string, FtpUser> _users;
    private readonly ConcurrentDictionary<long, SessionEntry> _sessions = new();
    private readonly object _limitsGate = new();
    private readonly Dictionary<IPAddress, int> _perIp = [];
    private readonly Dictionary<IPAddress, int> _unauthenticatedPerIp = [];
    private readonly Dictionary<string, int> _perUser = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string User, IPAddress Address), int> _perUserIp = new(UserAddressComparer.Instance);
    private readonly SslStreamCertificateContext? _certificateContext;
    private readonly CancellationTokenSource _writeLinger = new();
    private int _total;
    private long _totalBytesSent;
    private long _totalBytesReceived;
    private long _nextSessionId;
    private Socket? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private volatile bool _stopping;

    public FtpServer(FtpServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.PassivePortMin < 1 || options.PassivePortMax > 65535 || options.PassivePortMin > options.PassivePortMax)
        {
            throw new ArgumentException("Passive port range is invalid.", nameof(options));
        }

        if (options.Port is < 0 or > 65535)
        {
            throw new ArgumentException("Port is invalid.", nameof(options));
        }

        Options = options;
        _users = BuildUserTable(options.Users, nameof(options));

        PassivePorts = new PassivePortPool(options.PassivePortMin, options.PassivePortMax);
        if (options.Certificate is not null)
        {
            if (!options.Certificate.HasPrivateKey)
            {
                throw new ArgumentException("The TLS certificate has no private key.", nameof(options));
            }

            _certificateContext = SslStreamCertificateContext.Create(options.Certificate, additionalCertificates: null, offline: true);
        }
    }

    public static string ServerName { get; } =
        $"ModernFTP {typeof(FtpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0"}";

    public FtpServerOptions Options { get; }

    public IObservable<ServerEvent> Events => _events;

    public IPEndPoint? LocalEndPoint => _listener?.LocalEndPoint as IPEndPoint;

    public bool IsRunning => _acceptLoop is { IsCompleted: false };

    public int SessionCount => _sessions.Count;

    /// <summary>Open control connections that passed the accept checks.</summary>
    public int ActiveConnections => _sessions.Count;

    /// <summary>Data connection payload bytes sent to clients since the server was created.</summary>
    public long TotalBytesSent => Interlocked.Read(ref _totalBytesSent);

    /// <summary>Data connection payload bytes received from clients since the server was created.</summary>
    public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);

    /// <summary>A snapshot of every open session, ordered by id.</summary>
    public IReadOnlyList<SessionInfo> Sessions =>
        [.. _sessions.Values.Select(e => e.Session.Snapshot()).OrderBy(s => s.Id)];

    public bool TlsAvailable => _certificateContext is not null;

    internal PassivePortPool PassivePorts { get; }

    /// <summary>How long control replies (the closing 421 included) may still be written once shutdown starts.</summary>
    internal static TimeSpan WriteLinger { get; } = TimeSpan.FromSeconds(2);

    /// <summary>Cancelled <see cref="WriteLinger"/> after shutdown starts; every control write observes it.</summary>
    internal CancellationToken WriteLingerToken => _writeLinger.Token;

    /// <summary>Replies 421 and closes the session. False when no such session is open.</summary>
    public bool Disconnect(long sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            return false;
        }

        _ = entry.Session.DisconnectAsync();
        return true;
    }

    /// <summary>
    /// Cancels the session's running data transfer; the client gets 426 only and the session stays
    /// open. False when no such session is open or it has no transfer running.
    /// </summary>
    public bool AbortTransfer(long sessionId) =>
        _sessions.TryGetValue(sessionId, out var entry) && entry.Session.AbortTransferFromServer();

    /// <summary>
    /// Applies changed settings to the running server. Users (added, removed, enabled, disabled, password,
    /// permissions, home, limits) take effect at once: logged in sessions of a user who was removed or
    /// disabled get 421 and are closed, other logged in sessions switch to the user's new settings. The ban
    /// list, messages, global limits and timeouts apply to the next command or connection. Settings that
    /// need a restart are not applied; their names are returned (empty when everything applied).
    /// </summary>
    public IReadOnlyList<string> ApplyOptions(FtpServerOptions next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var users = BuildUserTable(next.Users, nameof(next));
        var restart = new List<string>();
        if (!next.ListenAddress.Equals(Options.ListenAddress))
        {
            restart.Add(RestartSetting.BindAddress);
        }

        if (next.Port != Options.Port)
        {
            restart.Add(RestartSetting.Port);
        }

        if (next.PassivePortMin != Options.PassivePortMin || next.PassivePortMax != Options.PassivePortMax)
        {
            restart.Add(RestartSetting.PassivePortRange);
        }

        if (!string.Equals(next.Certificate?.Thumbprint, Options.Certificate?.Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            restart.Add(RestartSetting.TlsCertificate);
        }

        List<(FtpSession Session, FtpUser? User)> changed = [];
        lock (_applyGate)
        {
            var o = Options;
            o.PassivePublicAddress = next.PassivePublicAddress;
            o.AllowActiveMode = next.AllowActiveMode;
            o.MaxConnections = next.MaxConnections;
            o.MaxConnectionsPerUser = next.MaxConnectionsPerUser;
            o.MaxConnectionsPerIp = next.MaxConnectionsPerIp;
            o.MaxUnauthenticatedPerIp = next.MaxUnauthenticatedPerIp;
            o.LoginTimeout = next.LoginTimeout;
            o.IdleTimeout = next.IdleTimeout;
            o.DataConnectionTimeout = next.DataConnectionTimeout;
            o.TlsHandshakeTimeout = next.TlsHandshakeTimeout;
            o.FailedLoginDelay = next.FailedLoginDelay;
            o.MaxFailedLogins = next.MaxFailedLogins;
            o.WelcomeMessage = next.WelcomeMessage;
            o.GoodbyeMessage = next.GoodbyeMessage;
            o.HideServerName = next.HideServerName;

            if (!ReferenceEquals(next.BanList, o.BanList))
            {
                var wanted = next.BanList.Entries;
                foreach (var entry in o.BanList.Entries.Except(wanted, StringComparer.Ordinal))
                {
                    o.BanList.Remove(entry);
                }

                foreach (var entry in wanted)
                {
                    o.BanList.Add(entry);
                }
            }

            // Swap the table first, then look at the sessions; a login does the reverse (see
            // FtpSession.PassAsync), so a login racing this call is either seen here or sees the new table.
            Interlocked.Exchange(ref _users, users);
            o.Users = next.Users;
            foreach (var entry in _sessions.Values)
            {
                if (entry.Session.User is { } current)
                {
                    var updated = users.GetValueOrDefault(current.UserName);
                    if (!ReferenceEquals(updated, current))
                    {
                        changed.Add((entry.Session, updated is { Enabled: true } ? updated : null));
                    }
                }
            }
        }

        foreach (var (session, user) in changed)
        {
            if (user is null)
            {
                _ = session.DisconnectAsync("account disabled or removed", "Your account was disabled or removed.");
            }
            else
            {
                session.ReplaceUser(user);
            }
        }

        return restart;
    }

    private static Dictionary<string, FtpUser> BuildUserTable(IReadOnlyList<FtpUser> list, string parameter)
    {
        var users = new Dictionary<string, FtpUser>(StringComparer.OrdinalIgnoreCase);
        foreach (var user in list)
        {
            if (!users.TryAdd(user.UserName, user))
            {
                throw new ArgumentException($"Duplicate user '{user.UserName}'.", parameter);
            }
        }

        return users;
    }

    public IDisposable Subscribe(Action<ServerEvent> handler) => _events.Subscribe(new ActionObserver(handler));

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("The server is already started.");
        }

        var endpoint = new IPEndPoint(Options.ListenAddress, Options.Port);
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (endpoint.AddressFamily == AddressFamily.InterNetworkV6 && endpoint.Address.Equals(IPAddress.IPv6Any))
            {
                socket.DualMode = true;
            }

            // A port already in use by another listener must fail here on every platform.
            ListenerSockets.Configure(socket);
            socket.Bind(endpoint);
            socket.Listen(512);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            socket.Dispose();
            throw new PortInUseException(endpoint.Port, ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _listener = socket;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = AcceptLoopAsync(socket, _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(TimeSpan? grace = null)
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null)
        {
            return;
        }

        _stopping = true;
        _listener?.Dispose();

        // Every control write is linked to this token: from here on no write may take longer than
        // the linger, even one already blocked on a client that stopped reading.
        _writeLinger.CancelAfter(WriteLinger);
        var sessions = _sessions.Values.ToArray();
        await Task.WhenAll(sessions.Select(s => s.Session.RequestShutdownAsync("Server is shutting down."))).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        foreach (var entry in sessions)
        {
            entry.Session.CloseSocket();
        }

        try
        {
            await Task.WhenAll(sessions.Select(s => s.Task).Append(_acceptLoop ?? Task.CompletedTask))
                .WaitAsync(grace ?? TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Sessions that ignore cancellation are abandoned; their sockets are already closed.
        }
        catch (Exception)
        {
            // Session failures were reported as events already.
        }

        cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _events.CompleteAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
    }

    /// <summary>Events discarded because subscribers could not keep up.</summary>
    public long DroppedEventCount => _events.DroppedCount;

    internal void Publish(ServerEvent serverEvent) => _events.Publish(serverEvent);

    internal void AddDataBytes(bool sent, int count)
    {
        if (sent)
        {
            Interlocked.Add(ref _totalBytesSent, count);
        }
        else
        {
            Interlocked.Add(ref _totalBytesReceived, count);
        }
    }

    internal FtpUser? FindUser(string name) => _users.GetValueOrDefault(name);

    internal SslServerAuthenticationOptions? CreateTlsOptions() => _certificateContext is null
        ? null
        : new SslServerAuthenticationOptions
        {
            ServerCertificateContext = _certificateContext,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificateRequired = false,

            // Data connections resume the control connection's TLS session; FileZilla requires it.
            // SslStream reports nothing about whether a handshake was a resumption, so it is not logged.
            AllowTlsResume = true,
            CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
        };

    /// <summary>Takes a per user slot at login. Returns the reason when a limit refuses it, otherwise null.</summary>
    internal string? TryAcquireUserSlot(FtpUser user, IPAddress address)
    {
        address = NetUtil.Normalize(address);
        var perUserLimit = Lower(Options.MaxConnectionsPerUser, user.MaxConnections);
        var perIpLimit = user.MaxConnectionsPerIp ?? 0;
        lock (_limitsGate)
        {
            var count = _perUser.GetValueOrDefault(user.UserName);
            if (perUserLimit > 0 && count >= perUserLimit)
            {
                return "per user connection limit reached";
            }

            var key = (user.UserName, address);
            var fromAddress = _perUserIp.GetValueOrDefault(key);
            if (perIpLimit > 0 && fromAddress >= perIpLimit)
            {
                return "per user and address connection limit reached";
            }

            _perUser[user.UserName] = count + 1;
            _perUserIp[key] = fromAddress + 1;
            return null;
        }
    }

    /// <summary>The lower of two limits where 0 or null means unlimited; 0 when both are unlimited.</summary>
    private static int Lower(int global, int? own) => (global > 0, own > 0) switch
    {
        (true, true) => Math.Min(global, own!.Value),
        (true, false) => global,
        (false, true) => own!.Value,
        _ => 0,
    };

    /// <summary>Called once when a session logs in: it no longer counts as unauthenticated.</summary>
    internal void MarkAuthenticated(IPAddress address)
    {
        lock (_limitsGate)
        {
            Decrement(_unauthenticatedPerIp, NetUtil.Normalize(address));
        }
    }

    private static void Decrement(Dictionary<IPAddress, int> counts, IPAddress address)
    {
        if (counts.TryGetValue(address, out var count))
        {
            if (count <= 1)
            {
                counts.Remove(address);
            }
            else
            {
                counts[address] = count - 1;
            }
        }
    }

    private async Task AcceptLoopAsync(Socket listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                if (token.IsCancellationRequested || _stopping)
                {
                    break;
                }

                Publish(new ErrorEvent { SessionId = 0, Message = $"accept failed: {ex.SocketErrorCode}" });
                continue;
            }

            _ = Task.Run(() => HandleConnectionAsync(socket, token), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(Socket socket, CancellationToken token)
    {
        var id = Interlocked.Increment(ref _nextSessionId);
        IPEndPoint remote;
        try
        {
            remote = (IPEndPoint)socket.RemoteEndPoint!;
        }
        catch (SocketException)
        {
            socket.Dispose();
            return;
        }

        var address = NetUtil.Normalize(remote.Address);
        Publish(new ConnectedEvent { SessionId = id, RemoteEndPoint = remote });

        string? rejection = null;
        string? rejectionReply = null;
        if (Options.BanList.IsBanned(address))
        {
            rejection = "banned address";
            rejectionReply = "421 Access denied.\r\n";
        }
        else
        {
            lock (_limitsGate)
            {
                var perIp = _perIp.GetValueOrDefault(address);
                var unauthenticated = _unauthenticatedPerIp.GetValueOrDefault(address);
                if (Options.MaxConnections > 0 && _total >= Options.MaxConnections)
                {
                    rejection = "server connection limit reached";
                }
                else if (Options.MaxConnectionsPerIp > 0 && perIp >= Options.MaxConnectionsPerIp)
                {
                    rejection = "per address connection limit reached";
                }
                else if (Options.MaxUnauthenticatedPerIp > 0 && unauthenticated >= Options.MaxUnauthenticatedPerIp)
                {
                    rejection = "per address limit of connections not logged in reached";
                }
                else
                {
                    _total++;
                    _perIp[address] = perIp + 1;
                    _unauthenticatedPerIp[address] = unauthenticated + 1;
                }
            }

            rejectionReply = "421 Too many connections, try again later.\r\n";
        }

        if (rejection is not null)
        {
            await RejectAsync(socket, rejectionReply!).ConfigureAwait(false);
            Publish(new DisconnectedEvent { SessionId = id, RemoteEndPoint = remote, Reason = rejection });
            return;
        }

        FtpSession? session = null;
        try
        {
            socket.NoDelay = true;
            session = new FtpSession(this, id, socket, token);
            var run = session.RunAsync();
            _sessions[id] = new SessionEntry(session, run);
            await run.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Publish(new ErrorEvent { SessionId = id, RemoteEndPoint = remote, Message = $"session failed: {ex.Message}" });
            socket.Dispose();
        }
        finally
        {
            _sessions.TryRemove(id, out _);
            lock (_limitsGate)
            {
                _total--;
                Decrement(_perIp, address);
                if (session?.User is null)
                {
                    Decrement(_unauthenticatedPerIp, address);
                }

                if (session?.User is { } loggedIn)
                {
                    var key = (loggedIn.UserName, address);
                    if (_perUserIp.TryGetValue(key, out var fromAddress))
                    {
                        if (fromAddress <= 1)
                        {
                            _perUserIp.Remove(key);
                        }
                        else
                        {
                            _perUserIp[key] = fromAddress - 1;
                        }
                    }
                }

                if (session?.User is { } user && _perUser.TryGetValue(user.UserName, out var userCount))
                {
                    if (userCount <= 1)
                    {
                        _perUser.Remove(user.UserName);
                    }
                    else
                    {
                        _perUser[user.UserName] = userCount - 1;
                    }
                }
            }

            Publish(new DisconnectedEvent
            {
                SessionId = id,
                RemoteEndPoint = remote,
                UserName = session?.User?.UserName,
                Reason = session?.CloseReason ?? "closed",
            });
        }
    }

    private static async Task RejectAsync(Socket socket, string reply)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.SendAsync(Encoding.ASCII.GetBytes(reply), SocketFlags.None, timeout.Token).ConfigureAwait(false);
            socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The client is gone; nothing to do.
        }
        finally
        {
            socket.Dispose();
        }
    }

    private sealed record SessionEntry(FtpSession Session, Task Task);

    private sealed class UserAddressComparer : IEqualityComparer<(string User, IPAddress Address)>
    {
        public static UserAddressComparer Instance { get; } = new();

        public bool Equals((string User, IPAddress Address) x, (string User, IPAddress Address) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.User, y.User) && x.Address.Equals(y.Address);

        public int GetHashCode((string User, IPAddress Address) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.User), obj.Address);
    }
}

/// <summary>Names of the settings that <see cref="FtpServer.ApplyOptions"/> cannot change while the server runs.</summary>
public static class RestartSetting
{
    public const string BindAddress = "bind address";
    public const string Port = "port";
    public const string PassivePortRange = "passive port range";
    public const string TlsCertificate = "TLS certificate";
}

/// <summary>The control port is already taken, usually by another running server.</summary>
public sealed class PortInUseException(int port, Exception innerException)
    : IOException($"Port {port} is already in use.", innerException)
{
    public int Port { get; } = port;
}
