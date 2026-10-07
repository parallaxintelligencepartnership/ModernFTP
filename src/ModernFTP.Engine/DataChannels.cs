using System.Net;
using System.Net.Sockets;

namespace ModernFTP.Engine;

internal sealed record DataConnection(Stream Stream, int LocalPort);

internal interface IDataChannel : IDisposable
{
    Task<DataConnection> OpenAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

internal static class NetUtil
{
    public static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

/// <summary>
/// Hands out passive listeners from the configured port range round robin: the search starts at the
/// port after the last one allocated and wraps, so a port ages through TIME_WAIT before it is reused.
/// A port is leased only by binding its listener inside the pool lock, and it goes back to the pool
/// only when that listener is disposed, so two live sessions can never share a passive port.
/// </summary>
internal sealed class PassivePortPool(int minPort, int maxPort)
{
    private readonly int _count = maxPort - minPort + 1;
    private readonly object _gate = new();
    private readonly HashSet<int> _leased = [];
    private int _last = -1;

    public PassiveDataChannel? TryOpen(IPAddress bindAddress, IPAddress expectedRemote)
    {
        bindAddress = NetUtil.Normalize(bindAddress);
        lock (_gate)
        {
            for (var i = 1; i <= _count; i++)
            {
                var index = (_last + i) % _count;
                var port = minPort + index;
                if (_leased.Contains(port))
                {
                    continue;
                }

                var socket = new Socket(bindAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    ListenerSockets.Configure(socket);
                    socket.Bind(new IPEndPoint(bindAddress, port));
                    socket.Listen(1);
                }
                catch (SocketException)
                {
                    socket.Dispose();
                    continue;
                }

                _leased.Add(port);
                _last = index;
                return new PassiveDataChannel(socket, port, expectedRemote, this);
            }
        }

        return null;
    }

    internal void Release(int port)
    {
        lock (_gate)
        {
            _leased.Remove(port);
        }
    }
}

internal sealed class PassiveDataChannel(Socket listener, int port, IPAddress expectedRemote, PassivePortPool pool) : IDataChannel
{
    private int _closed;

    public int Port => port;

    public async Task<DataConnection> OpenAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var socket = await listener.AcceptAsync(timeoutSource.Token).ConfigureAwait(false);
                var remote = NetUtil.Normalize(((IPEndPoint)socket.RemoteEndPoint!).Address);
                if (!remote.Equals(NetUtil.Normalize(expectedRemote)))
                {
                    // Data connection from a different host than the control connection: refuse it.
                    socket.Dispose();
                    continue;
                }

                CloseListener();
                return new DataConnection(new NetworkStream(socket, ownsSocket: true), port);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("No data connection was made in time.");
        }
    }

    public void Dispose() => CloseListener();

    /// <summary>Disposes the listener and only then returns its port to the pool.</summary>
    private void CloseListener()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            listener.Dispose();
            pool.Release(port);
        }
    }
}

internal sealed class ActiveDataChannel(IPEndPoint target) : IDataChannel
{
    public async Task<DataConnection> OpenAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(target, timeoutSource.Token).ConfigureAwait(false);
            return new DataConnection(new NetworkStream(socket, ownsSocket: true), ((IPEndPoint)socket.LocalEndPoint!).Port);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TimeoutException("Active data connection timed out.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
    }
}
