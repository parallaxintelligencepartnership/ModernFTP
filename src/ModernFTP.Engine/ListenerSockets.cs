using System.Net.Sockets;

namespace ModernFTP.Engine;

/// <summary>
/// Address reuse for listening sockets, decided per platform. .NET's ReuseAddress sets SO_REUSEADDR
/// plus SO_REUSEPORT on Unix, so it is never used here; Linux gets plain SO_REUSEADDR set raw.
/// </summary>
internal static class ListenerSockets
{
    internal const int LinuxSolSocket = 1;
    internal const int LinuxSoReuseAddr = 2;
    internal const int LinuxSoReusePort = 15;

    /// <summary>Call before Bind on every control and passive listener.</summary>
    public static void Configure(Socket socket)
    {
        if (OperatingSystem.IsLinux())
        {
            // SO_REUSEADDR alone never lets two sockets listen on one port on Linux; it only lets a
            // listener bind over connections in TIME_WAIT, so a restart or a recycled passive port works.
            // Set raw: .NET's ReuseAddress also sets SO_REUSEPORT on Linux, which lets a second
            // listener share the port (Drone build 15 proved it).
            socket.SetRawSocketOption(LinuxSolSocket, LinuxSoReuseAddr, BitConverter.GetBytes(1));
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
