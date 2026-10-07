using System.Net.Sockets;

namespace ModernFTP.Engine;

/// <summary>
/// Address reuse for listening sockets, decided per platform. .NET maps ReuseAddress to SO_REUSEADDR
/// on Linux and Windows but to SO_REUSEADDR plus SO_REUSEPORT on macOS and BSD.
/// </summary>
internal static class ListenerSockets
{
    /// <summary>Call before Bind on every control and passive listener.</summary>
    public static void Configure(Socket socket)
    {
        if (OperatingSystem.IsLinux())
        {
            // SO_REUSEADDR alone never lets two sockets listen on one port on Linux; it only lets a
            // listener bind over connections in TIME_WAIT, so a restart or a recycled passive port works.
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }
        else if (OperatingSystem.IsWindows())
        {
            // Windows SO_REUSEADDR would let another socket take over an active listener; it stays off,
            // and exclusive use keeps other processes from binding over ours.
            socket.ExclusiveAddressUse = true;
        }

        // macOS and BSD: ReuseAddress would add SO_REUSEPORT and let two listeners share a port, and
        // ExclusiveAddressUse is not supported, so neither is set. The passive pool bookkeeping is the guard.
    }
}
