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

/// <summary>Hands out passive listeners from the configured port range, rotating the start point.</summary>
internal sealed class PassivePortPool(int minPort, int maxPort)
{
    private readonly int _count = maxPort - minPort + 1;
    private int _cursor = -1;

    public PassiveDataChannel? TryOpen(IPAddress bindAddress, IPAddress expectedRemote)
    {
        bindAddress = NetUtil.Normalize(bindAddress);
        var start = (int)((uint)Interlocked.Increment(ref _cursor) % (uint)_count);
        for (var i = 0; i < _count; i++)
        {
            var port = minPort + ((start + i) % _count);
            var socket = new Socket(bindAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    // Lets a port in TIME_WAIT from the previous transfer be reused right away.
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                }
                else
                {
                    socket.ExclusiveAddressUse = true;
                }

                socket.Bind(new IPEndPoint(bindAddress, port));
                socket.Listen(1);
                return new PassiveDataChannel(socket, port, expectedRemote);
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
        }

        return null;
    }
}

internal sealed class PassiveDataChannel(Socket listener, int port, IPAddress expectedRemote) : IDataChannel
{
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

                listener.Dispose();
                return new DataConnection(new NetworkStream(socket, ownsSocket: true), port);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("No data connection was made in time.");
        }
    }

    public void Dispose() => listener.Dispose();
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
